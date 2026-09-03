namespace BlobTrap.Core.Hls;

public abstract class HlsPlaylist
{
    public required Uri BaseUri { get; init; }
    public int Version { get; set; } = 1;
}

/// <summary>A master playlist: a menu of variant streams plus their alternate renditions.</summary>
public sealed class HlsMasterPlaylist : HlsPlaylist
{
    public List<HlsVariantStream> Variants { get; } = new();
    public List<HlsRendition> Renditions { get; } = new();
    public List<HlsKey> SessionKeys { get; } = new();

    public IEnumerable<HlsRendition> AudioRenditions =>
        Renditions.Where(r => r.Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<HlsRendition> SubtitleRenditions =>
        Renditions.Where(r => r.Type.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase));
}

public sealed class HlsVariantStream
{
    public required Uri Uri { get; init; }
    public long? Bandwidth { get; init; }
    public long? AverageBandwidth { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Codecs { get; init; }
    public double? FrameRate { get; init; }
    public string? VideoRange { get; init; }
    public string? AudioGroupId { get; init; }
    public string? SubtitlesGroupId { get; init; }

    /// <summary>True for I-frame-only trick-play variants, which are never worth downloading.</summary>
    public bool IsIFrameOnly { get; init; }

    /// <summary>
    /// A variant that declares an AUDIO group carries video only - its audio lives in a
    /// separate rendition and has to be merged after download.
    /// </summary>
    public bool IsVideoOnly => AudioGroupId is not null;
}

public sealed class HlsRendition
{
    public required string Type { get; init; }
    public required string GroupId { get; init; }
    public string? Name { get; init; }
    public string? Language { get; init; }
    public bool IsDefault { get; init; }
    public bool AutoSelect { get; init; }
    public bool Forced { get; init; }
    public Uri? Uri { get; init; }
    public string? Channels { get; init; }
    public string? Characteristics { get; init; }
}

/// <summary>A media playlist: the actual list of segments for one stream.</summary>
public sealed class HlsMediaPlaylist : HlsPlaylist
{
    public double TargetDuration { get; set; }
    public long MediaSequence { get; set; }
    public bool HasEndList { get; set; }
    public bool IsIFrameOnly { get; set; }
    public string? PlaylistType { get; set; }
    public List<HlsSegment> Segments { get; } = new();

    /// <summary>No EXT-X-ENDLIST means the server is still appending segments.</summary>
    public bool IsLive => !HasEndList;

    public double TotalDuration => Segments.Sum(s => s.Duration);

    public IEnumerable<HlsKey> DistinctKeys =>
        Segments.Select(s => s.Key)
                .Where(k => k is not null)
                .Select(k => k!)
                .DistinctBy(k => (k.Method, k.Uri?.AbsoluteUri));
}

public sealed class HlsSegment
{
    public required Uri Uri { get; init; }
    public double Duration { get; init; }
    public string? Title { get; init; }
    public long MediaSequence { get; init; }
    public bool Discontinuity { get; init; }

    /// <summary>Set when the segment is a byte range inside a larger file.</summary>
    public long? ByteRangeOffset { get; init; }

    public long? ByteRangeLength { get; init; }

    public HlsKey? Key { get; init; }

    /// <summary>The fMP4 initialization segment this segment needs, from EXT-X-MAP.</summary>
    public HlsInitSegment? Map { get; init; }

    public string? RangeHeader =>
        ByteRangeLength is null || ByteRangeOffset is null
            ? null
            : $"bytes={ByteRangeOffset}-{ByteRangeOffset + ByteRangeLength - 1}";
}

public sealed class HlsInitSegment
{
    public required Uri Uri { get; init; }
    public long? ByteRangeOffset { get; init; }
    public long? ByteRangeLength { get; init; }

    public string? RangeHeader =>
        ByteRangeLength is null || ByteRangeOffset is null
            ? null
            : $"bytes={ByteRangeOffset}-{ByteRangeOffset + ByteRangeLength - 1}";
}

public sealed class HlsKey
{
    public required string Method { get; init; }
    public Uri? Uri { get; init; }
    public byte[]? Iv { get; init; }
    public string? KeyFormat { get; init; }
    public string? KeyFormatVersions { get; init; }

    public bool IsNone => Method.Equals("NONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>AES-128 and SAMPLE-AES with the identity key format are plain segment encryption.</summary>
    public bool IsAes128 => Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the key is delivered by a DRM system (Widevine, PlayReady, FairPlay).
    /// BlobTrap reports these and stops - decrypting them is not something it does.
    /// </summary>
    public bool IsDrm
    {
        get
        {
            if (IsNone || IsAes128) return false;
            if (KeyFormat is null) return !Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase);

            var format = KeyFormat.ToLowerInvariant();
            return format.Contains("widevine")
                || format.Contains("playready")
                || format.Contains("streamingkeydelivery")
                || format.Contains("fairplay")
                || format.StartsWith("urn:uuid:");
        }
    }

    public string? DrmName
    {
        get
        {
            if (!IsDrm) return null;
            var format = KeyFormat?.ToLowerInvariant() ?? Method.ToLowerInvariant();
            if (format.Contains("widevine") || format.Contains("edef8ba9")) return "Widevine";
            if (format.Contains("playready") || format.Contains("9a04f079")) return "PlayReady";
            if (format.Contains("streamingkeydelivery") || format.Contains("fairplay")) return "FairPlay";
            return "DRM";
        }
    }
}
