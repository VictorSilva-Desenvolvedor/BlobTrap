using BlobTrap.Core.Net;

namespace BlobTrap.Core.Download;

public sealed record ProgressiveProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond);

/// <summary>
/// Downloads one complete file over HTTP. Resumes from a partial file when the server
/// supports Range, and splits large files into parallel chunks when it does.
/// </summary>
public sealed class ProgressiveDownloader
{
    private const int BufferSize = 1 << 18;

    /// <summary>Below this size, splitting into chunks costs more than it saves.</summary>
    private const long ParallelThreshold = 24L * 1024 * 1024;

    private readonly MediaHttpClient _http;

    public ProgressiveDownloader(MediaHttpClient http) => _http = http;

    /// <summary>Chunks fetched at once for a large ranged download. 1 disables splitting.</summary>
    public int Parallelism { get; set; } = 4;

    public async Task DownloadAsync(
        Uri url,
        string outputPath,
        RequestContext context,
        IProgress<ProgressiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var probe = await TryProbeAsync(url, context, cancellationToken).ConfigureAwait(false);
        var total = probe?.ContentLength;
        var canRange = probe?.SupportsRanges == true && total is > 0;

        if (canRange && Parallelism > 1 && total >= ParallelThreshold)
        {
            await DownloadInChunksAsync(url, outputPath, context, total!.Value, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DownloadSequentialAsync(url, outputPath, context, total, canRange, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MediaProbe?> TryProbeAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        try { return await _http.ProbeAsync(url, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return null; }
    }

    private async Task DownloadSequentialAsync(
        Uri url,
        string outputPath,
        RequestContext context,
        long? total,
        bool canRange,
        IProgress<ProgressiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partPath = outputPath + ".part";
        long resumeFrom = 0;

        if (canRange && File.Exists(partPath))
        {
            var existing = new FileInfo(partPath).Length;
            // A .part at or past the full size is stale, not a resume point.
            if (existing > 0 && (total is null || existing < total)) resumeFrom = existing;
        }

        var meter = new SpeedMeter();
        var received = resumeFrom;

        using var response = await _http.OpenAsync(
            url, context, resumeFrom > 0 ? $"bytes={resumeFrom}-" : null, cancellationToken).ConfigureAwait(false);

        // The server ignored our Range: start over rather than corrupting the file.
        if (resumeFrom > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            resumeFrom = 0;
            received = 0;
        }

        response.EnsureSuccessStatusCode();
        total ??= response.Content.Headers.ContentLength is { } length ? resumeFrom + length : null;

        await using (var file = new FileStream(
            partPath,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, BufferSize, useAsync: true))
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[BufferSize];
            int read;

            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                received += read;
                meter.Add(read);
                progress?.Report(new ProgressiveProgress(received, total, meter.BytesPerSecond));
            }
        }

        MoveIntoPlace(partPath, outputPath);
    }

    private async Task DownloadInChunksAsync(
        Uri url,
        string outputPath,
        RequestContext context,
        long total,
        IProgress<ProgressiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partPath = outputPath + ".part";
        var workers = Math.Max(1, Parallelism);
        var chunkSize = (long)Math.Ceiling((double)total / workers);

        var meter = new SpeedMeter();

        // Preallocate so every worker can seek straight to its own slice.
        await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1, useAsync: true))
            file.SetLength(total);

        var reporter = progress is null
            ? null
            : new Timer(_ => progress.Report(new ProgressiveProgress(meter.TotalBytes, total, meter.BytesPerSecond)),
                        null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

        try
        {
            var tasks = new List<Task>(workers);

            for (var i = 0; i < workers; i++)
            {
                var start = i * chunkSize;
                if (start >= total) break;

                var end = Math.Min(start + chunkSize - 1, total - 1);
                tasks.Add(DownloadChunkAsync(url, partPath, context, start, end, meter, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            if (reporter is not null) await reporter.DisposeAsync().ConfigureAwait(false);
        }

        progress?.Report(new ProgressiveProgress(total, total, meter.BytesPerSecond));
        MoveIntoPlace(partPath, outputPath);
    }

    private async Task DownloadChunkAsync(
        Uri url,
        string partPath,
        RequestContext context,
        long start,
        long end,
        SpeedMeter meter,
        CancellationToken cancellationToken)
    {
        using var response = await _http.OpenAsync(url, context, $"bytes={start}-{end}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var file = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, BufferSize, useAsync: true);
        file.Seek(start, SeekOrigin.Begin);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            meter.Add(read);
        }
    }

    /// <summary>One atomic step, so a crash cannot destroy the old file without writing the new one.</summary>
    private static void MoveIntoPlace(string partPath, string outputPath) =>
        File.Move(partPath, outputPath, overwrite: true);
}
