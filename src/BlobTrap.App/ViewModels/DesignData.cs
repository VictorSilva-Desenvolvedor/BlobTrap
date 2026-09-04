using System.IO;
using System.Windows.Threading;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;

namespace BlobTrap.App.ViewModels;

/// <summary>
/// Realistic sample content for the design preview.
///
/// It builds genuine model objects rather than stand-ins, so the preview exercises the same
/// templates, converters and label logic the real app does. Reviewing the interface on an empty
/// window hides exactly the problems worth catching: overflowing titles, badge contrast, how a
/// long file path behaves next to a button.
/// </summary>
internal static class DesignData
{
    public static IReadOnlyList<CandidateItem> Candidates()
    {
        var items = new List<CandidateItem>();

        items.Add(Candidate(
            "https://vod-cdn.exemplo.com/hls/8f21c/master.m3u8",
            MediaKind.HlsPlaylist,
            title: "Documentário: A Vida Secreta dos Oceanos - Episódio 3",
            hits: 4));

        items.Add(Candidate(
            "https://dash.exemplo.com/videos/2024/manifest.mpd",
            MediaKind.DashManifest,
            title: "Entrevista completa com a equipe de engenharia",
            hits: 2));

        items.Add(Candidate(
            "https://media.exemplo.com/downloads/apresentacao-final.mp4",
            MediaKind.ProgressiveVideo,
            size: 348L * 1024 * 1024));

        items.Add(Candidate(
            "https://audio.exemplo.com/podcasts/episodio-142.m4a",
            MediaKind.ProgressiveAudio,
            size: 42L * 1024 * 1024));

        items.Add(Candidate(
            "https://vod-cdn.exemplo.com/subs/pt-BR.vtt",
            MediaKind.Subtitle,
            size: 18 * 1024));

        return items;
    }

    private static CandidateItem Candidate(
        string url,
        MediaKind kind,
        string? title = null,
        long? size = null,
        int hits = 1)
    {
        var candidate = new MediaCandidate(new Uri(url), kind, RequestContext.Default)
        {
            PageTitle = title,
            ContentLength = size,
            HitCount = hits,
            PageUrl = new Uri("https://www.exemplo.com/assistir/8f21c"),
        };

        return new CandidateItem(candidate);
    }

    /// <summary>A resolved source with a full ladder of tracks, for reviewing the quality dialog.</summary>
    public static MediaSource Source()
    {
        var variants = new List<MediaVariant>();

        void Video(int height, int width, long bitrate, double fps = 30)
        {
            variants.Add(new MediaVariant
            {
                Id = $"v{height}",
                Url = new Uri($"https://vod-cdn.exemplo.com/hls/8f21c/{height}.m3u8"),
                Track = TrackKind.VideoOnly,
                Delivery = DeliveryMode.HlsSegments,
                Width = width,
                Height = height,
                Bandwidth = bitrate,
                FrameRate = fps,
                Codecs = "avc1.640028",
                DurationSeconds = 2_412,
                AudioGroupId = "aud",
            });
        }

        Video(2160, 3840, 16_800_000, 60);
        Video(1080, 1920, 5_400_000, 60);
        Video(720, 1280, 2_600_000);
        Video(480, 854, 1_100_000);
        Video(360, 640, 620_000);

        foreach (var (name, language, bitrate) in new[]
                 {
                     ("Português (padrão)", "pt-BR", 128_000L),
                     ("English", "en", 128_000L),
                 })
        {
            variants.Add(new MediaVariant
            {
                Id = $"a-{language}",
                Url = new Uri($"https://vod-cdn.exemplo.com/hls/8f21c/audio-{language}.m3u8"),
                Track = TrackKind.AudioOnly,
                Delivery = DeliveryMode.HlsSegments,
                Name = name,
                Language = language,
                Bandwidth = bitrate,
                Codecs = "mp4a.40.2",
                AudioGroupId = "aud",
                Container = "m4a",
            });
        }

        foreach (var (name, language) in new[] { ("Português", "pt-BR"), ("English", "en") })
        {
            variants.Add(new MediaVariant
            {
                Id = $"s-{language}",
                Url = new Uri($"https://vod-cdn.exemplo.com/hls/8f21c/subs-{language}.m3u8"),
                Track = TrackKind.Subtitle,
                Delivery = DeliveryMode.HlsSegments,
                Name = name,
                Language = language,
                Container = "vtt",
            });
        }

        return new MediaSource
        {
            Id = "sample",
            Url = new Uri("https://vod-cdn.exemplo.com/hls/8f21c/master.m3u8"),
            Kind = MediaKind.HlsPlaylist,
            Request = RequestContext.Default,
            Title = "Documentário: A Vida Secreta dos Oceanos - Episódio 3",
            DurationSeconds = 2_412,
            Variants = variants,
            ResolvedBy = "hls-master",
        };
    }

    public static IReadOnlyList<JobItem> Jobs(Dispatcher dispatcher)
    {
        var items = new List<JobItem>
        {
            Job(dispatcher,
                "Documentário A Vida Secreta dos Oceanos [1080p].mp4",
                new JobPreview(
                    StateLabel: "Baixando vídeo",
                    DetailLabel: "184/412 segmentos  -  6.2 MB/s  -  faltam 1:12",
                    ProgressPercent: 44.6)),

            Job(dispatcher,
                "Entrevista completa com a equipe [720p].mp4",
                new JobPreview(
                    StateLabel: "Finalizando",
                    DetailLabel: "Juntando vídeo e áudio com ffmpeg",
                    ProgressPercent: 92)),

            Job(dispatcher,
                "Apresentação final [1080p].mp4",
                new JobPreview(
                    StateLabel: "Concluído",
                    DetailLabel: @"C:\Users\você\Vídeos\BlobTrap\Apresentação final [1080p].mp4",
                    ProgressPercent: 100,
                    IsCompleted: true,
                    CanCancel: false)),

            Job(dispatcher,
                "Transmissão ao vivo [1080p].mp4",
                new JobPreview(
                    StateLabel: "Falhou",
                    DetailLabel: "Este vídeo é protegido por DRM (Widevine). O BlobTrap não baixa conteúdo com DRM.",
                    ProgressPercent: 0,
                    IsFailed: true,
                    CanCancel: false)),

            Job(dispatcher,
                "Documentario sobre o mar [1080p].mp4",
                new JobPreview(
                    StateLabel: "Falhou",
                    DetailLabel: "Falha ao buscar segmento 412 apos 5 tentativas.",
                    ProgressPercent: 63,
                    IsFailed: true,
                    CanCancel: false,
                    CanRetry: true)),
        };

        return items;
    }

    private static JobItem Job(Dispatcher dispatcher, string fileName, JobPreview preview)
    {
        var variant = new MediaVariant
        {
            Id = "sample",
            Url = new Uri("https://vod-cdn.exemplo.com/hls/8f21c/1080.m3u8"),
            Track = TrackKind.VideoOnly,
            Delivery = DeliveryMode.HlsSegments,
            Width = 1920,
            Height = 1080,
            Bandwidth = 5_400_000,
            Codecs = "avc1.640028",
            DurationSeconds = 2_412,
        };

        var source = new MediaSource
        {
            Id = "sample",
            Url = new Uri("https://vod-cdn.exemplo.com/hls/8f21c/master.m3u8"),
            Kind = MediaKind.HlsPlaylist,
            Request = RequestContext.Default,
            Title = Path.GetFileNameWithoutExtension(fileName),
            DurationSeconds = 2_412,
            Variants = new[] { variant },
        };

        var plan = new DownloadPlan
        {
            Source = source,
            Video = variant,
            OutputPath = Path.Combine(@"C:\Users\você\Vídeos\BlobTrap", fileName),
        };

        return new JobItem(new DownloadJob(plan), dispatcher, preview);
    }
}
