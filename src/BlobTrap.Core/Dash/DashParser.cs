using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BlobTrap.Core.Dash;

/// <summary>
/// Parses MPEG-DASH manifests. Namespace-agnostic (matches on local names) because packagers
/// disagree on prefixes, and tolerant of missing optional attributes.
/// </summary>
public static class DashParser
{
    public static bool LooksLikeManifest(string text)
    {
        var head = text.AsSpan().TrimStart();
        return head.StartsWith("<?xml") || head.StartsWith("<MPD");
    }

    public static DashManifest Parse(string xml, Uri manifestUri)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root ?? throw new FormatException("MPD vazio.");

        if (!root.Name.LocalName.Equals("MPD", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Raiz inesperada '{root.Name.LocalName}', esperava MPD.");

        var mpdBase = CombineBaseUrls(manifestUri, root);

        var manifest = new DashManifest
        {
            BaseUri = mpdBase,
            IsDynamic = string.Equals(Attr(root, "type"), "dynamic", StringComparison.OrdinalIgnoreCase),
            Duration = ParseDuration(Attr(root, "mediaPresentationDuration")),
            MinBufferTime = ParseDuration(Attr(root, "minBufferTime")),
        };

        foreach (var periodElement in Children(root, "Period"))
        {
            var period = ParsePeriod(periodElement, mpdBase);
            manifest.Periods.Add(period);
        }

        FillMissingPeriodDurations(manifest);
        return manifest;
    }

    /// <summary>
    /// A Period without @duration ends where the next one starts, or at the presentation end.
    /// Templates without a SegmentTimeline need this to know how many segments exist.
    /// </summary>
    private static void FillMissingPeriodDurations(DashManifest manifest)
    {
        for (var i = 0; i < manifest.Periods.Count; i++)
        {
            var period = manifest.Periods[i];
            if (period.Duration is not null) continue;

            if (i + 1 < manifest.Periods.Count)
                period.Duration = manifest.Periods[i + 1].Start - period.Start;
            else if (manifest.Duration is not null)
                period.Duration = manifest.Duration.Value - period.Start;
        }
    }

    private static DashPeriod ParsePeriod(XElement element, Uri parentBase)
    {
        var periodBase = CombineBaseUrls(parentBase, element);

        var period = new DashPeriod
        {
            Id = Attr(element, "id"),
            Start = ParseDuration(Attr(element, "start")) ?? TimeSpan.Zero,
            Duration = ParseDuration(Attr(element, "duration")),
        };

        foreach (var setElement in Children(element, "AdaptationSet"))
            period.AdaptationSets.Add(ParseAdaptationSet(setElement, periodBase));

        return period;
    }

    private static DashAdaptationSet ParseAdaptationSet(XElement element, Uri parentBase)
    {
        var setBase = CombineBaseUrls(parentBase, element);

        var set = new DashAdaptationSet
        {
            MimeType = Attr(element, "mimeType"),
            ContentType = Attr(element, "contentType"),
            Language = Attr(element, "lang"),
            Codecs = Attr(element, "codecs"),
            Width = ParseInt(Attr(element, "width")),
            Height = ParseInt(Attr(element, "height")),
            FrameRate = ParseFrameRate(Attr(element, "frameRate")),
        };

        foreach (var protection in Children(element, "ContentProtection"))
        {
            var scheme = Attr(protection, "schemeIdUri");
            if (scheme is null) continue;
            set.Protections.Add(new DashContentProtection { SchemeIdUri = scheme, Value = Attr(protection, "value") });
        }

        // Segment info declared on the set is inherited by every representation inside it.
        var inheritedTemplate = Child(element, "SegmentTemplate");
        var inheritedList = Child(element, "SegmentList");

        foreach (var representationElement in Children(element, "Representation"))
            set.Representations.Add(ParseRepresentation(representationElement, set, setBase, inheritedTemplate, inheritedList));

        return set;
    }

    private static DashRepresentation ParseRepresentation(
        XElement element,
        DashAdaptationSet set,
        Uri parentBase,
        XElement? inheritedTemplate,
        XElement? inheritedList)
    {
        var representationBase = CombineBaseUrls(parentBase, element);
        var mimeType = Attr(element, "mimeType") ?? set.MimeType;

        var representation = new DashRepresentation
        {
            Id = Attr(element, "id") ?? Guid.NewGuid().ToString("N")[..8],
            BaseUri = representationBase,
            Bandwidth = ParseLong(Attr(element, "bandwidth")),
            Width = ParseInt(Attr(element, "width")) ?? set.Width,
            Height = ParseInt(Attr(element, "height")) ?? set.Height,
            FrameRate = ParseFrameRate(Attr(element, "frameRate")) ?? set.FrameRate,
            Codecs = Attr(element, "codecs") ?? set.Codecs,
            MimeType = mimeType,
            Language = set.Language,
            ContentType = ResolveContentType(set, mimeType),
        };

        representation.Segments = BuildSegmentSource(element, representationBase, inheritedTemplate, inheritedList);
        return representation;
    }

    private static string ResolveContentType(DashAdaptationSet set, string? mimeType)
    {
        var resolved = set.ResolvedContentType;
        if (resolved != "unknown") return resolved;

        if (mimeType is null) return "unknown";
        var slash = mimeType.IndexOf('/');
        return slash > 0 ? mimeType[..slash].ToLowerInvariant() : "unknown";
    }

    private static DashSegmentSource BuildSegmentSource(
        XElement representation,
        Uri baseUri,
        XElement? inheritedTemplate,
        XElement? inheritedList)
    {
        var template = Child(representation, "SegmentTemplate") ?? inheritedTemplate;
        if (template is not null) return ParseSegmentTemplate(template);

        var list = Child(representation, "SegmentList") ?? inheritedList;
        if (list is not null) return ParseSegmentList(list, baseUri);

        var segmentBase = Child(representation, "SegmentBase");
        return new DashSingleFileSource
        {
            Uri = baseUri,
            InitializationRange = segmentBase is not null
                ? Attr(Child(segmentBase, "Initialization"), "range")
                : null,
            IndexRange = segmentBase is not null ? Attr(segmentBase, "indexRange") : null,
        };
    }

    private static DashSegmentSource ParseSegmentTemplate(XElement element)
    {
        var source = new DashSegmentTemplateSource
        {
            Initialization = Attr(element, "initialization") ?? Attr(Child(element, "Initialization"), "sourceURL"),
            Media = Attr(element, "media"),
            StartNumber = ParseLong(Attr(element, "startNumber")) ?? 1,
            Timescale = ParseLong(Attr(element, "timescale")) ?? 1,
            Duration = ParseLong(Attr(element, "duration")),
            PresentationTimeOffset = ParseLong(Attr(element, "presentationTimeOffset")) ?? 0,
        };

        var timeline = Child(element, "SegmentTimeline");
        if (timeline is null) return source;

        long currentTime = 0;
        var first = true;

        foreach (var s in Children(timeline, "S"))
        {
            var t = ParseLong(Attr(s, "t"));
            var d = ParseLong(Attr(s, "d")) ?? 0;
            var repeat = ParseLong(Attr(s, "r")) ?? 0;

            if (t is not null) currentTime = t.Value;
            else if (first) currentTime = 0;

            first = false;

            // r is the number of *additional* repeats; a negative r means "until the period ends",
            // which we cannot expand without a duration, so it contributes one segment.
            var count = repeat >= 0 ? repeat + 1 : 1;
            for (var i = 0L; i < count; i++)
            {
                source.Timeline.Add((currentTime, d));
                currentTime += d;
            }
        }

        return source;
    }

    private static DashSegmentSource ParseSegmentList(XElement element, Uri baseUri)
    {
        var source = new DashSegmentListSource
        {
            Timescale = ParseLong(Attr(element, "timescale")) ?? 1,
            SegmentDuration = ParseLong(Attr(element, "duration")),
            InitializationUri = ResolveUri(Attr(Child(element, "Initialization"), "sourceURL"), baseUri),
            InitializationRange = Attr(Child(element, "Initialization"), "range"),
        };

        foreach (var segment in Children(element, "SegmentURL"))
        {
            var uri = ResolveUri(Attr(segment, "media"), baseUri) ?? baseUri;
            source.Segments.Add((uri, Attr(segment, "mediaRange")));
        }

        return source;
    }

    /// <summary>Applies any BaseURL children of <paramref name="element"/> on top of the inherited base.</summary>
    private static Uri CombineBaseUrls(Uri inherited, XElement element)
    {
        var current = inherited;

        foreach (var baseUrl in Children(element, "BaseURL"))
        {
            var value = baseUrl.Value.Trim();
            if (value.Length == 0) continue;
            if (Uri.TryCreate(current, value, out var combined)) current = combined;
        }

        return current;
    }

    private static Uri? ResolveUri(string? reference, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        return Uri.TryCreate(baseUri, reference.Trim(), out var uri) ? uri : null;
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static XElement? Child(XElement? parent, string localName) =>
        parent is null ? null : Children(parent, localName).FirstOrDefault();

    private static string? Attr(XElement? element, string name)
    {
        if (element is null) return null;

        var attribute = element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(attribute?.Value) ? null : attribute!.Value;
    }

    internal static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try { return XmlConvert.ToTimeSpan(value!.Trim()); }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    /// <summary>frameRate is either "30" or a rational "30000/1001".</summary>
    internal static double? ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value!.Trim();
        var slash = text.IndexOf('/');

        if (slash < 0) return ParseDoubleInvariant(text);

        var numerator = ParseDoubleInvariant(text[..slash]);
        var denominator = ParseDoubleInvariant(text[(slash + 1)..]);

        return numerator is null || denominator is null or 0 ? null : numerator / denominator;
    }

    private static double? ParseDoubleInvariant(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
