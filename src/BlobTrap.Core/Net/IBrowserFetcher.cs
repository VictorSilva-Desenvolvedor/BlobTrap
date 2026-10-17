namespace BlobTrap.Core.Net;

/// <summary>One response fetched by the browser on BlobTrap's behalf.</summary>
/// <param name="Status">HTTP status the browser got.</param>
/// <param name="ContentType">Content-Type header, when the server sent one.</param>
/// <param name="ContentRange">Content-Range header, present on a 206.</param>
/// <param name="Body">The whole body. Callers ask for bounded ranges, so this stays small.</param>
public sealed record BrowserFetchResult(int Status, string? ContentType, string? ContentRange, byte[] Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// Fetches a URL from inside the embedded browser, with its cookies, its network stack and the
/// frame that played the media as the requester.
///
/// The fallback for a 401/403 on a URL the browser itself just loaded: a signed link can be
/// tied to the browser session in ways no replayed header reproduces. This is how browser
/// extensions download - they never leave the browser - and why they get through where an
/// outside HTTP client does not. Core only knows this interface; the WebView2 side lives in App.
/// </summary>
public interface IBrowserFetcher
{
    /// <param name="url">What to fetch.</param>
    /// <param name="context">How the media was first requested; its Referer picks the frame.</param>
    /// <param name="range">A single "bytes=a-b" range, or null for the whole resource.</param>
    /// <returns>The browser's response, or null when the browser could not make the request at all.</returns>
    Task<BrowserFetchResult?> FetchAsync(Uri url, RequestContext context, string? range, CancellationToken cancellationToken);
}
