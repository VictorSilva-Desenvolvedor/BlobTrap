using System.Net;
using System.Net.Http.Headers;
using System.Text;

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

        return MediaProbe.From(rangedResponse);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
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
