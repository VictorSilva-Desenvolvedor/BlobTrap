using BlobTrap.Core.Hls;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Tools;
using BlobTrap.Core.Util;

namespace BlobTrap.Core.Resolving;

/// <summary>
/// Turns a sniffed candidate into a <see cref="MediaSource"/>. Tries the format-specific
/// resolver first and falls back to yt-dlp, which knows per-site extraction rules that no
/// amount of manifest parsing can replace.
/// </summary>
public sealed class MediaResolver
{
    private readonly MediaHttpClient _http;

    public MediaResolver(MediaHttpClient http)
    {
        _http = http;
        Hls = new HlsResolver(http);
        Dash = new DashResolver(http);
    }

    public HlsResolver Hls { get; }
    public DashResolver Dash { get; }

    /// <summary>Set when yt-dlp is installed; enables the fallback path and page extraction.</summary>
    public YtDlpRunner? YtDlp { get; set; } = YtDlpRunner.TryCreate();

    public async Task<MediaSource> ResolveAsync(MediaCandidate candidate, CancellationToken cancellationToken)
    {
        Exception? primaryFailure = null;

        try
        {
            var source = await ResolvePrimaryAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (source is not null && source.Variants.Count > 0) return source;
        }
        catch (OperationCanceledException) { throw; }
        catch (DrmProtectedException) { throw; }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }

        var fallback = await TryExternalAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (fallback is not null) return fallback;

        throw new InvalidOperationException(
            primaryFailure?.Message ?? "Não foi possível identificar formatos para esta mídia.", primaryFailure);
    }

    private async Task<MediaSource?> ResolvePrimaryAsync(MediaCandidate candidate, CancellationToken cancellationToken) =>
        candidate.Kind switch
        {
            MediaKind.HlsPlaylist => await Hls.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false),
            MediaKind.DashManifest => await Dash.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false),
            MediaKind.ProgressiveVideo or MediaKind.ProgressiveAudio or MediaKind.Subtitle =>
                await ResolveProgressiveAsync(candidate, cancellationToken).ConfigureAwait(false),
            MediaKind.PageEmbed => await TryExternalAsync(candidate, cancellationToken).ConfigureAwait(false),
            MediaKind.SmoothManifest => null,
            _ => null,
        };

    /// <summary>
    /// A direct file needs no manifest, but a HEAD tells us the size and can reveal that the
    /// URL is really a playlist behind a generic extension.
    /// </summary>
    private async Task<MediaSource> ResolveProgressiveAsync(MediaCandidate candidate, CancellationToken cancellationToken)
    {
        MediaProbe? probe = null;

        try
        {
            probe = await _http.ProbeAsync(candidate.Url, candidate.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The probe only enriches the result with size and real content type. Without it
            // we still hand back a downloadable variant, so this is handled by carrying on.
        }

        if (probe?.ContentType is { } contentType)
        {
            var actual = Sniffing.MediaClassifier.ClassifyByMime(contentType);

            if (actual is MediaKind.HlsPlaylist)
                return await Hls.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false);

            if (actual is MediaKind.DashManifest)
                return await Dash.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        var track = candidate.Kind switch
        {
            MediaKind.ProgressiveAudio => TrackKind.AudioOnly,
            MediaKind.Subtitle => TrackKind.Subtitle,
            _ => TrackKind.Muxed,
        };

        var extension = Path.GetExtension(candidate.Url.AbsolutePath).TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension)) extension = track == TrackKind.AudioOnly ? "m4a" : "mp4";

        var variant = new MediaVariant
        {
            Id = candidate.Id,
            Url = candidate.Url,
            Track = track,
            Delivery = DeliveryMode.Progressive,
            ContentLength = probe?.ContentLength ?? candidate.ContentLength,
            Container = extension.ToLowerInvariant(),
        };

        return new MediaSource
        {
            Id = candidate.Id,
            Url = candidate.Url,
            Kind = candidate.Kind,
            Request = candidate.Request,
            Title = candidate.PageTitle ?? Naming.NameFromUrl(candidate.Url),
            PageUrl = candidate.PageUrl,
            Variants = new[] { variant },
            ResolvedBy = "progressive",
        };
    }

    private async Task<MediaSource?> TryExternalAsync(MediaCandidate candidate, CancellationToken cancellationToken)
    {
        if (YtDlp is null) return null;

        // For a page, the page URL is the input; for a media URL, the page it came from is a
        // better input when we know it, since extractors key off the watch page.
        var targets = new List<Uri>();
        if (candidate.Kind == MediaKind.PageEmbed) targets.Add(candidate.Url);
        else
        {
            if (candidate.PageUrl is not null) targets.Add(candidate.PageUrl);
            targets.Add(candidate.Url);
        }

        foreach (var target in targets.Distinct())
        {
            try
            {
                var source = await YtDlp.ProbeAsync(target, candidate.Request, cancellationToken).ConfigureAwait(false);
                if (source is { Variants.Count: > 0 }) return source;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* try the next target */ }
        }

        return null;
    }
}
