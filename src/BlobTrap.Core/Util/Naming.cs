using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlobTrap.Core.Util;

/// <summary>Helpers for turning URLs and page titles into safe, human file names.</summary>
public static class Naming
{
    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

    /// <summary>Reserved DOS device names that cannot be used as a file name stem on Windows.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string SanitizeFileName(string? input, string fallback = "video", int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;

        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (InvalidChars.Contains(ch) || char.IsControl(ch)) builder.Append(' ');
            else builder.Append(ch);
        }

        var cleaned = CollapseWhitespace(builder.ToString()).Trim(' ', '.');
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength].TrimEnd(' ', '.');
        if (cleaned.Length == 0) return fallback;
        if (ReservedNames.Contains(cleaned)) return cleaned + "_";
        return cleaned;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace && lastWasSpace) continue;
            builder.Append(isSpace ? ' ' : ch);
            lastWasSpace = isSpace;
        }
        return builder.ToString();
    }

    /// <summary>Picks a display name for a URL: the last meaningful path segment, or the host.</summary>
    public static string NameFromUrl(Uri url)
    {
        var segment = url.Segments.LastOrDefault()?.Trim('/');
        if (!string.IsNullOrWhiteSpace(segment))
        {
            var decoded = Uri.UnescapeDataString(segment);
            var stem = Path.GetFileNameWithoutExtension(decoded);
            if (!string.IsNullOrWhiteSpace(stem)) return SanitizeFileName(stem);
        }
        return SanitizeFileName(url.Host);
    }

    /// <summary>A short, stable id for a URL - used to dedupe sniffed candidates across reloads.</summary>
    public static string StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Ensures the path does not collide with an existing file, appending " (2)", " (3)", ...</summary>
    public static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null or < 0) return "-";
        double value = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string FormatDuration(double? seconds)
    {
        if (seconds is null || double.IsNaN(seconds.Value) || seconds.Value <= 0) return "-";
        var span = TimeSpan.FromSeconds(seconds.Value);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    public static string FormatBitrate(long? bitsPerSecond)
    {
        if (bitsPerSecond is null or <= 0) return "-";
        double value = bitsPerSecond.Value;
        if (value >= 1_000_000) return (value / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps";
        return (value / 1_000).ToString("0", CultureInfo.InvariantCulture) + " kbps";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "-";
        return FormatBytes((long)bytesPerSecond) + "/s";
    }
}
