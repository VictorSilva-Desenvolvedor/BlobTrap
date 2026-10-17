using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BlobTrap.Core.Diagnostics;

namespace BlobTrap.Core.Net;

/// <summary>
/// The single HTTP client every fetch goes through. Owns retry, redirect and decompression
/// policy so callers only think about URLs and <see cref="RequestContext"/>.
/// </summary>
public sealed class MediaHttpClient : IDisposable
{
    private readonly HttpClient _client;
    private bool _disposed;

    public MediaHttpClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            // Media CDNs shard aggressively; the default of 2 per server throttles segment fetches.
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // We replay captured Cookie headers verbatim, so the handler must not manage its own.
            UseCookies = false,
        };

        _client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(5),
        };
    }

    public int MaxRetries { get; set; } = 4;

    /// <summary>
    /// Fetches through the embedded browser when the server refuses this client (401/403) a
    /// URL captured from a page. Null disables the fallback.
    /// </summary>
    public IBrowserFetcher? BrowserFallback { get; set; }

    /// <summary>Hosts that refused this client but served the browser. Later requests skip the doomed direct try.</summary>
    private readonly ConcurrentDictionary<string, byte> _browserOnlyHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts already diagnosed in the log, so an HLS of 900 segments logs the cause once, not 900 times.</summary>
    private readonly ConcurrentDictionary<string, byte> _diagnosedHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fetches a text resource (manifest, playlist, subtitle track).</summary>
    public async Task<string> GetStringAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(url, context, null, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return DecodeText(bytes, response.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>Fetches a small binary resource in full (an AES key, an init segment).</summary>
    public async Task<byte[]> GetBytesAsync(Uri url, RequestContext context, string? range, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(url, context, range, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a streaming response. The caller owns the returned message.</summary>
    public Task<HttpResponseMessage> OpenAsync(Uri url, RequestContext context, string? range, CancellationToken cancellationToken) =>
        SendWithRetryAsync(url, context, range, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    /// <summary>
    /// Asks the server what it has without downloading it. Falls back to a ranged GET because
    /// plenty of media CDNs answer HEAD with 405.
    /// </summary>
    public async Task<MediaProbe> ProbeAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        if (CanUseBrowser(context) && _browserOnlyHosts.ContainsKey(url.Host)
            && await ProbeViaBrowserAsync(url, context, cancellationToken).ConfigureAwait(false) is { } known)
            return known;

        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            context.ApplyTo(head);

            using var response = await _client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return MediaProbe.From(response);
        }
        catch (HttpRequestException)
        {
            // Handled by falling through to the ranged GET below - plenty of media CDNs
            // answer HEAD with 405 or drop the connection outright.
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A HEAD that timed out, not the caller cancelling. Same fallback applies.
        }

        using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        context.ApplyTo(ranged);
        ranged.Headers.Range = new RangeHeaderValue(0, 0);

        using var rangedResponse = await _client.SendAsync(ranged, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (IsRefusal(rangedResponse.StatusCode) && CanUseBrowser(context))
        {
            var firstTime = NoteRefusal(url, rangedResponse.StatusCode, context);
            var viaBrowser = await ProbeViaBrowserAsync(url, context, cancellationToken).ConfigureAwait(false);
            NoteBrowserOutcome(url, viaBrowser is not null, firstTime);

            if (viaBrowser is not null) return viaBrowser;
        }

        return MediaProbe.From(rangedResponse);
    }

    private async Task<MediaProbe?> ProbeViaBrowserAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        var result = await BrowserFallback!.FetchAsync(url, context, "bytes=0-0", cancellationToken).ConfigureAwait(false);
        if (result is not { IsSuccess: true }) return null;

        return new MediaProbe
        {
            ContentLength = TotalFromContentRange(result.ContentRange) ?? (result.Status == 200 ? result.Body.Length : null),
            ContentType = result.ContentType,
            SupportsRanges = result.Status == (int)HttpStatusCode.PartialContent,
            StatusCode = (HttpStatusCode)result.Status,
        };
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Uri url,
        RequestContext context,
        string? range,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var streaming = completionOption == HttpCompletionOption.ResponseHeadersRead;

        if (CanUseBrowser(context) && _browserOnlyHosts.ContainsKey(url.Host)
            && await ViaBrowserAsync(url, context, range, streaming, cancellationToken).ConfigureAwait(false) is { } known)
            return known;

        var direct = await SendDirectAsync(url, context, range, completionOption, cancellationToken).ConfigureAwait(false);
        if (!IsRefusal(direct.StatusCode) || !CanUseBrowser(context)) return direct;

        // Refused a URL the browser loaded: ask the browser. The direct response stays the answer
        // when the browser is refused too, so the caller sees the server's own status.
        var firstTime = NoteRefusal(url, direct.StatusCode, context);

        var viaBrowser = await ViaBrowserAsync(url, context, range, streaming, cancellationToken).ConfigureAwait(false);
        NoteBrowserOutcome(url, viaBrowser is not null, firstTime);

        if (viaBrowser is null) return direct;

        direct.Dispose();
        return viaBrowser;
    }

    /// <summary>Logs the refusal once per host. Returns true the first time, so the outcome is logged with it.</summary>
    private bool NoteRefusal(Uri url, HttpStatusCode status, RequestContext context)
    {
        if (!_diagnosedHosts.TryAdd(url.Host, 0)) return false;

        Log.Warn("http", $"{(int)status} de {url.Host} para o cliente HTTP; enviado: {DescribeSent(context)}. Tentando pelo navegador.");
        return true;
    }

    private void NoteBrowserOutcome(Uri url, bool succeeded, bool firstTime)
    {
        if (!succeeded)
        {
            if (firstTime)
                Log.Warn("http", $"{url.Host}: o navegador também foi recusado. O link expirou ou a sessão não tem acesso - não é algo que o BlobTrap contorne.");
            return;
        }

        if (_browserOnlyHosts.TryAdd(url.Host, 0))
            Log.Info("http", $"{url.Host}: o navegador conseguiu. A recusa era do cliente HTTP - token preso à sessão do navegador " +
                             "ou header que não se reproduz -, e os próximos pedidos a este host vão direto pelo navegador.");
    }

    private async Task<HttpResponseMessage> SendDirectAsync(
        Uri url,
        RequestContext context,
        string? range,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                context.ApplyTo(request);
                if (!string.IsNullOrWhiteSpace(range)) request.Headers.TryAddWithoutValidation("Range", range);

                var response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);

                if (IsRetryable(response.StatusCode) && attempt < MaxRetries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta;
                    response.Dispose();
                    await DelayAsync(attempt, retryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                lastError = ex;
                await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                // A per-request timeout, not the caller cancelling.
                lastError = ex;
                await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException($"Falha ao buscar {url} apos {MaxRetries + 1} tentativas.", lastError);
    }

    /// <summary>
    /// Only a URL captured from a page goes to the browser: the Referer is what says the
    /// browser loaded it. A tool download (ffmpeg, yt-dlp from GitHub) carries none, and a 403
    /// there is a real answer, not a session problem.
    /// </summary>
    private bool CanUseBrowser(RequestContext context) =>
        BrowserFallback is not null && !string.IsNullOrWhiteSpace(context.Referer);

    private static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    /// <summary>Header names only, and whether a session went along - what tells the causes of a 403 apart.</summary>
    internal static string DescribeSent(RequestContext context)
    {
        var names = context.EnumerateHeaders()
            .Select(h => h.Key)
            .Where(n => !n.Equals("Cookie", StringComparison.OrdinalIgnoreCase));

        // "com/sem Cookie", never "Cookie: x": the log's redaction would read that as the header
        // itself and blank exactly the fact this line exists to record.
        return $"{string.Join(", ", names)}; {(string.IsNullOrEmpty(context.Cookie) ? "sem" : "com")} Cookie";
    }

    /// <summary>
    /// Builds the response from the browser's fetch. Whole-body reads (manifests, segments,
    /// keys) take one fetch; streams go through <see cref="BrowserRangeStream"/> in blocks.
    /// Null when the browser was refused too or could not ask.
    /// </summary>
    private async Task<HttpResponseMessage?> ViaBrowserAsync(
        Uri url, RequestContext context, string? range, bool streaming, CancellationToken cancellationToken)
    {
        var fetcher = BrowserFallback!;

        if (!streaming)
        {
            var whole = await fetcher.FetchAsync(url, context, range, cancellationToken).ConfigureAwait(false);
            return whole is { IsSuccess: true } ? ToResponse(whole) : null;
        }

        var (start, end) = ParseRange(range);
        var firstEnd = Math.Min(end ?? long.MaxValue, start + BrowserRangeStream.BlockSize - 1);

        var first = await fetcher.FetchAsync(url, context, $"bytes={start}-{firstEnd}", cancellationToken).ConfigureAwait(false);
        if (first is not { IsSuccess: true }) return null;

        // The server ignored the range and sent everything: that is the whole answer.
        if (first.Status != (int)HttpStatusCode.PartialContent) return ToResponse(first);

        var total = TotalFromContentRange(first.ContentRange);
        var last = end ?? (total is { } t ? t - 1 : null);

        // A first block shorter than asked is the whole thing.
        if (first.Body.Length < firstEnd - start + 1 && (last is null || start + first.Body.Length > last))
            return ToResponse(first);

        // No total: the CDN did not expose Content-Range to the page through CORS. The stream
        // then reads until the file runs out, and the length goes unannounced.
        var stream = new BrowserRangeStream(
            fetcher, url, context, first.Body, start + first.Body.Length, last ?? BrowserRangeStream.UnknownEnd);
        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(stream),
        };

        if (last is not null) response.Content.Headers.ContentLength = last.Value - start + 1;
        if (first.ContentType is { } type && MediaTypeHeaderValue.TryParse(type, out var parsed))
            response.Content.Headers.ContentType = parsed;

        return response;
    }

    private static HttpResponseMessage ToResponse(BrowserFetchResult result)
    {
        var response = new HttpResponseMessage((HttpStatusCode)result.Status) { Content = new ByteArrayContent(result.Body) };

        if (result.ContentType is { } type && MediaTypeHeaderValue.TryParse(type, out var parsed))
            response.Content.Headers.ContentType = parsed;
        if (result.ContentRange is { } range && ContentRangeHeaderValue.TryParse(range, out var contentRange))
            response.Content.Headers.ContentRange = contentRange;

        return response;
    }

    /// <summary>"bytes=a-b" or "bytes=a-" into offsets; anything else means the whole resource.</summary>
    internal static (long Start, long? End) ParseRange(string? range)
    {
        if (range is null || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return (0, null);

        var parts = range[6..].Split('-', 2);
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) return (0, null);

        long? end = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : null;
        return (start, end);
    }

    /// <summary>The total from "bytes a-b/total"; null for "bytes a-b/*" or a missing header.</summary>
    internal static long? TotalFromContentRange(string? contentRange)
    {
        if (contentRange is null) return null;

        var slash = contentRange.LastIndexOf('/');
        return slash >= 0 && long.TryParse(contentRange[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            ? total
            : null;
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        if (retryAfter is { } delay && delay > TimeSpan.Zero)
            return Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, cancellationToken);

        var backoff = TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        return Task.Delay(backoff + jitter, cancellationToken);
    }

    /// <summary>Honours the declared charset, falling back to UTF-8 (BOM aware).</summary>
    private static string DecodeText(byte[] bytes, string? charSet)
    {
        if (!string.IsNullOrWhiteSpace(charSet))
        {
            try
            {
                return Encoding.GetEncoding(charSet!.Trim('"')).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // The server named a charset this platform does not know; handled by the
                // UTF-8 fallback below, which is right for essentially every manifest.
            }
        }

        return new UTF8Encoding(false, false).GetString(bytes).TrimStart('﻿');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}

/// <summary>What a HEAD (or ranged GET) told us about a URL.</summary>
public sealed record MediaProbe
{
    public long? ContentLength { get; init; }
    public string? ContentType { get; init; }
    public bool SupportsRanges { get; init; }
    public HttpStatusCode StatusCode { get; init; }

    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    public static MediaProbe From(HttpResponseMessage response)
    {
        // With a "bytes=0-0" probe the body length is 1, so the real size comes from Content-Range.
        long? length = response.Content.Headers.ContentLength;
        if (response.Content.Headers.ContentRange?.Length is { } total) length = total;

        return new MediaProbe
        {
            ContentLength = length,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            SupportsRanges = response.Headers.AcceptRanges.Contains("bytes")
                             || response.Content.Headers.ContentRange is not null,
            StatusCode = response.StatusCode,
        };
    }
}
