using System.Globalization;
using System.Text;

namespace BlobTrap.Core.Dash;

/// <summary>
/// Expands DASH segment templates: $RepresentationID$, $Number$, $Bandwidth$, $Time$,
/// with optional printf-style widths such as $Number%05d$. "$$" is a literal dollar sign.
/// </summary>
public static class DashTemplate
{
    public static string Expand(string template, string representationId, long? bandwidth, long? number, long? time)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('$')) return template;

        var result = new StringBuilder(template.Length + 16);
        var i = 0;

        while (i < template.Length)
        {
            var ch = template[i];
            if (ch != '$') { result.Append(ch); i++; continue; }

            // "$$" escapes a literal dollar.
            if (i + 1 < template.Length && template[i + 1] == '$') { result.Append('$'); i += 2; continue; }

            var close = template.IndexOf('$', i + 1);
            if (close < 0) { result.Append(template[i..]); break; }

            var token = template[(i + 1)..close];
            result.Append(Substitute(token, representationId, bandwidth, number, time));
            i = close + 1;
        }

        return result.ToString();
    }

    private static string Substitute(string token, string representationId, long? bandwidth, long? number, long? time)
    {
        var (name, format) = SplitFormat(token);

        return name.ToLowerInvariant() switch
        {
            "representationid" => representationId,
            "bandwidth" => FormatValue(bandwidth ?? 0, format),
            "number" => FormatValue(number ?? 0, format),
            "time" => FormatValue(time ?? 0, format),
            "subnumber" => FormatValue(number ?? 0, format),
            // An unknown token is left as-is so the failure is visible in the URL, not silent.
            _ => "$" + token + "$",
        };
    }

    private static (string Name, string? Format) SplitFormat(string token)
    {
        var percent = token.IndexOf('%');
        return percent < 0 ? (token, null) : (token[..percent], token[percent..]);
    }

    /// <summary>Applies a printf width spec like "%05d". Anything unrecognised falls back to plain digits.</summary>
    private static string FormatValue(long value, string? format)
    {
        if (string.IsNullOrEmpty(format)) return value.ToString(CultureInfo.InvariantCulture);

        var spec = format!.TrimStart('%');
        var suffix = spec.Length > 0 ? spec[^1] : 'd';
        if (suffix is not ('d' or 'u' or 'x' or 'X' or 'o')) return value.ToString(CultureInfo.InvariantCulture);

        var digits = spec[..^1];
        var zeroPadded = digits.StartsWith('0');
        var widthText = zeroPadded ? digits[1..] : digits;

        if (!int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width))
            return value.ToString(CultureInfo.InvariantCulture);

        var rendered = suffix switch
        {
            'x' => value.ToString("x", CultureInfo.InvariantCulture),
            'X' => value.ToString("X", CultureInfo.InvariantCulture),
            'o' => Convert.ToString(value, 8),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        return rendered.PadLeft(width, zeroPadded ? '0' : ' ');
    }
}
