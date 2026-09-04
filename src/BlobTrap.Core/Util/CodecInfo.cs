namespace BlobTrap.Core.Util;

/// <summary>
/// Reads an RFC 6381 CODECS string to tell which media a track actually carries.
///
/// This matters because a master playlist happily lists an audio-only rendition as an
/// EXT-X-STREAM-INF alongside the real video variants. Treating it as a video quality offers
/// the user a "video" that turns out to be a bare AAC stream.
/// </summary>
public static class CodecInfo
{
    private static readonly string[] VideoPrefixes =
    {
        "avc1", "avc3", "hvc1", "hev1", "vp8", "vp9", "vp09", "av01", "dvh1", "dvhe", "mp4v", "theora",
    };

    private static readonly string[] AudioPrefixes =
    {
        "mp4a", "ac-3", "ec-3", "opus", "vorbis", "mp3", "alac", "flac", "dtsc", "dtse", "ac-4",
    };

    public static bool HasVideo(string? codecs) => Matches(codecs, VideoPrefixes);

    public static bool HasAudio(string? codecs) => Matches(codecs, AudioPrefixes);

    /// <summary>True only when the string names codecs and none of them are video.</summary>
    public static bool IsAudioOnly(string? codecs) =>
        !string.IsNullOrWhiteSpace(codecs) && HasAudio(codecs) && !HasVideo(codecs);

    private static bool Matches(string? codecs, string[] prefixes)
    {
        if (string.IsNullOrWhiteSpace(codecs)) return false;

        foreach (var raw in codecs!.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var codec = raw.Trim().Trim('"').ToLowerInvariant();
            if (codec.Length == 0) continue;

            foreach (var prefix in prefixes)
                if (codec.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
