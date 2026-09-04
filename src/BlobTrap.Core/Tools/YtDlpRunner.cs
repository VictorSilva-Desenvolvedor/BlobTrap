using System.Globalization;
using System.Text.Json;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;

namespace BlobTrap.Core.Tools;

public sealed record YtDlpProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond);

/// <summary>
/// Wraps yt-dlp. It is the fallback for pages whose media never appears as a plain manifest
/// (signature-protected streams, per-site APIs), and the fast path for sites it already knows.
/// </summary>
public sealed class YtDlpRunner
{
    private const string ProgressPrefix = "BTPROG";

    private readonly string _ytDlpPath;

    public YtDlpRunner(string ytDlpPath) => _ytDlpPath = ytDlpPath;

    public static YtDlpRunner? TryCreate()
    {
        var path = ToolLocator.Find(ExternalTool.YtDlp);
        return path is null ? null : new YtDlpRunner(path);
    }

    /// <summary>Asks yt-dlp what a page holds, without downloading anything.</summary>
    public async Task<MediaSource?> ProbeAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--dump-single-json",
            "--no-warnings",
            "--no-playlist",
            "--skip-download",
        };

        AddNetworkArguments(arguments, context);
        arguments.Add(url.AbsoluteUri);

        var result = await ProcessRunner.RunAsync(_ytDlpPath, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;

        try
        {
            return ParseProbe(result.StandardOutput, url, context);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MediaSource ParseProbe(string json, Uri url, RequestContext context)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var variants = new List<MediaVariant>();

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray())
            {
                var variant = ParseFormat(format);
                if (variant is not null) variants.Add(variant);
            }
        }

        // Some extractors return a single direct URL and no format list.
        if (variants.Count == 0 && GetString(root, "url") is { } directUrl && Uri.TryCreate(directUrl, UriKind.Absolute, out var direct))
        {
            variants.Add(new MediaVariant
            {
                Id = GetString(root, "format_id") ?? "best",
                Url = direct,
                Track = TrackKind.Muxed,
                Delivery = DeliveryMode.External,
                ExternalFormatId = GetString(root, "format_id") ?? "best",
                Container = GetString(root, "ext") ?? "mp4",
                DurationSeconds = GetDouble(root, "duration"),
            });
        }

        var duration = GetDouble(root, "duration");

        return new MediaSource
        {
            Id = GetString(root, "id") ?? Util.Naming.StableId(url.AbsoluteUri),
            Url = url,
            Kind = MediaKind.PageEmbed,
            Request = context,
            Title = GetString(root, "title") ?? Util.Naming.NameFromUrl(url),
            DurationSeconds = duration,
            PageUrl = Uri.TryCreate(GetString(root, "webpage_url") ?? url.AbsoluteUri, UriKind.Absolute, out var page) ? page : url,
            ThumbnailUrl = Uri.TryCreate(GetString(root, "thumbnail") ?? string.Empty, UriKind.Absolute, out var thumb) ? thumb : null,
            IsLive = GetBool(root, "is_live") ?? false,
            ResolvedBy = "yt-dlp",
            Variants = variants
                .Select(v => v.DurationSeconds is null && duration is not null ? WithDuration(v, duration) : v)
                .ToList(),
        };
    }

    private static MediaVariant WithDuration(MediaVariant variant, double? duration) => new()
    {
        Id = variant.Id,
        Url = variant.Url,
        Track = variant.Track,
        Delivery = variant.Delivery,
        Width = variant.Width,
        Height = variant.Height,
        Bandwidth = variant.Bandwidth,
        FrameRate = variant.FrameRate,
        Codecs = variant.Codecs,
        Language = variant.Language,
        Name = variant.Name,
        DurationSeconds = duration,
        ContentLength = variant.ContentLength,
        Container = variant.Container,
        IsLive = variant.IsLive,
        ExternalFormatId = variant.ExternalFormatId,
    };

    private static MediaVariant? ParseFormat(JsonElement format)
    {
        var formatId = GetString(format, "format_id");
        var urlText = GetString(format, "url");
        if (formatId is null || urlText is null) return null;
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)) return null;

        var vcodec = GetString(format, "vcodec");
        var acodec = GetString(format, "acodec");

        var hasVideo = vcodec is not null && !vcodec.Equals("none", StringComparison.OrdinalIgnoreCase);
        var hasAudio = acodec is not null && !acodec.Equals("none", StringComparison.OrdinalIgnoreCase);

        // Storyboard and thumbnail "formats" are not media.
        var protocol = GetString(format, "protocol") ?? string.Empty;
        if (!hasVideo && !hasAudio && !protocol.StartsWith("m3u8", StringComparison.OrdinalIgnoreCase)) return null;

        var track = (hasVideo, hasAudio) switch
        {
            (true, true) => TrackKind.Muxed,
            (true, false) => TrackKind.VideoOnly,
            (false, true) => TrackKind.AudioOnly,
            _ => TrackKind.Muxed,
        };

        // yt-dlp reports bitrates in kbps.
        var bitrateKbps = GetDouble(format, "tbr")
                          ?? (GetDouble(format, "vbr") ?? 0) + (GetDouble(format, "abr") ?? 0);

        var codecs = track switch
        {
            TrackKind.AudioOnly => acodec,
            TrackKind.VideoOnly => vcodec,
            _ => string.Join(", ", new[] { vcodec, acodec }.Where(c => c is not null)),
        };

        return new MediaVariant
        {
            Id = formatId,
            Url = uri,
            Track = track,
            Delivery = DeliveryMode.External,
            ExternalFormatId = formatId,
            Width = GetInt(format, "width"),
            Height = GetInt(format, "height"),
            Bandwidth = bitrateKbps > 0 ? (long)(bitrateKbps * 1000) : null,
            FrameRate = GetDouble(format, "fps"),
            Codecs = string.IsNullOrWhiteSpace(codecs) ? null : codecs,
            Language = GetString(format, "language"),
            Name = GetString(format, "format_note"),
            ContentLength = GetLong(format, "filesize") ?? GetLong(format, "filesize_approx"),
            Container = GetString(format, "ext") ?? "mp4",
        };
    }

    /// <summary>
    /// Runs the actual download. Returns the final file path, which yt-dlp may have changed
    /// (it picks the real container after merging).
    /// </summary>
    public async Task<string> DownloadAsync(
        Uri url,
        string? formatSelector,
        string outputPath,
        RequestContext context,
        IProgress<YtDlpProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);

        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath));
        var extension = Path.GetExtension(outputPath).TrimStart('.');

        var arguments = new List<string>
        {
            "--no-warnings",
            "--no-playlist",
            "--newline",
            "--no-simulate",
            "--progress",
            "--progress-template",
            $"download:{ProgressPrefix} %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress.speed)s",
            "--print", "after_move:filepath",
            "-o", stem + ".%(ext)s",
        };

        if (!string.IsNullOrWhiteSpace(formatSelector))
        {
            arguments.Add("-f");
            arguments.Add(formatSelector!);
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            arguments.Add("--merge-output-format");
            arguments.Add(extension);
        }

        if (ToolLocator.Find(ExternalTool.Ffmpeg) is { } ffmpeg)
        {
            arguments.Add("--ffmpeg-location");
            arguments.Add(ffmpeg);
        }

        AddNetworkArguments(arguments, context);
        arguments.Add(url.AbsoluteUri);

        string? finalPath = null;

        var result = await ProcessRunner.RunAsync(
            _ytDlpPath,
            arguments,
            onStandardOutput: line =>
            {
                if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                {
                    if (ParseProgressLine(line) is { } update) progress?.Report(update);
                    return;
                }

                // Anything else on stdout is the "--print after_move:filepath" result.
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('[')) finalPath = trimmed;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"yt-dlp falhou (codigo {result.ExitCode}).{Environment.NewLine}{result.StandardError.Trim()}");

        if (finalPath is not null && File.Exists(finalPath)) return finalPath;

        // Fall back to whatever landed next to the stem we asked for.
        var produced = Directory.GetFiles(directory, Path.GetFileName(stem) + ".*")
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

        return produced ?? throw new FileNotFoundException("yt-dlp terminou sem produzir um arquivo.", outputPath);
    }

    internal static YtDlpProgress? ParseProgressLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return null;

        var received = ParseNumber(parts[1]);
        if (received is null) return null;

        var total = ParseNumber(parts[2]) ?? ParseNumber(parts[3]);
        var speed = ParseNumber(parts[4]) ?? 0;

        return new YtDlpProgress((long)received.Value, total is null ? null : (long)total.Value, speed);
    }

    /// <summary>yt-dlp prints "NA" for values it does not know yet.</summary>
    private static double? ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>
    /// Replays the captured request identity onto yt-dlp's command line.
    ///
    /// The session Cookie goes through argv, which on Windows any process running as the same
    /// user can read (Win32_Process.CommandLine). This is a deliberate choice, not an
    /// oversight: the alternative, <c>--cookies</c>, wants a Netscape cookie jar, and building
    /// one from a raw Cookie header means inferring domain, path, secure and expiry - real bug
    /// surface - in exchange for moving the secret to a temp file instead. The trade is narrow,
    /// since an attacker already running as this user can read the browser's own cookie store.
    /// </summary>
    private static void AddNetworkArguments(List<string> arguments, RequestContext context)
    {
        arguments.Add("--user-agent");
        arguments.Add(context.UserAgent);

        if (!string.IsNullOrWhiteSpace(context.Referer))
        {
            arguments.Add("--referer");
            arguments.Add(context.Referer!);
        }

        foreach (var (key, value) in context.EnumerateHeaders())
        {
            if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) continue;

            arguments.Add("--add-header");
            arguments.Add($"{key}:{value}");
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(_ytDlpPath, new[] { "--version" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.Success ? result.StandardOutput.Trim() : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
}
