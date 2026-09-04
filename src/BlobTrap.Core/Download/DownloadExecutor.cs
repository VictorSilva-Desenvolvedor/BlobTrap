using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Resolving;
using BlobTrap.Core.Tools;
using BlobTrap.Core.Util;

namespace BlobTrap.Core.Download;

/// <summary>
/// Runs one <see cref="DownloadPlan"/> end to end: fetch the tracks, merge them, write the
/// final file. Everything format-specific has already been decided by the resolvers, so this
/// only orchestrates.
/// </summary>
public sealed class DownloadExecutor
{
    private readonly MediaHttpClient _http;
    private readonly MediaResolver _resolver;

    public DownloadExecutor(MediaHttpClient http, MediaResolver resolver)
    {
        _http = http;
        _resolver = resolver;
    }

    /// <summary>Concurrent segment fetches per job.</summary>
    public int SegmentParallelism { get; set; } = 8;

    public async Task ExecuteAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var plan = job.Plan;

        if (plan.Source.IsProtected)
            throw new DrmProtectedException(plan.Source.ProtectionSystem ?? "DRM");

        var workDirectory = Path.Combine(ToolLocator.AppDataDirectory, "temp", job.Id);
        Directory.CreateDirectory(workDirectory);

        try
        {
            var warnings = new List<string>();

            if (plan.Video.Delivery == DeliveryMode.External)
            {
                await RunExternalAsync(plan, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var muxWarning = await RunNativeAsync(plan, workDirectory, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (muxWarning is not null) warnings.Add(muxWarning);
            }

            warnings.AddRange(await DownloadSubtitlesAsync(plan, cancellationToken).ConfigureAwait(false));
            if (warnings.Count > 0) job.Warnings = warnings;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task RunExternalAsync(DownloadPlan plan, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var ytDlp = _resolver.YtDlp
            ?? throw new InvalidOperationException("Esta mídia exige o yt-dlp, que não está instalado.");

        var selector = BuildFormatSelector(plan);
        var pageUrl = plan.Source.PageUrl ?? plan.Source.Url;

        var reporter = new Progress<YtDlpProgress>(p => progress.Report(new DownloadProgress
        {
            BytesReceived = p.BytesReceived,
            TotalBytes = p.TotalBytes,
            BytesPerSecond = p.BytesPerSecond,
            Stage = "Baixando",
        }));

        var produced = await ytDlp
            .DownloadAsync(pageUrl, selector, plan.OutputPath, plan.Request, reporter, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(produced, plan.OutputPath, StringComparison.OrdinalIgnoreCase))
            MoveOver(produced, plan.OutputPath);
    }

    /// <summary>yt-dlp merges tracks itself, so a paired selection becomes "video+audio".</summary>
    private static string? BuildFormatSelector(DownloadPlan plan)
    {
        var video = plan.Video.ExternalFormatId;
        var audio = plan.Audio?.ExternalFormatId;

        if (video is null) return null;
        if (plan.AudioOnly) return audio ?? "bestaudio/best";
        return audio is null || plan.Video.Track == TrackKind.Muxed ? video : $"{video}+{audio}";
    }

    private async Task<string?> RunNativeAsync(
        DownloadPlan plan,
        string workDirectory,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var totalEstimate = plan.EstimatedBytes;
        long completedBytes = 0;

        var videoPath = Path.Combine(workDirectory, "video." + TempExtension(plan.Video));

        var videoDuration = await DownloadTrackAsync(
            plan.Video, videoPath, plan.Request,
            "Baixando video", completedBytes, totalEstimate, progress, cancellationToken).ConfigureAwait(false);

        completedBytes = FileLength(videoPath);

        string? audioPath = null;
        if (plan.NeedsMerge && plan.Audio is not null)
        {
            audioPath = Path.Combine(workDirectory, "audio." + TempExtension(plan.Audio));

            await DownloadTrackAsync(
                plan.Audio, audioPath, plan.Request,
                "Baixando audio", completedBytes, totalEstimate, progress, cancellationToken).ConfigureAwait(false);
        }

        return await FinalizeAsync(
                plan, FfmpegRunner.TryCreate(), videoPath, audioPath, videoDuration, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Downloads one track and returns its duration in seconds when known.</summary>
    private async Task<double?> DownloadTrackAsync(
        MediaVariant variant,
        string outputPath,
        RequestContext context,
        string stage,
        long baseBytes,
        long? totalEstimate,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        switch (variant.Delivery)
        {
            case DeliveryMode.Progressive:
            {
                var downloader = new ProgressiveDownloader(_http);

                var reporter = new Progress<ProgressiveProgress>(p => progress.Report(new DownloadProgress
                {
                    BytesReceived = baseBytes + p.BytesReceived,
                    TotalBytes = totalEstimate ?? (p.TotalBytes is null ? null : baseBytes + p.TotalBytes),
                    BytesPerSecond = p.BytesPerSecond,
                    Stage = stage,
                }));

                await downloader.DownloadAsync(variant.Url, outputPath, context, reporter, cancellationToken).ConfigureAwait(false);
                return variant.DurationSeconds;
            }

            case DeliveryMode.HlsSegments:
            {
                var shape = await _resolver.Hls.BuildPartsAsync(variant, context, cancellationToken).ConfigureAwait(false);
                await DownloadPartsAsync(shape.Parts, outputPath, context, stage, baseBytes, totalEstimate, progress, cancellationToken)
                    .ConfigureAwait(false);
                return shape.DurationSeconds > 0 ? shape.DurationSeconds : variant.DurationSeconds;
            }

            case DeliveryMode.DashSegments:
            {
                var shape = _resolver.Dash.BuildParts(variant);
                await DownloadPartsAsync(shape.Parts, outputPath, context, stage, baseBytes, totalEstimate, progress, cancellationToken)
                    .ConfigureAwait(false);
                return shape.DurationSeconds > 0 ? shape.DurationSeconds : variant.DurationSeconds;
            }

            default:
                throw new NotSupportedException($"Modo de entrega nao suportado: {variant.Delivery}.");
        }
    }

    private async Task DownloadPartsAsync(
        IReadOnlyList<MediaPart> parts,
        string outputPath,
        RequestContext context,
        string stage,
        long baseBytes,
        long? totalEstimate,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var downloader = new SegmentDownloader(_http) { Parallelism = SegmentParallelism };

        var reporter = new Progress<SegmentProgress>(p => progress.Report(new DownloadProgress
        {
            BytesReceived = baseBytes + p.BytesReceived,
            TotalBytes = totalEstimate,
            SegmentsDone = p.Done,
            SegmentsTotal = p.Total,
            BytesPerSecond = p.BytesPerSecond,
            Stage = stage,
        }));

        await downloader.DownloadAsync(parts, outputPath, context, reporter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges, remuxes or simply moves the downloaded tracks into the final file. Returns a
    /// warning when the result is not what was asked for but is still worth keeping.
    /// </summary>
    /// <param name="ffmpeg">
    /// The muxer, or null when none is installed. Taken as a parameter rather than resolved
    /// here so the no-ffmpeg branch can be tested: discovering it internally would make the
    /// outcome depend on what happens to be on the machine, and this is the branch that
    /// decides whether the user keeps the gigabytes just downloaded or loses them.
    /// </param>
    internal static async Task<string?> FinalizeAsync(
        DownloadPlan plan,
        FfmpegRunner? ffmpeg,
        string videoPath,
        string? audioPath,
        double? duration,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var expected = duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : (TimeSpan?)null;

        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);

        if (ffmpeg is null)
        {
            // Without ffmpeg nothing can be merged or rewrapped, but the bytes are already
            // downloaded. Handing both tracks over side by side beats deleting them and
            // failing: the user keeps the work, and can merge later or reinstall and retry.
            if (audioPath is not null)
            {
                var stem = Path.Combine(
                    Path.GetDirectoryName(plan.OutputPath)!,
                    Path.GetFileNameWithoutExtension(plan.OutputPath));

                MoveOver(videoPath, Naming.EnsureUniquePath($"{stem} (video){Path.GetExtension(videoPath)}"));
                MoveOver(audioPath, Naming.EnsureUniquePath($"{stem} (audio){Path.GetExtension(audioPath)}"));

                return "Sem ffmpeg, o vídeo e o áudio foram salvos como dois arquivos separados.";
            }

            MoveOver(videoPath, ChangeExtension(plan.OutputPath, Path.GetExtension(videoPath)));
            return null;
        }

        var muxReporter = new Progress<MuxProgress>(p => progress.Report(new DownloadProgress
        {
            BytesReceived = 0,
            TotalBytes = null,
            Stage = "Finalizando",
            SegmentsDone = p.Fraction is null ? 0 : (int)(p.Fraction.Value * 100),
            SegmentsTotal = p.Fraction is null ? 0 : 100,
        }));

        progress.Report(new DownloadProgress { Stage = "Finalizando" });

        if (audioPath is not null)
        {
            await ffmpeg.MergeAsync(videoPath, audioPath, plan.OutputPath, expected, muxReporter, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (plan.AudioOnly)
        {
            await ffmpeg.ExtractAudioAsync(videoPath, plan.OutputPath, expected, muxReporter, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (plan.Video.Delivery == DeliveryMode.Progressive && SameContainer(videoPath, plan.OutputPath))
        {
            // A progressive file already in the right container needs no ffmpeg pass at all.
            MoveOver(videoPath, plan.OutputPath);
        }
        else
        {
            await ffmpeg.RemuxAsync(videoPath, plan.OutputPath, expected, muxReporter, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Fetches the selected subtitle tracks as sidecar files. Returns what failed: a broken
    /// subtitle must not sink a video that already downloaded, but it must not vanish either.
    /// </summary>
    private async Task<IReadOnlyList<string>> DownloadSubtitlesAsync(DownloadPlan plan, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (plan.Subtitles.Count == 0) return warnings;

        var directory = Path.GetDirectoryName(plan.OutputPath)!;
        var stem = Path.GetFileNameWithoutExtension(plan.OutputPath);

        foreach (var subtitle in plan.Subtitles)
        {
            var suffix = subtitle.Language ?? subtitle.Name ?? "sub";
            var path = Naming.EnsureUniquePath(
                Path.Combine(directory, $"{stem}.{Naming.SanitizeFileName(suffix, "sub", 20)}.{subtitle.Container}"));

            try
            {
                if (subtitle.Delivery == DeliveryMode.HlsSegments)
                {
                    var shape = await _resolver.Hls.BuildPartsAsync(subtitle, plan.Request, cancellationToken).ConfigureAwait(false);
                    var downloader = new SegmentDownloader(_http) { Parallelism = 4 };
                    await downloader.DownloadAsync(shape.Parts, path, plan.Request, null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var text = await _http.GetStringAsync(subtitle.Url, plan.Request, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                warnings.Add($"Legenda '{suffix}' falhou: {ex.Message}");
            }
        }

        return warnings;
    }

    private static string TempExtension(MediaVariant variant) => variant.Delivery switch
    {
        // Concatenated HLS TS segments are a raw transport stream until ffmpeg rewraps them.
        DeliveryMode.HlsSegments => variant.Container == "webm" ? "webm" : "ts",
        DeliveryMode.DashSegments => variant.Container,
        _ => string.IsNullOrWhiteSpace(variant.Container) ? "bin" : variant.Container,
    };

    private static bool SameContainer(string a, string b) =>
        Path.GetExtension(a).Equals(Path.GetExtension(b), StringComparison.OrdinalIgnoreCase);

    private static string ChangeExtension(string path, string extension) =>
        Naming.EnsureUniquePath(Path.ChangeExtension(path, extension));

    private static void MoveOver(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Atomic swap: never delete what is there until the replacement is in place.
        File.Move(source, destination, overwrite: true);
    }

    /// <summary>
    /// Size of a finished track, used only as the progress baseline for the next one. Returning
    /// 0 when the file cannot be measured understates the bar; it never affects the download.
    /// </summary>
    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the file is already downloaded, and a stale temp folder is reused
            // by id next run. Failing here would turn a finished download into an error.
        }
        catch (UnauthorizedAccessException)
        {
            // Same - cleanup is not worth losing a completed job over.
        }
    }
}
