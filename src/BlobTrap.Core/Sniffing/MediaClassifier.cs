using BlobTrap.Core.Models;

namespace BlobTrap.Core.Sniffing;

/// <summary>
/// Decides whether a URL seen on the wire is media, and of what kind. Pure function of
/// (url, mime type) so it can be unit tested without a browser.
/// </summary>
public static class MediaClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".flv", ".avi", ".ogv", ".3gp", ".mpg", ".mpeg", ".wmv",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".opus", ".ogg", ".oga", ".flac", ".wav", ".weba",
    };

    private static readonly HashSet<string> SegmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".m4s", ".cmfv", ".cmfa", ".cmft", ".fmp4", ".dash", ".seg", ".aac_seg",
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vtt", ".srt", ".ttml", ".dfxp", ".ssa", ".ass",
    };

    /// <summary>Hosts whose media requests are ads or analytics beacons, never the content.</summary>
    private static readonly string[] NoiseHostFragments =
    {
        "doubleclick.net", "googlesyndication.com", "google-analytics.com", "googletagmanager.com",
        "scorecardresearch.com", "adsystem.com", "adnxs.com", "moatads.com", "imasdk.googleapis.com",
        "amazon-adsystem.com", "criteo.", "taboola.com", "outbrain.com", "quantserve.com",
    };

    /// <summary>Path fragments that mark a request as an ad break rather than the feature content.</summary>
    private static readonly string[] NoisePathFragments =
    {
        "/ads/", "/advert", "/preroll", "/midroll", "/beacon", "/pixel", "/tracking",

    };

    public static MediaKind Classify(Uri url, string? mimeType)
    {
        if (!url.IsAbsoluteUri) return MediaKind.Unknown;
        if (url.Scheme is not ("http" or "https")) return MediaKind.Unknown;
        if (IsNoise(url)) return MediaKind.Unknown;

        var byMime = ClassifyByMime(mimeType);
        var byPath = ClassifyByPath(url);

        // A CDN that serves .m3u8 as text/plain is common, so the path wins whenever it is
        // specific. Mime only wins when the path told us nothing.
        if (byPath != MediaKind.Unknown) return Reconcile(byPath, byMime);
        return byMime;
    }

    /// <summary>
    /// When path and mime disagree, trust whichever is more specific. The one case that
    /// matters: a ".mp4" that the server labels as an iso segment is a DASH fragment.
    /// </summary>
    private static MediaKind Reconcile(MediaKind byPath, MediaKind byMime)
    {
        if (byMime == MediaKind.MediaSegment) return MediaKind.MediaSegment;

        // "audio.mp4" style DASH audio representations are labelled audio/mp4 by the server.
        if (byPath == MediaKind.ProgressiveVideo && byMime == MediaKind.ProgressiveAudio)
            return MediaKind.ProgressiveAudio;

        return byPath;
    }

    public static MediaKind ClassifyByMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return MediaKind.Unknown;

        var mime = mimeType!.Split(';')[0].Trim().ToLowerInvariant();

        return mime switch
        {
            "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl"
                or "audio/x-mpegurl" or "application/mpegurl" => MediaKind.HlsPlaylist,

            "application/dash+xml" => MediaKind.DashManifest,
            "application/vnd.ms-sstr+xml" => MediaKind.SmoothManifest,

            "video/mp2t" or "video/iso.segment" or "audio/iso.segment" => MediaKind.MediaSegment,

            "text/vtt" or "application/x-subrip" or "application/ttml+xml"
                or "application/ttaf+xml" => MediaKind.Subtitle,

            _ when mime.StartsWith("video/") => MediaKind.ProgressiveVideo,
            _ when mime.StartsWith("audio/") => MediaKind.ProgressiveAudio,
            _ => MediaKind.Unknown,
        };
    }

    public static MediaKind ClassifyByPath(Uri url)
    {
        var path = url.AbsolutePath;
        var extension = Path.GetExtension(path);

        // Some CDNs hide the real name in a query parameter (…?file=movie.mp4).
        if (string.IsNullOrEmpty(extension) && !string.IsNullOrEmpty(url.Query))
            extension = ExtensionFromQuery(url.Query);

        if (extension is ".m3u8" or ".m3u") return MediaKind.HlsPlaylist;
        if (extension is ".mpd") return MediaKind.DashManifest;
        if (extension is ".ism" or ".isml") return MediaKind.SmoothManifest;

        if (path.EndsWith("/manifest", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".ism/manifest", StringComparison.OrdinalIgnoreCase))
            return MediaKind.SmoothManifest;

        if (SegmentExtensions.Contains(extension)) return MediaKind.MediaSegment;
        if (SubtitleExtensions.Contains(extension)) return MediaKind.Subtitle;

        if (VideoExtensions.Contains(extension))
            return LooksLikeFragment(path) ? MediaKind.MediaSegment : MediaKind.ProgressiveVideo;

        if (AudioExtensions.Contains(extension))
            return LooksLikeFragment(path) ? MediaKind.MediaSegment : MediaKind.ProgressiveAudio;

        return MediaKind.Unknown;
    }

    private static string ExtensionFromQuery(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;

            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            var extension = Path.GetExtension(value.Split('?')[0]);
            if (!string.IsNullOrEmpty(extension) && extension.Length <= 6) return extension;
        }
        return string.Empty;
    }

    /// <summary>
    /// A ".mp4" is a fragment, not a file, when its name is an init/segment marker. Getting
    /// this wrong floods the candidate list with thousands of unplayable chunks.
    /// </summary>
    private static bool LooksLikeFragment(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0) return false;

        var lower = name.ToLowerInvariant();

        if (lower is "init" or "initialization" or "header") return true;
        if (lower.StartsWith("init-") || lower.StartsWith("init_")) return true;
        if (lower.EndsWith("-init") || lower.EndsWith("_init")) return true;

        if (lower.StartsWith("seg-") || lower.StartsWith("seg_") ||
            lower.StartsWith("chunk-") || lower.StartsWith("chunk_") ||
            lower.StartsWith("frag-") || lower.StartsWith("frag_") ||
            lower.StartsWith("media-") && lower.Contains('.'))
            return true;

        // A bare number ("00042.mp4") is always a segment index.
        return lower.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Traduz o ResourceType que o CDP carimba na resposta.
    ///
    /// Só serve como último recurso, quando caminho e mime já falharam: "Media" é o navegador
    /// dizendo que entregou aquele corpo a um elemento de mídia, o que é mais confiável do que
    /// qualquer palpite sobre a URL — e é a única pista que sobra quando a CDN serve o arquivo
    /// sem extensão e sem Content-Type útil.
    ///
    /// O mime, quando existe, decide entre áudio e vídeo. Sem ele o padrão é vídeo: errar para
    /// esse lado deixa a faixa aparecer na lista e o resolvedor descobre o resto ao sondar a
    /// URL; errar para o outro esconde a mídia, que é o que se está tentando corrigir.
    /// </summary>
    /// <summary>
    /// Tipos de conteudo que embrulham a midia num protocolo proprio, negociado, que o motor
    /// nativo do BlobTrap nao fala.
    ///
    /// O caso vivo e' o UMP/SABR do YouTube: desde a migracao, o player pede
    /// <c>/videoplayback?...&amp;sabr=1</c> por POST e recebe
    /// <c>application/vnd.yt-ump</c> - um container proprio, com os segmentos dentro. A URL
    /// existe e responde, mas baixa-la direto entrega bytes que nao tocam em lugar nenhum.
    ///
    /// Reconhecer isto nao e' para oferecer o arquivo: e' para saber que a PAGINA precisa ir
    /// para o extrator externo. Ver <c>MediaRegistry.Observe</c>.
    /// </summary>
    public static bool IsOpaqueStreamingProtocol(string? mimeType)
    {
        var mime = (mimeType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

        return mime is "application/vnd.yt-ump" or "application/vnd.yt-sabr";
    }

    public static MediaKind FromResourceType(string? resourceType, string? mimeType)
    {
        if (!string.Equals(resourceType?.Trim(), "Media", StringComparison.OrdinalIgnoreCase))
            return MediaKind.Unknown;

        var mime = (mimeType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (mime.StartsWith("audio/")) return MediaKind.ProgressiveAudio;

        return MediaKind.ProgressiveVideo;
    }

    public static bool IsNoise(Uri url)
    {
        var host = url.Host;
        foreach (var fragment in NoiseHostFragments)
            if (host.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;

        var path = url.AbsolutePath;
        foreach (var fragment in NoisePathFragments)
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Groups segments that belong to the same stream so the UI shows one "stream detected"
    /// row instead of one row per chunk. Keyed by the segment's parent directory.
    /// </summary>
    public static string SegmentFamilyKey(Uri url)
    {
        var path = url.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        var directory = lastSlash > 0 ? path[..lastSlash] : path;
        return url.Host + directory;
    }
}
