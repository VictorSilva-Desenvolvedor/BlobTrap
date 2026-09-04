using System.Text.Json;
using BlobTrap.Core.Net;
using BlobTrap.Core.Sniffing;
using Microsoft.Web.WebView2.Core;

namespace BlobTrap.App.Browser;

/// <summary>
/// Watches the page's DOM for &lt;video&gt; and &lt;audio&gt; elements and reports their sources.
///
/// This complements <see cref="CdpSniffer"/>: a video already in the HTTP cache, or one set
/// from JavaScript before the sniffer attached, produces no network event but is still sitting
/// right there in the DOM. Elements whose source is a blob: URL are reported too - not as
/// something downloadable, but so the UI can say "MSE stream, look for the manifest".
/// </summary>
public sealed class PageMediaProbe
{
    /// <summary>
    /// Injected once per document. Reports current sources, then keeps watching, because
    /// single-page players swap the source without ever navigating.
    /// </summary>
    private const string ProbeScript = """
        (function () {
          if (window.__blobTrapProbe) return;
          window.__blobTrapProbe = true;

          const seen = new Set();

          function report(el) {
            const src = el.currentSrc || el.src || '';
            if (!src || seen.has(src)) return;
            seen.add(src);

            window.chrome.webview.postMessage(JSON.stringify({
              kind: 'media-element',
              src: src,
              tag: el.tagName.toLowerCase(),
              duration: isFinite(el.duration) ? el.duration : null,
              width: el.videoWidth || null,
              height: el.videoHeight || null,
              poster: el.poster || null,
              title: document.title,
              page: location.href
            }));
          }

          function scanAll() {
            document.querySelectorAll('video, audio').forEach(el => {
              report(el);
              el.querySelectorAll('source').forEach(s => {
                if (s.src && !seen.has(s.src)) {
                  seen.add(s.src);
                  window.chrome.webview.postMessage(JSON.stringify({
                    kind: 'media-element',
                    src: s.src,
                    tag: 'source',
                    title: document.title,
                    page: location.href
                  }));
                }
              });
            });
          }

          document.addEventListener('loadedmetadata', e => {
            if (e.target && (e.target.tagName === 'VIDEO' || e.target.tagName === 'AUDIO')) report(e.target);
          }, true);

          new MutationObserver(scanAll).observe(document.documentElement, { childList: true, subtree: true });

          scanAll();
          setInterval(scanAll, 2000);
        })();
        """;

    private readonly MediaRegistry _registry;

    public PageMediaProbe(MediaRegistry registry) => _registry = registry;

    /// <summary>Reported when a player is backed by MSE, whose blob: URL cannot be fetched directly.</summary>
    public event EventHandler<string>? BlobSourceDetected;

    public async Task AttachAsync(CoreWebView2 webView)
    {
        await webView.AddScriptToExecuteOnDocumentCreatedAsync(ProbeScript);
        webView.WebMessageReceived += OnWebMessageReceived;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string payload;

        try
        {
            payload = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            // The page posted something that is not a string. Only our own injected script is
            // supposed to post here, but any script on the page can, so a non-string message
            // is another site's traffic rather than a fault of ours - dropping it is correct.
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (GetString(root, "kind") != "media-element") return;

            var src = GetString(root, "src");
            if (string.IsNullOrWhiteSpace(src)) return;

            if (src!.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                BlobSourceDetected?.Invoke(this, src);
                return;
            }

            if (!Uri.TryCreate(src, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme is not ("http" or "https")) return;

            var pageUrl = Uri.TryCreate(GetString(root, "page") ?? string.Empty, UriKind.Absolute, out var page) ? page : null;

            var context = RequestContext.Default with
            {
                Referer = pageUrl?.AbsoluteUri,
                Origin = pageUrl?.GetLeftPart(UriPartial.Authority),
            };

            _registry.Observe(uri, null, context, pageUrl, GetString(root, "title"));
        }
        catch (JsonException)
        {
            // Messages from the page are untrusted input; ignore anything malformed.
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
