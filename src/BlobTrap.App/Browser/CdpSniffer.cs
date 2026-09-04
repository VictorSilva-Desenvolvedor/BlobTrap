using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BlobTrap.Core.Net;
using BlobTrap.Core.Sniffing;
using Microsoft.Web.WebView2.Core;

namespace BlobTrap.App.Browser;

/// <summary>
/// Watches the embedded browser's network through the Chrome DevTools Protocol and feeds
/// every media URL it sees into a <see cref="MediaRegistry"/>.
///
/// CDP is used instead of WebView2's own WebResourceRequested filter because only CDP reports
/// requests made by the media pipeline itself (MSE segment fetches), which is exactly what a
/// blob:-backed player does.
/// </summary>
public sealed class CdpSniffer : IDisposable
{
    /// <summary>Requests whose response has not arrived yet, keyed by CDP request id.</summary>
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();

    private readonly MediaRegistry _registry;
    private CoreWebView2? _webView;
    private bool _disposed;

    public CdpSniffer(MediaRegistry registry) => _registry = registry;

    public Uri? CurrentPageUrl { get; private set; }
    public string? CurrentPageTitle { get; private set; }

    /// <summary>Raised when something goes wrong attaching; the UI shows it as a status line.</summary>
    public event EventHandler<string>? Warning;

    public async Task AttachAsync(CoreWebView2 webView)
    {
        _webView = webView;

        try
        {
            await webView.CallDevToolsProtocolMethodAsync("Network.enable",
                """{"maxTotalBufferSize":10000000,"maxResourceBufferSize":5000000}""");

            Subscribe(webView, "Network.requestWillBeSent", OnRequestWillBeSent);
            Subscribe(webView, "Network.requestWillBeSentExtraInfo", OnRequestExtraInfo);
            Subscribe(webView, "Network.responseReceived", OnResponseReceived);
            Subscribe(webView, "Network.loadingFinished", OnLoadingFinished);
            Subscribe(webView, "Network.loadingFailed", OnLoadingFailed);
        }
        catch (Exception ex)
        {
            Warning?.Invoke(this, $"Não foi possível ativar o monitor de rede: {ex.Message}");
        }
    }

    private void Subscribe(CoreWebView2 webView, string eventName, Action<JsonElement> handler)
    {
        var receiver = webView.GetDevToolsProtocolEventReceiver(eventName);

        receiver.DevToolsProtocolEventReceived += (_, e) =>
        {
            if (_disposed) return;

            try
            {
                using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
                handler(document.RootElement);
            }
            catch (JsonException)
            {
                // A malformed CDP payload is not worth surfacing to the user.
            }
        };
    }

    public void UpdatePage(Uri? url, string? title)
    {
        CurrentPageUrl = url;
        CurrentPageTitle = title;
    }

    /// <summary>Forgets in-flight state. Called on navigation so ids from the old page cannot leak.</summary>
    public void ResetPageState() => _pending.Clear();

    private void OnRequestWillBeSent(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (requestId is null) return;

        if (!root.TryGetProperty("request", out var request)) return;

        var url = GetString(request, "url");
        if (url is null) return;

        var entry = _pending.GetOrAdd(requestId, static _ => new PendingRequest());
        entry.Url = url;
        entry.Headers = ReadHeaders(request, "headers");

        // Redirects reuse the request id; the latest URL is the one that matters.
        TrimPending();
    }

    /// <summary>
    /// The Cookie header only shows up in requestWillBeSentExtraInfo. Without it, replaying a
    /// request to an authenticated CDN gets a 403.
    /// </summary>
    private void OnRequestExtraInfo(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (requestId is null) return;

        var entry = _pending.GetOrAdd(requestId, static _ => new PendingRequest());
        entry.ExtraHeaders = ReadHeaders(root, "headers");
    }

    private void OnResponseReceived(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (requestId is null) return;
        if (!root.TryGetProperty("response", out var response)) return;

        var url = GetString(response, "url") ?? (_pending.TryGetValue(requestId, out var known) ? known.Url : null);
        if (url is null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        var mimeType = GetString(response, "mimeType");
        var responseHeaders = ReadHeaders(response, "headers");

        var contentLength = ReadContentLength(responseHeaders);

        _pending.TryGetValue(requestId, out var pending);

        var merged = MergeHeaders(pending);
        var context = RequestContext.FromHeaders(merged, CurrentPageUrl);

        // "type" e' o ResourceType do CDP. Quando vale "Media", e' o proprio Chrome dizendo que
        // entregou este corpo a um elemento de midia - a unica pista que sobra quando a CDN
        // serve sem extensao no caminho e sem Content-Type util.
        var resourceType = GetString(root, "type");

        _registry.Observe(uri, mimeType, context, CurrentPageUrl, CurrentPageTitle, contentLength, resourceType);

        if (pending is not null) pending.Observed = true;
    }

    private void OnLoadingFinished(JsonElement root)
    {
        var requestId = GetString(root, "requestId");
        if (requestId is not null) _pending.TryRemove(requestId, out _);
    }

    private void OnLoadingFailed(JsonElement root) => OnLoadingFinished(root);

    private static Dictionary<string, string> MergeHeaders(PendingRequest? pending)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pending is null) return merged;

        foreach (var (key, value) in pending.Headers) merged[key] = value;
        foreach (var (key, value) in pending.ExtraHeaders) merged[key] = value;

        return merged;
    }

    private static Dictionary<string, string> ReadHeaders(JsonElement parent, string propertyName)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
            return headers;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                headers[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return headers;
    }

    /// <summary>
    /// Tamanho do ATIVO que a resposta representa — não o tamanho desta resposta.
    ///
    /// A ordem importa e já esteve invertida. Numa 206 Partial Content, `Content-Length` é o
    /// tamanho do PEDAÇO e `Content-Range: bytes a-b/total` é o do arquivo. Lendo o
    /// `Content-Length` primeiro, um filme de 14 MB era registrado como 64 KB — e aí o filtro
    /// de "ocultar arquivos pequenos" descartava a mídia como se fosse um bumper de anúncio.
    ///
    /// Isso não era um caso de canto: todo player moderno busca mídia por range, então valia
    /// para YouTube, para um .mp4 de 355 MB no archive.org e para qualquer arquivo direto.
    /// A tela dizia "Nenhuma mídia detectada" com o vídeo tocando na frente do usuário.
    /// </summary>
    internal static long? ReadContentLength(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("content-range", out var range))
        {
            // "bytes a-b/total", ou "bytes a-b/*" quando o servidor ainda não sabe o total.
            var slash = range.LastIndexOf('/');
            if (slash >= 0 && long.TryParse(range[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
                return total;
        }

        if (headers.TryGetValue("content-length", out var raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            return length;

        return null;
    }

    /// <summary>A long-lived page can issue thousands of requests; keep the map from growing without bound.</summary>
    private void TrimPending()
    {
        if (_pending.Count <= 2000) return;

        foreach (var entry in _pending.Where(p => p.Value.Observed).Take(1000))
            _pending.TryRemove(entry.Key, out _);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        _disposed = true;
        _pending.Clear();
        _webView = null;
    }

    private sealed class PendingRequest
    {
        public string Url = string.Empty;
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ExtraHeaders = new(StringComparer.OrdinalIgnoreCase);
        public bool Observed;
    }
}
