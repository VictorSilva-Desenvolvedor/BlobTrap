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
    /// Headers the browser sent that must not be replayed: the ones HttpClient sets per
    /// request (framing, compression, range, conditionals) or that describe the browser's own
    /// cache. Everything else goes back out.
    ///
    /// This used to be an allow-list of Authorization plus x-*. A player that authenticates
    /// with any other name - a course platform's own token header, say - lost it on replay,
    /// and the CDN answered 403 to a request the browser had just made successfully.
    /// </summary>
    private static readonly HashSet<string> NotReplayed = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "keep-alive", "content-length", "transfer-encoding", "te", "trailer",
        "upgrade", "accept-encoding", "range", "if-range", "if-none-match", "if-modified-since",
        "if-match", "if-unmodified-since", "cache-control", "pragma", "upgrade-insecure-requests",
        "x-client-data", "proxy-connection",
    };

    private static bool IsReplayableHeader(string key) => !key.StartsWith(':') && !NotReplayed.Contains(key);

    /// <summary>
    /// Headers that authenticate rather than describe the request. yt-dlp applies every
    /// header it is given to every request it makes - the page, the site's API, the format it
    /// picked - so it gets only these, never the browser's fetch metadata for one media URL.
    /// </summary>
    public static bool IsAuthenticationHeader(string key) =>
        key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
        || key.Equals("x-forwarded-for", StringComparison.OrdinalIgnoreCase)
        || (key.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("x-client-data", StringComparison.OrdinalIgnoreCase));

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
