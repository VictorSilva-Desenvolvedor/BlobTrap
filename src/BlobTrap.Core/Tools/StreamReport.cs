using System.Globalization;
using System.Text.RegularExpressions;

namespace BlobTrap.Core.Tools;

public enum StreamType
{
    Video,
    Audio,
    Subtitle,
}

/// <summary>Uma faixa do arquivo, como o ffmpeg a descreve.</summary>
public sealed record StreamInfo(
    StreamType Type,
    string Codec,
    int? Width = null,
    int? Height = null,
    int? SampleRate = null,
    string? Channels = null,
    int? BitrateKbps = null)
{
    public override string ToString() => Type switch
    {
        StreamType.Video => $"video {Codec}" + (Width is null ? string.Empty : $" {Width}x{Height}") + Kbps(),
        StreamType.Audio => $"audio {Codec}"
            + (SampleRate is null ? string.Empty : $" {SampleRate} Hz")
            + (Channels is null ? string.Empty : $" {Channels}") + Kbps(),
        _ => $"legenda {Codec}",
    };

    private string Kbps() => BitrateKbps is null ? string.Empty : $" {BitrateKbps} kb/s";
}

/// <summary>
/// O que o ffmpeg diz sobre um arquivo ao abri-lo (<c>ffmpeg -i arquivo</c>), em forma de dado.
///
/// Lê o stderr do ffmpeg e não o ffprobe porque o ffprobe nem sempre está instalado: o
/// instalador do BlobTrap baixa só o ffmpeg. O formato dessas linhas é estável há anos, mas
/// é texto feito para gente — por isso o parser nunca lança: o que não reconhece, ignora, e
/// quem chama trata "nenhuma faixa lida" como "não consegui conferir", não como "sem áudio".
/// </summary>
public sealed partial class StreamReport
{
    public StreamReport(IReadOnlyList<StreamInfo> streams, TimeSpan? duration)
    {
        Streams = streams;
        Duration = duration;
    }

    public IReadOnlyList<StreamInfo> Streams { get; }
    public TimeSpan? Duration { get; }

    /// <summary>False quando nada foi lido: arquivo ilegível ou saída que não reconhecemos.</summary>
    public bool HasStreams => Streams.Count > 0;

    public StreamInfo? Video => Streams.FirstOrDefault(s => s.Type == StreamType.Video);
    public StreamInfo? Audio => Streams.FirstOrDefault(s => s.Type == StreamType.Audio);

    public static StreamReport Parse(string ffmpegOutput)
    {
        var streams = new List<StreamInfo>();
        TimeSpan? duration = null;

        foreach (var line in ffmpegOutput.Split('\n'))
        {
            if (duration is null && DurationPattern().Match(line) is { Success: true } durationMatch)
                duration = FfmpegRunner.ParseTimestamp(durationMatch.Groups[1].Value);

            if (StreamPattern().Match(line) is not { Success: true } match) continue;

            // Capa de álbum embutida aparece como faixa de vídeo; contá-la faria um MP3 passar
            // por "tem vídeo".
            if (line.Contains("(attached pic)", StringComparison.Ordinal)) continue;

            var type = match.Groups[1].Value switch
            {
                "Video" => StreamType.Video,
                "Audio" => StreamType.Audio,
                _ => StreamType.Subtitle,
            };

            var codec = match.Groups[2].Value;
            var rest = match.Groups[3].Value;

            streams.Add(type switch
            {
                StreamType.Video => ParseVideo(codec, rest),
                StreamType.Audio => ParseAudio(codec, rest),
                _ => new StreamInfo(type, codec),
            });
        }

        return new StreamReport(streams, duration);
    }

    private static StreamInfo ParseVideo(string codec, string rest)
    {
        int? width = null, height = null;

        // ", 1920x1080" - exigir a vírgula evita casar o "0x31637661" do código do codec.
        if (ResolutionPattern().Match(rest) is { Success: true } size)
        {
            width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
            height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return new StreamInfo(StreamType.Video, codec, Width: width, Height: height, BitrateKbps: Kbps(rest));
    }

    private static StreamInfo ParseAudio(string codec, string rest)
    {
        int? sampleRate = null;
        string? channels = null;

        if (SampleRatePattern().Match(rest) is { Success: true } hz)
        {
            sampleRate = int.Parse(hz.Groups[1].Value, CultureInfo.InvariantCulture);
            channels = hz.Groups[2].Value.Trim();
        }

        return new StreamInfo(StreamType.Audio, codec, SampleRate: sampleRate, Channels: channels, BitrateKbps: Kbps(rest));
    }

    private static int? Kbps(string text) =>
        BitratePattern().Match(text) is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"^\s*Stream #\d+:\d+[^:]*:\s*(Video|Audio|Subtitle):\s*([A-Za-z0-9_]+)(.*)$")]
    private static partial Regex StreamPattern();

    [GeneratedRegex(@"Duration:\s*(\d+:\d{2}:\d{2}\.\d+)")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@",\s*(\d{2,5})x(\d{2,5})")]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"(\d+) Hz,\s*([^,]+)")]
    private static partial Regex SampleRatePattern();

    [GeneratedRegex(@"(\d+) kb/s")]
    private static partial Regex BitratePattern();
}
