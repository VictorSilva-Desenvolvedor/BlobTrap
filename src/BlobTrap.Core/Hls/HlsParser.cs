using System.Globalization;

namespace BlobTrap.Core.Hls;

/// <summary>Parses HLS playlists (RFC 8216). Tolerant: unknown tags are skipped, not fatal.</summary>
public static class HlsParser
{
    public static bool LooksLikePlaylist(string text) =>
        text.AsSpan().TrimStart().StartsWith("#EXTM3U");

    /// <summary>A master playlist advertises variants; a media playlist lists segments.</summary>
    public static bool IsMaster(string text)
    {
        foreach (var line in EnumerateLines(text))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) return true;
            if (line.StartsWith("#EXTINF", StringComparison.Ordinal)) return false;
            if (line.StartsWith("#EXT-X-TARGETDURATION", StringComparison.Ordinal)) return false;
        }

        // A master with only EXT-X-MEDIA rows (audio-only packaging) still counts as one.
        return text.Contains("#EXT-X-MEDIA:", StringComparison.Ordinal);
    }

    public static HlsPlaylist Parse(string text, Uri baseUri) =>
        IsMaster(text) ? ParseMaster(text, baseUri) : ParseMedia(text, baseUri);

    public static HlsMasterPlaylist ParseMaster(string text, Uri baseUri)
    {
        var playlist = new HlsMasterPlaylist { BaseUri = baseUri };
        HlsAttributes? pendingStreamInf = null;
        var pendingIsIFrame = false;

        foreach (var line in EnumerateLines(text))
        {
            if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal))
            {
                playlist.Version = ParseInt(line["#EXT-X-VERSION:".Length..]) ?? 1;
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var attributes = HlsAttributes.Parse(line["#EXT-X-MEDIA:".Length..]);
                var type = attributes.Get("TYPE");
                var groupId = attributes.Get("GROUP-ID");
                if (type is null || groupId is null) continue;

                playlist.Renditions.Add(new HlsRendition
                {
                    Type = type,
                    GroupId = groupId,
                    Name = attributes.Get("NAME"),
                    Language = attributes.Get("LANGUAGE"),
                    IsDefault = attributes.GetBool("DEFAULT"),
                    AutoSelect = attributes.GetBool("AUTOSELECT"),
                    Forced = attributes.GetBool("FORCED"),
                    Uri = Resolve(attributes.Get("URI"), baseUri),
                    Channels = attributes.Get("CHANNELS"),
                    Characteristics = attributes.Get("CHARACTERISTICS"),
                });
            }
            else if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
            {
                var key = ParseKey(line["#EXT-X-SESSION-KEY:".Length..], baseUri, 0);
                if (key is not null) playlist.SessionKeys.Add(key);
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pendingStreamInf = HlsAttributes.Parse(line["#EXT-X-STREAM-INF:".Length..]);
                pendingIsIFrame = false;
            }
            else if (line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal))
            {
                // Trick-play variants carry their URI inline instead of on the next line.
                var attributes = HlsAttributes.Parse(line["#EXT-X-I-FRAME-STREAM-INF:".Length..]);
                var uri = Resolve(attributes.Get("URI"), baseUri);
                if (uri is not null)
                    playlist.Variants.Add(BuildVariant(attributes, uri, isIFrameOnly: true));
            }
            else if (!line.StartsWith('#'))
            {
                if (pendingStreamInf is null) continue;

                var uri = Resolve(line, baseUri);
                if (uri is not null)
                    playlist.Variants.Add(BuildVariant(pendingStreamInf, uri, pendingIsIFrame));

                pendingStreamInf = null;
            }
        }

        return playlist;
    }

    private static HlsVariantStream BuildVariant(HlsAttributes attributes, Uri uri, bool isIFrameOnly)
    {
        var (width, height) = ParseResolution(attributes.Get("RESOLUTION"));

        return new HlsVariantStream
        {
            Uri = uri,
            Bandwidth = ParseLong(attributes.Get("BANDWIDTH")),
            AverageBandwidth = ParseLong(attributes.Get("AVERAGE-BANDWIDTH")),
            Width = width,
            Height = height,
            Codecs = attributes.Get("CODECS"),
            FrameRate = ParseDouble(attributes.Get("FRAME-RATE")),
            VideoRange = attributes.Get("VIDEO-RANGE"),
            AudioGroupId = attributes.Get("AUDIO"),
            SubtitlesGroupId = attributes.Get("SUBTITLES"),
            IsIFrameOnly = isIFrameOnly,
        };
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        var playlist = new HlsMediaPlaylist { BaseUri = baseUri };

        HlsKey? currentKey = null;
        HlsInitSegment? currentMap = null;
        double pendingDuration = 0;
        string? pendingTitle = null;
        long? pendingRangeLength = null;
        long? pendingRangeOffset = null;
        var pendingDiscontinuity = false;
        var sequence = 0L;
        var sawSequence = false;

        // With EXT-X-BYTERANGE and no offset, a segment starts where the previous one ended.
        var lastRangeEndByUri = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var line in EnumerateLines(text))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var payload = line["#EXTINF:".Length..];
                var comma = payload.IndexOf(',');
                var durationText = comma >= 0 ? payload[..comma] : payload;
                pendingDuration = ParseDouble(durationText) ?? 0;
                pendingTitle = comma >= 0 && comma + 1 < payload.Length ? payload[(comma + 1)..].Trim() : null;
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                playlist.TargetDuration = ParseDouble(line["#EXT-X-TARGETDURATION:".Length..]) ?? 0;
            }
            else if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal))
            {
                playlist.Version = ParseInt(line["#EXT-X-VERSION:".Length..]) ?? 1;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                sequence = ParseLong(line["#EXT-X-MEDIA-SEQUENCE:".Length..]) ?? 0;
                playlist.MediaSequence = sequence;
                sawSequence = true;
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
            {
                playlist.PlaylistType = line["#EXT-X-PLAYLIST-TYPE:".Length..].Trim();
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                currentKey = ParseKey(line["#EXT-X-KEY:".Length..], baseUri, sequence);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var attributes = HlsAttributes.Parse(line["#EXT-X-MAP:".Length..]);
                var uri = Resolve(attributes.Get("URI"), baseUri);
                if (uri is not null)
                {
                    var (length, offset) = ParseByteRange(attributes.Get("BYTERANGE"));
                    currentMap = new HlsInitSegment
                    {
                        Uri = uri,
                        ByteRangeLength = length,
                        ByteRangeOffset = length is not null ? offset ?? 0 : null,
                    };
                }
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                (pendingRangeLength, pendingRangeOffset) = ParseByteRange(line["#EXT-X-BYTERANGE:".Length..]);
            }
            else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
            {
                pendingDiscontinuity = true;
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                playlist.HasEndList = true;
            }
            else if (line.StartsWith("#EXT-X-I-FRAMES-ONLY", StringComparison.Ordinal))
            {
                playlist.IsIFrameOnly = true;
            }
            else if (!line.StartsWith('#'))
            {
                var uri = Resolve(line, baseUri);
                if (uri is null) continue;

                long? offset = pendingRangeOffset;
                if (pendingRangeLength is not null && offset is null)
                    offset = lastRangeEndByUri.TryGetValue(uri.AbsoluteUri, out var end) ? end : 0;

                if (pendingRangeLength is not null)
                    lastRangeEndByUri[uri.AbsoluteUri] = (offset ?? 0) + pendingRangeLength.Value;

                var segmentSequence = sawSequence ? sequence : playlist.Segments.Count;

                playlist.Segments.Add(new HlsSegment
                {
                    Uri = uri,
                    Duration = pendingDuration,
                    Title = pendingTitle,
                    MediaSequence = segmentSequence,
                    Discontinuity = pendingDiscontinuity,
                    ByteRangeLength = pendingRangeLength,
                    ByteRangeOffset = offset,
                    Key = currentKey is { IsNone: false } ? WithDerivedIv(currentKey, segmentSequence) : null,
                    Map = currentMap,
                });

                sequence++;
                pendingDuration = 0;
                pendingTitle = null;
                pendingRangeLength = null;
                pendingRangeOffset = null;
                pendingDiscontinuity = false;
            }
        }

        return playlist;
    }

    /// <summary>
    /// When EXT-X-KEY omits IV, the IV is the segment's media sequence number as a 128-bit
    /// big-endian integer (RFC 8216 section 5.2). Getting this wrong yields garbage output.
    /// </summary>
    private static HlsKey WithDerivedIv(HlsKey key, long mediaSequence)
    {
        if (key.Iv is not null) return key;

        var iv = new byte[16];
        var value = (ulong)mediaSequence;
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(value >> (8 * i));

        return new HlsKey
        {
            Method = key.Method,
            Uri = key.Uri,
            Iv = iv,
            KeyFormat = key.KeyFormat,
            KeyFormatVersions = key.KeyFormatVersions,
        };
    }

    private static HlsKey? ParseKey(string attributeList, Uri baseUri, long mediaSequence)
    {
        var attributes = HlsAttributes.Parse(attributeList);
        var method = attributes.Get("METHOD");
        if (method is null) return null;

        return new HlsKey
        {
            Method = method,
            Uri = Resolve(attributes.Get("URI"), baseUri),
            Iv = ParseHex(attributes.Get("IV")),
            KeyFormat = attributes.Get("KEYFORMAT"),
            KeyFormatVersions = attributes.Get("KEYFORMATVERSIONS"),
        };
    }

    /// <summary>Parses "&lt;length&gt;[@&lt;offset&gt;]".</summary>
    internal static (long? Length, long? Offset) ParseByteRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);

        var trimmed = value!.Trim().Trim('"');
        var at = trimmed.IndexOf('@');

        if (at < 0) return (ParseLong(trimmed), null);
        return (ParseLong(trimmed[..at]), ParseLong(trimmed[(at + 1)..]));
    }

    internal static byte[]? ParseHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value!.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (text.Length == 0 || text.Length % 2 != 0) return null;

        try { return Convert.FromHexString(text); }
        catch (FormatException) { return null; }
    }

    private static (int? Width, int? Height) ParseResolution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);

        var parts = value!.Split('x', 'X');
        if (parts.Length != 2) return (null, null);

        return (ParseInt(parts[0]), ParseInt(parts[1]));
    }

    /// <summary>Resolves a playlist line against the playlist's own URL, honouring absolute URLs.</summary>
    internal static Uri? Resolve(string? reference, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var trimmed = reference!.Trim().Trim('"');
        if (trimmed.Length == 0) return null;

        return Uri.TryCreate(baseUri, trimmed, out var absolute) ? absolute : null;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result : null;
}

/// <summary>
/// An HLS attribute list. Splitting on commas naively breaks CODECS="a,b", so this walks
/// the string tracking quote state.
/// </summary>
internal sealed class HlsAttributes
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static HlsAttributes Parse(string input)
    {
        var attributes = new HlsAttributes();

        var start = 0;
        var inQuotes = false;

        for (var i = 0; i <= input.Length; i++)
        {
            if (i < input.Length)
            {
                if (input[i] == '"') { inQuotes = !inQuotes; continue; }
                if (input[i] != ',' || inQuotes) continue;
            }

            var pair = input[start..i];
            start = i + 1;

            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            var key = pair[..equals].Trim();
            var value = pair[(equals + 1)..].Trim().Trim('"');
            if (key.Length > 0) attributes._values[key] = value;
        }

        return attributes;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    public bool GetBool(string key) =>
        string.Equals(Get(key), "YES", StringComparison.OrdinalIgnoreCase);
}
