using BlobTrap.Core.Dash;
using BlobTrap.Core.Download;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Util;

namespace BlobTrap.Core.Resolving;

/// <summary>Everything the downloader needs to expand one DASH representation.</summary>
public sealed record DashVariantPayload(DashRepresentation Representation, TimeSpan? PeriodDuration);

/// <summary>Fetches an MPD and turns every representation into a selectable variant.</summary>
public sealed class DashResolver
{
    private readonly MediaHttpClient _http;

    public DashResolver(MediaHttpClient http) => _http = http;

    public async Task<MediaSource> ResolveAsync(MediaCandidate candidate, CancellationToken cancellationToken)
    {
        var xml = await _http.GetStringAsync(candidate.Url, candidate.Request, cancellationToken).ConfigureAwait(false);
        var manifest = DashParser.Parse(xml, candidate.Url);

        var variants = new List<MediaVariant>();

        foreach (var period in manifest.Periods)
        {
            foreach (var set in period.AdaptationSets)
            {
                foreach (var representation in set.Representations)
                {
                    var variant = BuildVariant(representation, period, manifest);
                    if (variant is not null) variants.Add(variant);
                }
            }
        }

        // Several periods (ad breaks) produce duplicate qualities; keep one entry per quality.
        var deduped = variants
            .GroupBy(v => (v.Track, v.Height, v.Bandwidth, v.Codecs, v.Language))
            .Select(g => g.First())
            .ToList();

        return new MediaSource
        {
            Id = candidate.Id,
            Url = candidate.Url,
            Kind = MediaKind.DashManifest,
            Request = candidate.Request,
            Title = candidate.PageTitle ?? Naming.NameFromUrl(candidate.Url),
            PageUrl = candidate.PageUrl,
            DurationSeconds = manifest.Duration?.TotalSeconds,
            IsLive = manifest.IsDynamic,
            Variants = deduped,
            IsProtected = manifest.IsProtected,
            ProtectionSystem = manifest.ProtectionSystem,
            ResolvedBy = "dash",
        };
    }

    private static MediaVariant? BuildVariant(DashRepresentation representation, DashPeriod period, DashManifest manifest)
    {
        var track = representation.ContentType switch
        {
            "video" => TrackKind.VideoOnly,
            "audio" => TrackKind.AudioOnly,
            "text" or "application" => TrackKind.Subtitle,
            _ => TrackKind.Muxed,
        };

        // contentType is advisory; the codec list is what actually says what is inside.
        if (track == TrackKind.VideoOnly && CodecInfo.HasAudio(representation.Codecs))
            track = TrackKind.Muxed;
        else if (track == TrackKind.Muxed && CodecInfo.IsAudioOnly(representation.Codecs))
            track = TrackKind.AudioOnly;

        var duration = period.Duration ?? manifest.Duration;

        return new MediaVariant
        {
            Id = Naming.StableId(representation.BaseUri.AbsoluteUri + representation.Id),
            Url = representation.BaseUri,
            Track = track,
            Delivery = DeliveryMode.DashSegments,
            Width = representation.Width,
            Height = representation.Height,
            Bandwidth = representation.Bandwidth,
            FrameRate = representation.FrameRate,
            Codecs = representation.Codecs,
            Language = representation.Language,
            Name = track == TrackKind.AudioOnly ? BuildAudioName(representation) : null,
            DurationSeconds = duration?.TotalSeconds,
            IsLive = manifest.IsDynamic,
            Container = representation.Container,
            Payload = new DashVariantPayload(representation, duration),
        };
    }

    private static string BuildAudioName(DashRepresentation representation)
    {
        var name = representation.Language ?? "Áudio";
        if (representation.Bandwidth is > 0) name += $" {representation.Bandwidth / 1000} kbps";
        return name;
    }

    /// <summary>Expands a variant into the ordered list of segment requests.</summary>
    public DashDownloadShape BuildParts(MediaVariant variant)
    {
        if (variant.Payload is not DashVariantPayload payload)
            throw new InvalidOperationException("Variante DASH sem representacao associada.");

        var segments = payload.Representation.Segments.BuildSegments(payload.Representation, payload.PeriodDuration);

        if (segments.Count == 0)
            throw new InvalidOperationException("Representacao DASH sem segmentos (manifesto incompleto ou ao vivo sem timeline).");

        var parts = segments
            .Select(s => new MediaPart
            {
                Uri = s.Uri,
                Range = s.Range,
                IsInitialization = s.IsInitialization,
                DurationSeconds = s.DurationSeconds,
            })
            .ToList();

        return new DashDownloadShape(parts, payload.PeriodDuration?.TotalSeconds ?? 0);
    }
}

public sealed record DashDownloadShape(IReadOnlyList<MediaPart> Parts, double DurationSeconds);
