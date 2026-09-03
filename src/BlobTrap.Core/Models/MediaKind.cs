namespace BlobTrap.Core.Models;

/// <summary>What kind of thing a sniffed URL points at.</summary>
public enum MediaKind
{
    Unknown = 0,

    /// <summary>A complete video file served over HTTP (mp4, webm, mkv, ...).</summary>
    ProgressiveVideo,

    /// <summary>A complete audio file served over HTTP (mp3, m4a, opus, ...).</summary>
    ProgressiveAudio,

    /// <summary>An HLS playlist (.m3u8) - either master or media.</summary>
    HlsPlaylist,

    /// <summary>An MPEG-DASH manifest (.mpd).</summary>
    DashManifest,

    /// <summary>A Smooth Streaming manifest (/Manifest, .ism).</summary>
    SmoothManifest,

    /// <summary>A single fragment of a stream (.ts, .m4s, init.mp4). Only useful as a hint.</summary>
    MediaSegment,

    /// <summary>A subtitle track (.vtt, .srt, .ttml).</summary>
    Subtitle,

    /// <summary>A page an external extractor (yt-dlp) knows how to handle.</summary>
    PageEmbed,
}

public static class MediaKindExtensions
{
    /// <summary>True when the kind is something the user can actually download on its own.</summary>
    public static bool IsDownloadable(this MediaKind kind) => kind switch
    {
        MediaKind.ProgressiveVideo or MediaKind.ProgressiveAudio => true,
        MediaKind.HlsPlaylist or MediaKind.DashManifest or MediaKind.SmoothManifest => true,
        MediaKind.Subtitle or MediaKind.PageEmbed => true,
        _ => false,
    };

    public static bool IsStreaming(this MediaKind kind) =>
        kind is MediaKind.HlsPlaylist or MediaKind.DashManifest or MediaKind.SmoothManifest;

    public static string ToDisplayString(this MediaKind kind) => kind switch
    {
        MediaKind.ProgressiveVideo => "Arquivo de vídeo",
        MediaKind.ProgressiveAudio => "Arquivo de áudio",
        MediaKind.HlsPlaylist => "HLS",
        MediaKind.DashManifest => "DASH",
        MediaKind.SmoothManifest => "Smooth Streaming",
        MediaKind.MediaSegment => "Fragmento",
        MediaKind.Subtitle => "Legenda",
        MediaKind.PageEmbed => "Página",
        _ => "Desconhecido",
    };
}
