namespace BlobTrap.Core.Dash;

/// <summary>A parsed MPEG-DASH manifest.</summary>
public sealed class DashManifest
{
    public required Uri BaseUri { get; init; }
    public bool IsDynamic { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan? MinBufferTime { get; init; }
    public List<DashPeriod> Periods { get; } = new();

    public IEnumerable<DashRepresentation> AllRepresentations =>
        Periods.SelectMany(p => p.AdaptationSets).SelectMany(a => a.Representations);

    public bool IsProtected => Periods
        .SelectMany(p => p.AdaptationSets)
        .Any(a => a.Protections.Any(c => c.IsDrm));

    public string? ProtectionSystem => Periods
        .SelectMany(p => p.AdaptationSets)
        .SelectMany(a => a.Protections)
        .FirstOrDefault(c => c.IsDrm)?.Name;
}

public sealed class DashPeriod
{
    public string? Id { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan? Duration { get; set; }
    public List<DashAdaptationSet> AdaptationSets { get; } = new();
}

public sealed class DashAdaptationSet
{
    public string? MimeType { get; init; }
    public string? ContentType { get; init; }
    public string? Language { get; init; }
    public string? Codecs { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public List<DashRepresentation> Representations { get; } = new();
    public List<DashContentProtection> Protections { get; } = new();

    /// <summary>contentType is optional in the wild, so fall back to the mime type prefix.</summary>
    public string ResolvedContentType
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ContentType)) return ContentType!.ToLowerInvariant();
            if (MimeType is null) return "unknown";
            var slash = MimeType.IndexOf('/');
            return slash > 0 ? MimeType[..slash].ToLowerInvariant() : MimeType.ToLowerInvariant();
        }
    }
}

public sealed class DashRepresentation
{
    public required string Id { get; init; }
    public required Uri BaseUri { get; init; }
    public long? Bandwidth { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string? Codecs { get; init; }
    public string? MimeType { get; init; }
    public string? Language { get; init; }
    public string ContentType { get; init; } = "unknown";

    /// <summary>How this representation's bytes are laid out. Never null after parsing.</summary>
    public DashSegmentSource Segments { get; set; } = DashSegmentSource.None;

    public bool IsVideo => ContentType == "video";
    public bool IsAudio => ContentType == "audio";
    public bool IsText => ContentType is "text" or "application";

    public string Container
    {
        get
        {
            var mime = MimeType?.ToLowerInvariant() ?? string.Empty;
            if (mime.Contains("webm")) return "webm";
            if (mime.Contains("mp4")) return IsAudio ? "m4a" : "mp4";
            if (mime.Contains("vtt")) return "vtt";
            if (mime.Contains("ttml")) return "ttml";
            return IsAudio ? "m4a" : "mp4";
        }
    }
}

public sealed class DashContentProtection
{
    public required string SchemeIdUri { get; init; }
    public string? Value { get; init; }

    /// <summary>
    /// mp4protection alone only says "this is cenc"; a DRM system UUID says who holds the key.
    /// Widevine, PlayReady and ClearKey each have a well-known UUID.
    /// </summary>
    public bool IsDrm
    {
        get
        {
            var scheme = SchemeIdUri.ToLowerInvariant();
            if (scheme.Contains("edef8ba9-79d6-4ace-a3c8-27dcd51d21ed")) return true;  // Widevine
            if (scheme.Contains("9a04f079-9840-4286-ab92-e65be0885f95")) return true;  // PlayReady
            if (scheme.Contains("94ce86fb-07ff-4f43-adb8-93d2fa968ca2")) return true;  // FairPlay
            if (scheme.Contains("e2719d58-a985-b3c9-781a-b030af78d30e")) return true;  // ClearKey
            if (scheme.Contains("mp4protection")) return true;                          // cenc, key held elsewhere
            return false;
        }
    }

    public string Name
    {
        get
        {
            var scheme = SchemeIdUri.ToLowerInvariant();
            if (scheme.Contains("edef8ba9")) return "Widevine";
            if (scheme.Contains("9a04f079")) return "PlayReady";
            if (scheme.Contains("94ce86fb")) return "FairPlay";
            if (scheme.Contains("e2719d58")) return "ClearKey";
            if (scheme.Contains("mp4protection")) return "CENC";
            return "DRM";
        }
    }
}

/// <summary>One fetchable piece of a DASH representation.</summary>
public sealed record DashSegmentRef(Uri Uri, string? Range, bool IsInitialization, double DurationSeconds);

public abstract class DashSegmentSource
{
    public static DashSegmentSource None { get; } = new DashNoSegments();

    /// <summary>Expands this source into the ordered list of requests needed to rebuild the stream.</summary>
    public abstract IReadOnlyList<DashSegmentRef> BuildSegments(DashRepresentation representation, TimeSpan? periodDuration);

    private sealed class DashNoSegments : DashSegmentSource
    {
        public override IReadOnlyList<DashSegmentRef> BuildSegments(DashRepresentation representation, TimeSpan? periodDuration) =>
            Array.Empty<DashSegmentRef>();
    }
}

/// <summary>A single self-contained file, optionally addressed by byte ranges.</summary>
public sealed class DashSingleFileSource : DashSegmentSource
{
    public required Uri Uri { get; init; }
    public string? InitializationRange { get; init; }
    public string? IndexRange { get; init; }

    public override IReadOnlyList<DashSegmentRef> BuildSegments(DashRepresentation representation, TimeSpan? periodDuration) =>
        new[] { new DashSegmentRef(Uri, null, false, periodDuration?.TotalSeconds ?? 0) };
}

/// <summary>An explicit list of segment URLs.</summary>
public sealed class DashSegmentListSource : DashSegmentSource
{
    public Uri? InitializationUri { get; init; }
    public string? InitializationRange { get; init; }
    public long Timescale { get; init; } = 1;
    public long? SegmentDuration { get; init; }
    public List<(Uri Uri, string? Range)> Segments { get; } = new();

    public override IReadOnlyList<DashSegmentRef> BuildSegments(DashRepresentation representation, TimeSpan? periodDuration)
    {
        var result = new List<DashSegmentRef>(Segments.Count + 1);
        var duration = SegmentDuration is > 0 ? (double)SegmentDuration.Value / Math.Max(Timescale, 1) : 0;

        if (InitializationUri is not null)
            result.Add(new DashSegmentRef(InitializationUri, InitializationRange, true, 0));

        foreach (var (uri, range) in Segments)
            result.Add(new DashSegmentRef(uri, range, false, duration));

        return result;
    }
}

/// <summary>Segment URLs generated from a template, with or without an explicit timeline.</summary>
public sealed class DashSegmentTemplateSource : DashSegmentSource
{
    public string? Initialization { get; init; }
    public string? Media { get; init; }
    public long StartNumber { get; init; } = 1;
    public long Timescale { get; init; } = 1;
    public long? Duration { get; init; }
    public long PresentationTimeOffset { get; init; }

    /// <summary>Entries of a SegmentTimeline: start time, duration, repeat count (already expanded).</summary>
    public List<(long Time, long Duration)> Timeline { get; } = new();

    public override IReadOnlyList<DashSegmentRef> BuildSegments(DashRepresentation representation, TimeSpan? periodDuration)
    {
        var result = new List<DashSegmentRef>();
        var timescale = Math.Max(Timescale, 1);

        if (!string.IsNullOrWhiteSpace(Initialization))
        {
            var uri = ResolveTemplate(Initialization!, representation, number: null, time: null);
            if (uri is not null) result.Add(new DashSegmentRef(uri, null, true, 0));
        }

        if (string.IsNullOrWhiteSpace(Media)) return result;

        if (Timeline.Count > 0)
        {
            var number = StartNumber;
            foreach (var (time, duration) in Timeline)
            {
                var uri = ResolveTemplate(Media!, representation, number, time);
                if (uri is not null) result.Add(new DashSegmentRef(uri, null, false, (double)duration / timescale));
                number++;
            }
            return result;
        }

        if (Duration is not > 0 || periodDuration is null) return result;

        var segmentSeconds = (double)Duration.Value / timescale;
        if (segmentSeconds <= 0) return result;

        var count = (long)Math.Ceiling(periodDuration.Value.TotalSeconds / segmentSeconds);
        for (var i = 0L; i < count; i++)
        {
            var uri = ResolveTemplate(Media!, representation, StartNumber + i, PresentationTimeOffset + i * Duration.Value);
            if (uri is not null) result.Add(new DashSegmentRef(uri, null, false, segmentSeconds));
        }

        return result;
    }

    private Uri? ResolveTemplate(string template, DashRepresentation representation, long? number, long? time)
    {
        var expanded = DashTemplate.Expand(template, representation.Id, representation.Bandwidth, number, time);
        return Uri.TryCreate(representation.BaseUri, expanded, out var uri) ? uri : null;
    }
}
