using System.Globalization;
using System.Text.RegularExpressions;

namespace BlobTrap.Core.Tools;

public sealed record MuxProgress(TimeSpan Position, double? Fraction);

/// <summary>
/// Drives ffmpeg for the two jobs BlobTrap needs: turning a concatenated segment stream into
/// a real container, and merging separate video and audio tracks. Both use stream copy, so
/// they are I/O bound and lossless - no re-encoding unless the caller asks for it.
/// </summary>
public sealed partial class FfmpegRunner
{
    private readonly string _ffmpegPath;

    public FfmpegRunner(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>Creates a runner from the installed ffmpeg, or null when none is available.</summary>
    public static FfmpegRunner? TryCreate()
    {
        var path = ToolLocator.Find(ExternalTool.Ffmpeg);
        return path is null ? null : new FfmpegRunner(path);
    }

    /// <summary>Rewraps a raw stream (concatenated TS or fMP4) into a clean container.</summary>
    public Task RemuxAsync(
        string inputPath,
        string outputPath,
        TimeSpan? expectedDuration,
        IProgress<MuxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", inputPath,
            "-c", "copy",
            // Timestamps out of a live segment stream often start far from zero.
            "-fflags", "+genpts",
            "-avoid_negative_ts", "make_zero",
        };

        AddFastStartIfMp4(arguments, outputPath);
        arguments.Add(outputPath);

        return RunAsync(arguments, expectedDuration, progress, cancellationToken);
    }

    /// <summary>Merges a video-only and an audio-only file into a single container.</summary>
    public Task MergeAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        TimeSpan? expectedDuration,
        IProgress<MuxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", videoPath,
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-c", "copy",
        };

        // MP4 cannot hold Opus; transcoding just the audio keeps the video untouched.
        if (IsMp4(outputPath))
        {
            arguments.Add("-c:a");
            arguments.Add("aac");
            arguments.Add("-b:a");
            arguments.Add("192k");
        }

        arguments.Add("-shortest");
        AddFastStartIfMp4(arguments, outputPath);
        arguments.Add(outputPath);

        return RunAsync(arguments, expectedDuration, progress, cancellationToken);
    }

    /// <summary>Strips the video stream, keeping (or encoding) audio only.</summary>
    public Task ExtractAudioAsync(
        string inputPath,
        string outputPath,
        TimeSpan? expectedDuration,
        IProgress<MuxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", inputPath,
            "-vn",
        };

        if (Path.GetExtension(outputPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(new[] { "-c:a", "libmp3lame", "-q:a", "2" });
        }
        else
        {
            arguments.AddRange(new[] { "-c:a", "copy" });
        }

        arguments.Add(outputPath);
        return RunAsync(arguments, expectedDuration, progress, cancellationToken);
    }

    private static void AddFastStartIfMp4(List<string> arguments, string outputPath)
    {
        if (!IsMp4(outputPath)) return;
        arguments.Add("-movflags");
        arguments.Add("+faststart");
    }

    private static bool IsMp4(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".m4a" or ".mov";

    private async Task RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? expectedDuration,
        IProgress<MuxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = expectedDuration;

        var result = await ProcessRunner.RunAsync(
            _ffmpegPath,
            arguments,
            onStandardError: line =>
            {
                // ffmpeg reports the input duration before it starts, which beats our estimate.
                if (total is null && DurationPattern().Match(line) is { Success: true } durationMatch)
                    total = ParseTimestamp(durationMatch.Groups[1].Value);

                if (progress is null) return;
                if (TimePattern().Match(line) is not { Success: true } match) return;

                var position = ParseTimestamp(match.Groups[1].Value);
                if (position is null) return;

                var fraction = total is { TotalSeconds: > 0 }
                    ? Math.Clamp(position.Value.TotalSeconds / total.Value.TotalSeconds, 0, 1)
                    : (double?)null;

                progress.Report(new MuxProgress(position.Value, fraction));
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"ffmpeg falhou (codigo {result.ExitCode}).{Environment.NewLine}{Tail(result.StandardError)}");
    }

    private static string Tail(string text, int lines = 12)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, all.TakeLast(lines).Select(l => l.TrimEnd()));
    }

    internal static TimeSpan? ParseTimestamp(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3) return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return null;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return null;

        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    [GeneratedRegex(@"time=(\d+:\d{2}:\d{2}\.\d+)")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"Duration:\s*(\d+:\d{2}:\d{2}\.\d+)")]
    private static partial Regex DurationPattern();
}
