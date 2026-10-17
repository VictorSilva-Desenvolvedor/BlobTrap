using BlobTrap.Core.Tools;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Saídas reais do <c>ffmpeg -i</c>. O parser lê texto feito para gente, então a defesa dele
/// é a variedade das amostras: cada forma que já apareceu num arquivo baixado entra aqui.
/// </summary>
public class StreamReportTests
{
    private const string Mp4Completo = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'filme.mp4':
          Metadata:
            major_brand     : isom
          Duration: 00:26:10.52, start: 0.000000, bitrate: 4650 kb/s
          Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 4500 kb/s, 29.97 fps, 29.97 tbr, 30k tbn (default)
          Stream #0:1[0x2](eng): Audio: aac (LC) (mp4a / 0x6d703461), 48000 Hz, stereo, fltp, 128 kb/s (default)
        At least one output file must be specified
        """;

    [Fact]
    public void Mp4ComVideoEAudio_LeAsDuasFaixas()
    {
        var report = StreamReport.Parse(Mp4Completo);

        Assert.True(report.HasStreams);

        Assert.Equal("h264", report.Video!.Codec);
        Assert.Equal(1920, report.Video.Width);
        Assert.Equal(1080, report.Video.Height);
        Assert.Equal(4500, report.Video.BitrateKbps);

        Assert.Equal("aac", report.Audio!.Codec);
        Assert.Equal(48000, report.Audio.SampleRate);
        Assert.Equal("stereo", report.Audio.Channels);
        Assert.Equal(128, report.Audio.BitrateKbps);

        Assert.Equal(TimeSpan.FromSeconds(26 * 60 + 10.52), report.Duration);
    }

    /// <summary>
    /// O código do codec traz "0x31637661"; sem exigir a vírgula antes da resolução, "31x..."
    /// ou similar poderia ser lido como largura.
    /// </summary>
    [Fact]
    public void ResolucaoNaoConfundeComOCodigoHexDoCodec()
    {
        var report = StreamReport.Parse(Mp4Completo);

        Assert.Equal(1920, report.Video!.Width);
    }

    [Fact]
    public void ArquivoSemAudio_NaoTemFaixaDeAudio()
    {
        var report = StreamReport.Parse("""
            Input #0, matroska,webm, from 'mudo.webm':
              Duration: 00:01:00.00, start: 0.000000, bitrate: 2000 kb/s
              Stream #0:0: Video: vp9 (Profile 0), yuv420p(tv), 1280x720, 30 fps, 30 tbr, 1k tbn (default)
            """);

        Assert.True(report.HasStreams);
        Assert.NotNull(report.Video);
        Assert.Null(report.Audio);
        Assert.Equal(720, report.Video!.Height);
    }

    [Fact]
    public void CapaEmbutidaNaoContaComoVideo()
    {
        var report = StreamReport.Parse("""
            Input #0, mp3, from 'musica.mp3':
              Duration: 00:03:30.00, start: 0.025057, bitrate: 320 kb/s
              Stream #0:0: Audio: mp3, 44100 Hz, stereo, fltp, 320 kb/s
              Stream #0:1: Video: mjpeg (Progressive), yuvj444p(pc, bt470bg/unknown/unknown), 500x500, 90k tbr, 90k tbn (attached pic)
            """);

        Assert.Null(report.Video);
        Assert.NotNull(report.Audio);
    }

    [Fact]
    public void DuracaoNaoDisponivel_ContinuaLendoAsFaixas()
    {
        var report = StreamReport.Parse("""
              Duration: N/A, bitrate: N/A
              Stream #0:0: Video: h264 (Main), yuv420p, 640x360, 25 fps
              Stream #0:1: Audio: aac (LC), 44100 Hz, mono, fltp
            """);

        Assert.Null(report.Duration);
        Assert.Equal(2, report.Streams.Count);
        Assert.Equal("mono", report.Audio!.Channels);
    }

    /// <summary>
    /// Arquivo que o ffmpeg não abriu: nada de faixa. Quem chama precisa distinguir isto de
    /// "arquivo sem áudio", senão todo arquivo ilegível vira falso alarme de mudo.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("filme.mp4: Invalid data found when processing input")]
    public void SaidaSemFaixas_NaoTemStreams(string saida)
    {
        Assert.False(StreamReport.Parse(saida).HasStreams);
    }

    [Fact]
    public void SaidaComFimDeLinhaWindows_AindaLe()
    {
        var report = StreamReport.Parse(Mp4Completo.Replace("\n", "\r\n"));

        Assert.Equal(2, report.Streams.Count);
    }
}
