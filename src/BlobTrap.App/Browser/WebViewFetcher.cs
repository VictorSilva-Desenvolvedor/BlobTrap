using System.Text.Json;
using System.Windows.Threading;
using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Net;
using Microsoft.Web.WebView2.Core;

namespace BlobTrap.App.Browser;

/// <summary>
/// Fetches through the embedded browser: a <c>fetch()</c> run inside the frame that played the
/// media, in an isolated world the page's own scripts cannot see or tamper with.
///
/// Running in that frame is the point. The player there already fetched these URLs, so the
/// same request from the same origin carries the same cookies, Referer and network stack, and
/// CORS lets it through for the same reason it let the player through. It is how a browser
/// extension downloads, and why one gets past a 403 that an outside HTTP client cannot.
/// </summary>
public sealed class WebViewFetcher : IBrowserFetcher
{
    private const string WorldName = "blobtrap-fetch";

    private readonly CoreWebView2 _core;
    private readonly Dispatcher _dispatcher;

    public WebViewFetcher(CoreWebView2 core, Dispatcher dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
    }

    public Task<BrowserFetchResult?> FetchAsync(Uri url, RequestContext context, string? range, CancellationToken cancellationToken) =>
        // WebView2 only takes calls on the thread that created it; downloads run on the pool.
        _dispatcher.InvokeAsync(() => FetchOnUiThreadAsync(url, context, range), DispatcherPriority.Background, cancellationToken)
            .Task.Unwrap();

    private async Task<BrowserFetchResult?> FetchOnUiThreadAsync(Uri url, RequestContext context, string? range)
    {
        try
        {
            var frameId = PickFrame(await ListFramesAsync(), context.Referer, url);

            var world = await _core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                JsonSerializer.Serialize(new { frameId, worldName = WorldName }));
            var contextId = JsonDocument.Parse(world).RootElement.GetProperty("executionContextId").GetInt32();

            var evaluated = await _core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new
                {
                    expression = Script(url, range),
                    contextId,
                    awaitPromise = true,
                    returnByValue = true,
                }));

            return Parse(evaluated, url);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Null tells the caller the browser could not ask at all; the server's own 403
            // then stays the answer, so nothing is hidden by swallowing this.
            Log.Warn("browser-fetch", $"o navegador não conseguiu pedir {Redaction.ScrubUrl(url)}", ex);
            return null;
        }
    }

    private async Task<IReadOnlyList<(string Id, string Url)>> ListFramesAsync()
    {
        var tree = await _core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}");
        var frames = new List<(string, string)>();

        void Walk(JsonElement node)
        {
            var frame = node.GetProperty("frame");
            frames.Add((frame.GetProperty("id").GetString()!, frame.GetProperty("url").GetString() ?? string.Empty));

            if (node.TryGetProperty("childFrames", out var children))
                foreach (var child in children.EnumerateArray()) Walk(child);
        }

        Walk(JsonDocument.Parse(tree).RootElement.GetProperty("frameTree"));
        return frames;
    }

    /// <summary>
    /// The frame most likely to be the player: the one whose URL is the Referer the media was
    /// requested with, then one on the Referer's origin, then one on the media's origin, then
    /// the top page. The first entry of <paramref name="frames"/> is the top page.
    /// </summary>
    internal static string PickFrame(IReadOnlyList<(string Id, string Url)> frames, string? referer, Uri media)
    {
        static string? Origin(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;

        static string WithoutFragment(string value) => value.Split('#')[0];

        if (referer is not null)
        {
            var exact = frames.FirstOrDefault(f => WithoutFragment(f.Url) == WithoutFragment(referer));
            if (exact.Id is not null) return exact.Id;

            var refererOrigin = Origin(referer);
            var sameOrigin = frames.LastOrDefault(f => refererOrigin is not null && Origin(f.Url) == refererOrigin);
            if (sameOrigin.Id is not null) return sameOrigin.Id;
        }

        var mediaOrigin = media.GetLeftPart(UriPartial.Authority);
        var mediaFrame = frames.LastOrDefault(f => Origin(f.Url) == mediaOrigin);

        return mediaFrame.Id ?? frames[0].Id;
    }

    /// <summary>
    /// The body comes back as base64 in chunks of 32 KB: <c>String.fromCharCode</c> over a
    /// whole 8 MB block would blow the argument limit. Any failure, including CORS, is reported
    /// as an error instead of thrown, so the caller gets a status instead of a broken promise.
    /// </summary>
    internal static string Script(Uri url, string? range) => $$"""
        (async () => {
          try {
            const headers = {};
            const range = {{JsonSerializer.Serialize(range)}};
            if (range) headers.Range = range;
            const r = await fetch({{JsonSerializer.Serialize(url.AbsoluteUri)}}, { credentials: 'include', cache: 'no-store', headers });
            const bytes = new Uint8Array(await r.arrayBuffer());
            let binary = '';
            for (let i = 0; i < bytes.length; i += 0x8000)
              binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
            return JSON.stringify({ status: r.status, type: r.headers.get('content-type'),
                                    range: r.headers.get('content-range'), body: btoa(binary) });
          } catch (e) {
            return JSON.stringify({ error: String(e) });
          }
        })()
        """;

    internal static BrowserFetchResult? Parse(string evaluateResponse, Uri url)
    {
        using var outer = JsonDocument.Parse(evaluateResponse);
        var result = outer.RootElement.GetProperty("result");

        if (outer.RootElement.TryGetProperty("exceptionDetails", out _) || result.GetProperty("type").GetString() != "string")
            return null;

        using var inner = JsonDocument.Parse(result.GetProperty("value").GetString()!);
        var root = inner.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            Log.Warn("browser-fetch", $"fetch no navegador falhou para {Redaction.ScrubUrl(url)}: {Redaction.Scrub(error.GetString())}");
            return null;
        }

        return new BrowserFetchResult(
            root.GetProperty("status").GetInt32(),
            root.GetProperty("type").GetString(),
            root.GetProperty("range").GetString(),
            Convert.FromBase64String(root.GetProperty("body").GetString() ?? string.Empty));
    }
}
