using System.Collections.Concurrent;
using System.Security.Cryptography;
using BlobTrap.Core.Net;

namespace BlobTrap.Core.Download;

public sealed record SegmentProgress(int Done, int Total, long BytesReceived, double BytesPerSecond);

/// <summary>
/// Fetches an ordered list of <see cref="MediaPart"/> in parallel and concatenates them into
/// one file, decrypting AES-128 segments on the way. Downloads run concurrently but writes
/// stay strictly in order, since a stream's bytes are only valid in sequence.
/// </summary>
public sealed class SegmentDownloader
{
    private readonly MediaHttpClient _http;
    private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();

    public SegmentDownloader(MediaHttpClient http) => _http = http;

    /// <summary>How many parts may be in flight at once. Also bounds peak memory.</summary>
    public int Parallelism { get; set; } = 8;

    public async Task DownloadAsync(
        IReadOnlyList<MediaPart> parts,
        string outputPath,
        RequestContext context,
        IProgress<SegmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (parts.Count == 0) throw new InvalidOperationException("Stream sem segmentos para baixar.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var window = Math.Max(1, Parallelism);
        var meter = new SpeedMeter();
        var contentParts = parts.Count(p => !p.IsInitialization);
        var done = 0;

        await using var output = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1 << 20, useAsync: true);

        var inFlight = new Queue<Task<byte[]>>(window);
        var next = 0;

        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            while (next < parts.Count || inFlight.Count > 0)
            {
                while (inFlight.Count < window && next < parts.Count)
                {
                    inFlight.Enqueue(FetchPartAsync(parts[next], context, meter, failFast.Token));
                    next++;
                }

                var data = await inFlight.Dequeue().ConfigureAwait(false);
                await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);

                done++;
                progress?.Report(new SegmentProgress(
                    Math.Min(done, contentParts), contentParts, meter.TotalBytes, meter.BytesPerSecond));
            }
        }
        catch
        {
            // Stop the still-running fetches before unwinding, so they do not keep hitting the CDN.
            failFast.Cancel();
            await ObserveRemainingAsync(inFlight).ConfigureAwait(false);
            throw;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Awaits abandoned fetches and swallows their errors, so none surface as unobserved.</summary>
    private static async Task ObserveRemainingAsync(Queue<Task<byte[]>> inFlight)
    {
        while (inFlight.Count > 0)
        {
            try { await inFlight.Dequeue().ConfigureAwait(false); }
            catch { /* the original failure is the one that matters */ }
        }
    }

    private async Task<byte[]> FetchPartAsync(
        MediaPart part,
        RequestContext context,
        SpeedMeter meter,
        CancellationToken cancellationToken)
    {
        var bytes = await _http.GetBytesAsync(part.Uri, context, part.Range, cancellationToken).ConfigureAwait(false);
        meter.Add(bytes.LongLength);

        if (!part.IsEncrypted) return bytes;

        var key = await GetKeyAsync(part.KeyUri!, context, cancellationToken).ConfigureAwait(false);
        return Decrypt(bytes, key, part.Iv);
    }

    /// <summary>
    /// Busca a chave AES uma vez por URL, e não uma vez por segmento — uma playlist de duas
    /// mil partes com a mesma chave faria duas mil requisições idênticas.
    ///
    /// O cache guarda os bytes, não a <see cref="Task"/> que os buscou. Guardando a Task, ela
    /// ficava amarrada ao <see cref="CancellationToken"/> de quem chegou primeiro: se aquela
    /// primeira busca fosse cancelada, todo segmento seguinte reusava a Task cancelada e
    /// falhava por um motivo que já não existia mais.
    /// </summary>
    private async Task<byte[]> GetKeyAsync(Uri keyUri, RequestContext context, CancellationToken cancellationToken)
    {
        if (_keyCache.TryGetValue(keyUri.AbsoluteUri, out var cached)) return cached;

        var key = await _http.GetBytesAsync(keyUri, context, null, cancellationToken).ConfigureAwait(false);

        // Duas buscas simultâneas da mesma chave devolvem bytes iguais, então quem chegar
        // segundo pode simplesmente perder a corrida: o desperdício é uma requisição.
        return _keyCache.GetOrAdd(keyUri.AbsoluteUri, key);
    }

    /// <summary>HLS AES-128 is plain CBC with PKCS#7 padding over the whole segment.</summary>
    internal static byte[] Decrypt(byte[] data, byte[] key, byte[]? iv)
    {
        if (key.Length != 16)
            throw new CryptographicException($"Chave AES-128 inválida ({key.Length} bytes).");

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var vector = iv ?? new byte[16];
        if (vector.Length != 16) vector = vector.Length > 16 ? vector[..16] : vector.Concat(new byte[16 - vector.Length]).ToArray();

        try
        {
            return aes.DecryptCbc(data, vector, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            // Some packagers omit the final padding block; salvage what decrypts cleanly.
            return aes.DecryptCbc(data, vector, PaddingMode.None);
        }
    }
}
