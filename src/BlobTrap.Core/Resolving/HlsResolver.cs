using BlobTrap.Core.Hls;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Util;

namespace BlobTrap.Core.Resolving;

/// <summary>Carries the parsed variant playlist reference through to the downloader.</summary>
public sealed record HlsVariantPayload(Uri PlaylistUri);

/// <summary>Fetches an HLS playlist and turns it into selectable variants.</summary>
public sealed class HlsResolver
{
    private readonly MediaHttpClient _http;

    public HlsResolver(MediaHttpClient http) => _http = http;

    public async Task<MediaSource> ResolveAsync(MediaCandidate candidate, CancellationToken cancellationToken)
    {
        var text = await _http.GetStringAsync(candidate.Url, candidate.Request, cancellationToken).ConfigureAwait(false);

        if (!HlsParser.LooksLikePlaylist(text))
            throw new FormatException("A resposta nao e uma playlist HLS.");

        var title = candidate.PageTitle ?? Naming.NameFromUrl(candidate.Url);

        return HlsParser.IsMaster(text)
            ? BuildFromMaster(HlsParser.ParseMaster(text, candidate.Url), candidate, title)
            : await BuildFromMediaAsync(HlsParser.ParseMedia(text, candidate.Url), candidate, title).ConfigureAwait(false);
    }

    internal static MediaSource BuildFromMaster(HlsMasterPlaylist master, MediaCandidate candidate, string title)
    {
        var variants = new List<MediaVariant>();

        var drmKey = master.SessionKeys.FirstOrDefault(k => k.IsDrm);

        // CODECS is only a SHOULD in RFC 8216, so a master can describe an audio-only variant
        // with no codec list at all. Missing metadata is read as audio only when some other
        // variant declares video - the mixture is what makes the intent unambiguous. A master
        // where nothing declares anything stays video, so we never hide the only video there is.
        //
        // The vouching variant must itself survive as video, or a packager that stamps
        // RESOLUTION on an audio-only entry would back an inference and then be demoted to
        // audio too, leaving nothing offerable at all.
        var hasDeclaredVideo = master.Variants.Any(v =>
            !v.IsIFrameOnly && DeclaresVideo(v) && !CodecInfo.IsAudioOnly(v.Codecs));

        foreach (var stream in master.Variants)
        {
            // Trick-play tracks hold only keyframes; downloading one gives a stuttering file.
            if (stream.IsIFrameOnly) continue;

            // A master may list an audio-only rendition as a stream alongside the video
            // variants; offering it as a "quality" hands the user a bare AAC file.
            var isAudioOnly = CodecInfo.IsAudioOnly(stream.Codecs)
                              || (hasDeclaredVideo && !DeclaresVideo(stream)
                                  && string.IsNullOrWhiteSpace(stream.Codecs));

            var track = isAudioOnly
                ? TrackKind.AudioOnly
                : stream.IsVideoOnly ? TrackKind.VideoOnly : TrackKind.Muxed;

            variants.Add(new MediaVariant
            {
                Id = Naming.StableId(stream.Uri.AbsoluteUri),
                Url = stream.Uri,
                Track = track,
                Delivery = DeliveryMode.HlsSegments,
                Width = isAudioOnly ? null : stream.Width,
                Height = isAudioOnly ? null : stream.Height,
                Bandwidth = stream.AverageBandwidth ?? stream.Bandwidth,
                FrameRate = isAudioOnly ? null : stream.FrameRate,
                Codecs = stream.Codecs,
                Name = isAudioOnly ? "Áudio" : null,
                AudioGroupId = stream.AudioGroupId,
                SubtitleGroupId = stream.SubtitlesGroupId,
                Container = isAudioOnly ? "m4a" : GuessContainer(stream.Codecs),
                Payload = new HlsVariantPayload(stream.Uri),
            });
        }

        foreach (var rendition in master.AudioRenditions)
        {
            if (rendition.Uri is null) continue;

            variants.Add(new MediaVariant
            {
                Id = Naming.StableId(rendition.Uri.AbsoluteUri),
                Url = rendition.Uri,
                Track = TrackKind.AudioOnly,
                Delivery = DeliveryMode.HlsSegments,
                Language = rendition.Language,
                Name = BuildAudioName(rendition),
                AudioGroupId = rendition.GroupId,
                Container = "m4a",
                Payload = new HlsVariantPayload(rendition.Uri),
            });
        }

        foreach (var rendition in master.SubtitleRenditions)
        {
            if (rendition.Uri is null) continue;

            variants.Add(new MediaVariant
            {
                Id = Naming.StableId(rendition.Uri.AbsoluteUri),
                Url = rendition.Uri,
                Track = TrackKind.Subtitle,
                Delivery = DeliveryMode.HlsSegments,
                Language = rendition.Language,
                Name = rendition.Name,
                SubtitleGroupId = rendition.GroupId,
                Container = "vtt",
                Payload = new HlsVariantPayload(rendition.Uri),
            });
        }

        return new MediaSource
        {
            Id = candidate.Id,
            Url = candidate.Url,
            Kind = MediaKind.HlsPlaylist,
            Request = candidate.Request,
            Title = title,
            PageUrl = candidate.PageUrl,
            Variants = variants,
            IsProtected = drmKey is not null,
            ProtectionSystem = drmKey?.DrmName,
            ResolvedBy = "hls-master",
        };
    }

    private static string BuildAudioName(HlsRendition rendition)
    {
        var name = rendition.Name ?? rendition.Language ?? "Áudio";
        if (rendition.Channels is { } channels && channels != "2") name += $" {channels}ch";
        if (rendition.IsDefault) name += " (padrao)";
        return name;
    }

    private Task<MediaSource> BuildFromMediaAsync(HlsMediaPlaylist media, MediaCandidate candidate, string title)
    {
        var drmKey = media.DistinctKeys.FirstOrDefault(k => k.IsDrm);
        var duration = media.TotalDuration;

        var variant = new MediaVariant
        {
            Id = candidate.Id,
            Url = candidate.Url,
            Track = TrackKind.Muxed,
            Delivery = DeliveryMode.HlsSegments,
            DurationSeconds = duration > 0 ? duration : null,
            IsLive = media.IsLive,
            Container = media.Segments.Count > 0 && media.Segments[0].Map is not null ? "mp4" : "mp4",
            Payload = new HlsVariantPayload(candidate.Url),
        };

        var source = new MediaSource
        {
            Id = candidate.Id,
            Url = candidate.Url,
            Kind = MediaKind.HlsPlaylist,
            Request = candidate.Request,
            Title = title,
            PageUrl = candidate.PageUrl,
            DurationSeconds = duration > 0 ? duration : null,
            IsLive = media.IsLive,
            Variants = new[] { variant },
            IsProtected = drmKey is not null,
            ProtectionSystem = drmKey?.DrmName,
            ResolvedBy = "hls-media",
        };

        return Task.FromResult(source);
    }

    /// <summary>
    /// Positive evidence that a variant carries video: a declared video codec, or a RESOLUTION.
    /// Either one alone is enough; neither means the variant said nothing about itself.
    /// </summary>
    private static bool DeclaresVideo(HlsVariantStream stream) =>
        CodecInfo.HasVideo(stream.Codecs) || stream.Width is > 0 || stream.Height is > 0;

    private static string GuessContainer(string? codecs)
    {
        if (codecs is null) return "mp4";
        return codecs.Contains("vp9", StringComparison.OrdinalIgnoreCase) ? "webm" : "mp4";
    }

    /// <summary>
    /// Expands a variant into the parts to fetch. Called at download time because a media
    /// playlist for a long video can list thousands of segments.
    /// </summary>
    public async Task<HlsDownloadShape> BuildPartsAsync(
        MediaVariant variant,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var playlistUri = (variant.Payload as HlsVariantPayload)?.PlaylistUri ?? variant.Url;

        var text = await _http.GetStringAsync(playlistUri, context, cancellationToken).ConfigureAwait(false);

        // A "variant" that turns out to be another master happens with nested CDN packaging.
        if (HlsParser.IsMaster(text))
        {
            var nested = HlsParser.ParseMaster(text, playlistUri);
            var best = nested.Variants.Where(v => !v.IsIFrameOnly)
                                      .OrderByDescending(v => v.Bandwidth ?? 0)
                                      .FirstOrDefault()
                       ?? throw new FormatException("Master aninhado sem variantes.");

            playlistUri = best.Uri;
            text = await _http.GetStringAsync(playlistUri, context, cancellationToken).ConfigureAwait(false);
        }

        var media = HlsParser.ParseMedia(text, playlistUri);

        if (media.DistinctKeys.Any(k => k.IsDrm))
            throw new DrmProtectedException(media.DistinctKeys.First(k => k.IsDrm).DrmName ?? "DRM");

        var parts = new List<Download.MediaPart>(media.Segments.Count + 1);
        var mapsAdded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segment in media.Segments)
        {
            // An fMP4 stream needs its init segment before any media segment of that map.
            if (segment.Map is { } map && mapsAdded.Add(map.Uri.AbsoluteUri + map.RangeHeader))
            {
                parts.Add(new Download.MediaPart
                {
                    Uri = map.Uri,
                    Range = map.RangeHeader,
                    IsInitialization = true,
                });
            }

            parts.Add(new Download.MediaPart
            {
                Uri = segment.Uri,
                Range = segment.RangeHeader,
                DurationSeconds = segment.Duration,
                KeyUri = segment.Key is { IsAes128: true } key ? key.Uri : null,
                Iv = segment.Key?.Iv,
            });
        }

        var isFragmentedMp4 = media.Segments.Any(s => s.Map is not null);

        return new HlsDownloadShape(parts, media.TotalDuration, media.IsLive, isFragmentedMp4);
    }
}

/// <summary>What the downloader needs to know about an expanded HLS variant.</summary>
public sealed record HlsDownloadShape(
    IReadOnlyList<Download.MediaPart> Parts,
    double DurationSeconds,
    bool IsLive,
    bool IsFragmentedMp4);

/// <summary>Thrown when a stream is encrypted by a DRM system. BlobTrap stops here by design.</summary>
public sealed class DrmProtectedException : Exception
{
    public DrmProtectedException(string system)
        : base($"Este video e protegido por DRM ({system}). O BlobTrap nao baixa conteudo com DRM.")
        => System = system;

    public string System { get; }
}
