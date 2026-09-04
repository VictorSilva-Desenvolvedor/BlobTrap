using BlobTrap.Core.Util;

namespace BlobTrap.Core.Models;

public enum TrackKind
{
    /// <summary>Video and audio already interleaved - downloadable on its own.</summary>
    Muxed,
    VideoOnly,
    AudioOnly,
    Subtitle,
}

/// <summary>How a variant's bytes have to be fetched.</summary>
public enum DeliveryMode
{
    /// <summary>One HTTP GET (with Range resume) for a complete file.</summary>
    Progressive,

    /// <summary>An HLS media playlist whose segments are fetched and concatenated.</summary>
    HlsSegments,

    /// <summary>A DASH representation whose segments are fetched and concatenated.</summary>
    DashSegments,

    /// <summary>Handed to yt-dlp, which knows the site's own extraction rules.</summary>
    External,
}

/// <summary>One selectable quality or track inside a resolved media source.</summary>
public sealed class MediaVariant
{
    public required string Id { get; init; }
    public required Uri Url { get; init; }
    public required TrackKind Track { get; init; }
    public required DeliveryMode Delivery { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? Bandwidth { get; init; }
    public double? FrameRate { get; init; }
    public string? Codecs { get; init; }
    public string? Language { get; init; }
    public string? Name { get; init; }
    public double? DurationSeconds { get; init; }
    public long? ContentLength { get; init; }

    /// <summary>Container extension without the dot ("mp4", "webm", "m4a", "vtt").</summary>
    public string Container { get; init; } = "mp4";

    /// <summary>True when the stream has no fixed end (HLS live, DASH dynamic).</summary>
    public bool IsLive { get; init; }

    /// <summary>The HLS audio group this video variant expects, used to pair tracks.</summary>
    public string? AudioGroupId { get; init; }

    public string? SubtitleGroupId { get; init; }

    /// <summary>Format id as reported by an external extractor, when Delivery is External.</summary>
    public string? ExternalFormatId { get; init; }

    /// <summary>Resolver-specific payload handed to the downloader (parsed playlist, segment template, ...).</summary>
    public object? Payload { get; init; }

    /// <summary>Best-effort size when the server never reported one: bitrate x duration.</summary>
    public long? EstimatedBytes =>
        ContentLength ?? (Bandwidth is > 0 && DurationSeconds is > 0
            ? (long)(Bandwidth.Value / 8.0 * DurationSeconds.Value)
            : null);

    public string ResolutionLabel => Height switch
    {
        null => Width is not null ? $"{Width}px" : "SD",
        >= 4320 => "8K",
        >= 2160 => "4K",
        _ => $"{Height}p",
    };

    /// <summary>The label shown in the quality picker.</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>(4);

            switch (Track)
            {
                case TrackKind.AudioOnly:
                    parts.Add(string.IsNullOrWhiteSpace(Name) ? "Áudio" : Name!);
                    break;
                case TrackKind.Subtitle:
                    parts.Add("Legenda" + (Language is not null ? $" ({Language})" : string.Empty));
                    break;
                default:
                    parts.Add(ResolutionLabel);
                    if (FrameRate is > 31) parts.Add($"{FrameRate:0}fps");
                    break;
            }

            if (Track != TrackKind.Subtitle && Bandwidth is > 0)
                parts.Add(Naming.FormatBitrate(Bandwidth));

            var codec = FriendlyCodec();
            if (codec is not null) parts.Add(codec);

            if (Track == TrackKind.VideoOnly) parts.Add("sem áudio");
            if (IsLive) parts.Add("AO VIVO");

            return string.Join(" - ", parts);
        }
    }

    /// <summary>Maps an RFC 6381 codec string onto something a person recognises.</summary>
    public string? FriendlyCodec()
    {
        if (string.IsNullOrWhiteSpace(Codecs)) return null;
        var lower = Codecs!.ToLowerInvariant();

        if (Track == TrackKind.AudioOnly)
        {
            if (lower.Contains("opus")) return "Opus";
            if (lower.Contains("mp4a")) return "AAC";
            if (lower.Contains("ec-3")) return "E-AC3";
            if (lower.Contains("ac-3")) return "AC3";
            if (lower.Contains("mp3")) return "MP3";
            if (lower.Contains("flac")) return "FLAC";
            return null;
        }

        if (lower.Contains("av01")) return "AV1";
        if (lower.Contains("vp9") || lower.Contains("vp09")) return "VP9";
        if (lower.Contains("hvc1") || lower.Contains("hev1")) return "HEVC";
        if (lower.Contains("avc1") || lower.Contains("h264")) return "H.264";
        if (lower.Contains("vp8")) return "VP8";
        return null;
    }

    public string SizeLabel => Naming.FormatBytes(EstimatedBytes);

    /// <summary>Sort key so the picker lists the best quality first.</summary>
    public long QualityScore => (Height ?? 0) * 1_000_000L + Math.Min(Bandwidth ?? 0, 999_999);

    public override string ToString() => $"{Track}/{Delivery} {Label}";
}
