using System.Collections.Concurrent;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;

namespace BlobTrap.Core.Sniffing;

public sealed class SnifferOptions
{
    /// <summary>Drop progressive files smaller than this when the server reports a size. 0 keeps everything.</summary>
    public long MinProgressiveBytes { get; set; }

    /// <summary>Keep subtitle tracks in the candidate list.</summary>
    public bool IncludeSubtitles { get; set; } = true;

    /// <summary>Keep audio-only files (podcasts, music) in the candidate list.</summary>
    public bool IncludeAudio { get; set; } = true;

    /// <summary>Hide ad and analytics hosts.</summary>
    public bool FilterNoise { get; set; } = true;
}

/// <summary>Segments seen for one stream, used to prove a stream is playing even without its manifest.</summary>
public sealed class SegmentFamily
{
    public required string Key { get; init; }
    public required Uri SampleUrl { get; init; }
    public int Count { get; set; }
    public long TotalBytes { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// Accumulates what the sniffer sees on a page: deduped media candidates plus segment
/// families. Thread-safe - CDP events arrive off the UI thread.
/// </summary>
public sealed class MediaRegistry
{
    private readonly ConcurrentDictionary<string, MediaCandidate> _candidates = new();
    private readonly ConcurrentDictionary<string, SegmentFamily> _families = new();
    private readonly object _sync = new();

    public SnifferOptions Options { get; } = new();

    public event EventHandler<MediaCandidate>? CandidateAdded;
    public event EventHandler<MediaCandidate>? CandidateUpdated;
    public event EventHandler? Cleared;

    public IReadOnlyList<MediaCandidate> Snapshot() =>
        _candidates.Values.OrderByDescending(c => c.Kind.IsStreaming())
                          .ThenByDescending(c => c.ContentLength ?? 0)
                          .ThenBy(c => c.FirstSeen)
                          .ToList();

    public IReadOnlyList<SegmentFamily> SegmentFamilies() =>
        _families.Values.OrderByDescending(f => f.Count).ToList();

    public MediaCandidate? Find(string id) => _candidates.TryGetValue(id, out var candidate) ? candidate : null;

    /// <summary>
    /// Records one observed request/response. Returns the candidate when this URL is
    /// something the user could download, or null when it was noise, a segment, or filtered.
    /// </summary>
    public MediaCandidate? Observe(
        Uri url,
        string? mimeType,
        RequestContext request,
        Uri? pageUrl = null,
        string? pageTitle = null,
        long? contentLength = null)
    {
        if (Options.FilterNoise && MediaClassifier.IsNoise(url)) return null;

        var kind = MediaClassifier.Classify(url, mimeType);
        if (kind == MediaKind.Unknown) return null;

        if (kind == MediaKind.MediaSegment)
        {
            TrackSegment(url, contentLength);
            return null;
        }

        if (!Options.IncludeSubtitles && kind == MediaKind.Subtitle) return null;
        if (!Options.IncludeAudio && kind == MediaKind.ProgressiveAudio) return null;

        if (Options.MinProgressiveBytes > 0 &&
            kind is MediaKind.ProgressiveVideo or MediaKind.ProgressiveAudio &&
            contentLength is > 0 && contentLength < Options.MinProgressiveBytes)
            return null;

        // A byte-range request for a file we already track is the same candidate, not a new one.
        var key = CandidateKey(url);

        MediaCandidate candidate;
        bool isNew;

        lock (_sync)
        {
            if (_candidates.TryGetValue(key, out var existing))
            {
                existing.HitCount++;
                existing.LastSeen = DateTimeOffset.Now;
                existing.MimeType ??= mimeType;
                if (contentLength > existing.ContentLength) existing.ContentLength = contentLength;
                if (existing.PageUrl is null) existing.PageUrl = pageUrl;
                if (string.IsNullOrWhiteSpace(existing.PageTitle)) existing.PageTitle = pageTitle;

                candidate = existing;
                isNew = false;
            }
            else
            {
                candidate = new MediaCandidate(url, kind, request)
                {
                    MimeType = mimeType,
                    ContentLength = contentLength,
                    PageUrl = pageUrl,
                    PageTitle = pageTitle,
                };
                _candidates[key] = candidate;
                isNew = true;
            }
        }

        if (isNew) CandidateAdded?.Invoke(this, candidate);
        else CandidateUpdated?.Invoke(this, candidate);

        return candidate;
    }

    /// <summary>Adds a URL the user typed or pasted, bypassing the noise filters.</summary>
    public MediaCandidate AddManual(Uri url, RequestContext request, MediaKind? forcedKind = null)
    {
        var kind = forcedKind ?? MediaClassifier.Classify(url, null);
        if (kind is MediaKind.Unknown or MediaKind.MediaSegment) kind = MediaKind.PageEmbed;

        var key = CandidateKey(url);
        var candidate = new MediaCandidate(url, kind, request);

        _candidates[key] = candidate;
        CandidateAdded?.Invoke(this, candidate);
        return candidate;
    }

    private void TrackSegment(Uri url, long? contentLength)
    {
        var key = MediaClassifier.SegmentFamilyKey(url);
        var family = _families.GetOrAdd(key, static (k, u) => new SegmentFamily { Key = k, SampleUrl = u }, url);

        lock (_sync)
        {
            family.Count++;
            family.TotalBytes += contentLength ?? 0;
            family.LastSeen = DateTimeOffset.Now;
        }
    }

    /// <summary>Identity of a candidate: the URL without volatile CDN tokens.</summary>
    private static string CandidateKey(Uri url)
    {
        // Range/token/expiry parameters change per request but point at the same asset.
        var query = url.Query.TrimStart('?');
        if (query.Length == 0) return url.GetLeftPart(UriPartial.Path);

        var kept = query.Split('&')
            .Where(pair => !IsVolatileParameter(pair.Split('=')[0]))
            .OrderBy(pair => pair, StringComparer.Ordinal);

        var stable = string.Join('&', kept);
        return stable.Length == 0
            ? url.GetLeftPart(UriPartial.Path)
            : url.GetLeftPart(UriPartial.Path) + "?" + stable;
    }

    private static bool IsVolatileParameter(string name) => name.ToLowerInvariant() switch
    {
        "range" or "rn" or "rbuf" or "cpn" or "ei" or "_nc_rid" or "bytestart" or "byteend"
            or "expires" or "expire" or "token" or "hdnts" or "signature" or "sig" or "_" => true,
        _ => false,
    };

    public void Clear()
    {
        _candidates.Clear();
        _families.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
