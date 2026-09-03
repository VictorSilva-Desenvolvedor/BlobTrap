using System.Net;
using System.Net.Http.Headers;

namespace BlobTrap.Core.Net;

/// <summary>
/// The HTTP identity a media URL was first seen with. Most CDNs reject requests that
/// arrive without the original Referer / Origin / Cookie / User-Agent, so every request
/// the downloader makes replays this context.
/// </summary>
public sealed record RequestContext
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    public string UserAgent { get; init; } = DefaultUserAgent;
    public string? Referer { get; init; }
    public string? Origin { get; init; }
    public string? Cookie { get; init; }
    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static RequestContext Default { get; } = new();

    /// <summary>Builds a context from raw CDP request headers, keeping only what matters for replay.</summary>
    public static RequestContext FromHeaders(IReadOnlyDictionary<string, string> headers, Uri? pageUrl = null)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? ua = null, referer = null, origin = null, cookie = null;

        foreach (var (rawKey, value) in headers)
        {
            var key = rawKey.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase)) ua = value;
            else if (key.Equals("referer", StringComparison.OrdinalIgnoreCase)) referer = value;
            else if (key.Equals("origin", StringComparison.OrdinalIgnoreCase)) origin = value;
            else if (key.Equals("cookie", StringComparison.OrdinalIgnoreCase)) cookie = value;
            else if (IsReplayableHeader(key)) extra[key] = value;
        }

        if (referer is null && pageUrl is not null) referer = pageUrl.AbsoluteUri;
        if (origin is null && pageUrl is not null) origin = pageUrl.GetLeftPart(UriPartial.Authority);

        return new RequestContext
        {
            UserAgent = ua ?? DefaultUserAgent,
            Referer = referer,
            Origin = origin,
            Cookie = cookie,
            ExtraHeaders = extra,
        };
    }

    /// <summary>
    /// Headers that carry authorization or CDN-token state and must be replayed. Everything
    /// else (hop-by-hop, :pseudo, sec-fetch-*, content negotiation) is set by HttpClient itself.
    /// </summary>
    private static bool IsReplayableHeader(string key)
    {
        if (key.StartsWith(':')) return false;
        return key.ToLowerInvariant() switch
        {
            "authorization" or "x-forwarded-for" => true,
            _ => key.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
                 && !key.StartsWith("x-client-data", StringComparison.OrdinalIgnoreCase),
        };
    }

    public RequestContext WithCookie(string? cookie) => this with { Cookie = cookie };

    public RequestContext WithReferer(string? referer) => this with { Referer = referer };

    /// <summary>Applies this context to an outgoing request. Never throws on malformed values.</summary>
    public void ApplyTo(HttpRequestMessage request)
    {
        TryAdd(request, "User-Agent", UserAgent);
        TryAdd(request, "Referer", Referer);
        TryAdd(request, "Origin", Origin);
        TryAdd(request, "Cookie", Cookie);
        foreach (var (key, value) in ExtraHeaders) TryAdd(request, key, value);

        if (!request.Headers.Accept.Any())
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    private static void TryAdd(HttpRequestMessage request, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            // A malformed captured header is not worth failing the download over.
        }
    }

    /// <summary>Renders the context as yt-dlp / ffmpeg style "Key: value" lines.</summary>
    public IEnumerable<KeyValuePair<string, string>> EnumerateHeaders()
    {
        yield return new("User-Agent", UserAgent);
        if (!string.IsNullOrWhiteSpace(Referer)) yield return new("Referer", Referer!);
        if (!string.IsNullOrWhiteSpace(Origin)) yield return new("Origin", Origin!);
        if (!string.IsNullOrWhiteSpace(Cookie)) yield return new("Cookie", Cookie!);
        foreach (var (key, value) in ExtraHeaders) yield return new(key, value);
    }
}
