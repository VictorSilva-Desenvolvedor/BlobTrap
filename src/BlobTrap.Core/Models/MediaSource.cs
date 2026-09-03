using BlobTrap.Core.Net;

namespace BlobTrap.Core.Models;

/// <summary>
/// A candidate after resolution: the manifest was fetched and parsed (or an extractor was
/// asked), so we now know the title, duration and every selectable track.
/// </summary>
public sealed class MediaSource
{
    public required string Id { get; init; }
    public required Uri Url { get; init; }
    public required MediaKind Kind { get; init; }
    public required RequestContext Request { get; init; }
    public required IReadOnlyList<MediaVariant> Variants { get; init; }

    public string Title { get; init; } = "video";
    public double? DurationSeconds { get; init; }
    public Uri? PageUrl { get; init; }
    public Uri? ThumbnailUrl { get; init; }
    public bool IsLive { get; init; }

    /// <summary>True when the manifest advertises DRM. BlobTrap surfaces these but will not fetch them.</summary>
    public bool IsProtected { get; init; }

    public string? ProtectionSystem { get; init; }

    /// <summary>Which resolver produced this source, for diagnostics.</summary>
    public string ResolvedBy { get; init; } = "unknown";

    public IEnumerable<MediaVariant> VideoVariants =>
        Variants.Where(v => v.Track is TrackKind.Muxed or TrackKind.VideoOnly)
                .OrderByDescending(v => v.QualityScore);

    public IEnumerable<MediaVariant> AudioVariants =>
        Variants.Where(v => v.Track == TrackKind.AudioOnly)
                .OrderByDescending(v => v.Bandwidth ?? 0);

    public IEnumerable<MediaVariant> SubtitleVariants =>
        Variants.Where(v => v.Track == TrackKind.Subtitle);

    /// <summary>The variant a user most likely wants: highest resolution, then highest bitrate.</summary>
    public MediaVariant? BestVideo => VideoVariants.FirstOrDefault();

    /// <summary>
    /// The audio track to pair with <paramref name="video"/>. Prefers the group the video
    /// declares, then the default track, then the highest bitrate one.
    /// </summary>
    public MediaVariant? BestAudioFor(MediaVariant? video)
    {
        if (video is null || video.Track == TrackKind.Muxed) return null;

        var pool = AudioVariants.ToList();
        if (pool.Count == 0) return null;

        if (video.AudioGroupId is { } group)
        {
            var grouped = pool.Where(a => a.AudioGroupId == group).ToList();
            if (grouped.Count > 0) pool = grouped;
        }

        return pool.FirstOrDefault();
    }
}
