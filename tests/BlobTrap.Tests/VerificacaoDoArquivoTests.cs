using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Tools;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A conferência do arquivo final é a resposta a "baixou na qualidade que escolhi, e com
/// som?". Cada caso aqui é uma forma de o download terminar SEM erro e o arquivo estar errado.
/// </summary>
public class VerificacaoDoArquivoTests
{
    [Fact]
    public void ArquivoCompleto_SoRegistra()
    {
        var linhas = DownloadAudit.VerifyOutput(Plano(1080), Relatorio(video: 1080, audio: true), 1_500_000_000);

        var unica = Assert.Single(linhas);
        Assert.Equal(LogLevel.Info, unica.Level);
        Assert.Contains("1920x1080", unica.Message);
        Assert.Contains("audio aac", unica.Message);
    }

    [Fact]
    public void ArquivoSemFaixaDeAudio_Avisa()
    {
        var linhas = DownloadAudit.VerifyOutput(Plano(1080), Relatorio(video: 1080, audio: false), null);

        Assert.Contains(linhas, l => l.Level == LogLevel.Warn && l.Message.Contains("SEM AUDIO"));
    }

    [Fact]
    public void ResolucaoMenorQueAEscolhida_Avisa()
    {
        var linhas = DownloadAudit.VerifyOutput(Plano(1080), Relatorio(video: 720, audio: true), null);

        var aviso = Assert.Single(linhas, l => l.Level == LogLevel.Warn);
        Assert.Contains("720p", aviso.Message);
        Assert.Contains("1080p", aviso.Message);
    }

    [Fact]
    public void ResolucaoIgualOuMaior_NaoAvisa()
    {
        var linhas = DownloadAudit.VerifyOutput(Plano(720), Relatorio(video: 1080, audio: true), null);

        Assert.DoesNotContain(linhas, l => l.Level == LogLevel.Warn);
    }

    [Fact]
    public void DuracaoMuitoMenorQueAEsperada_AvisaTruncado()
    {
        var plano = Plano(1080, duracaoSegundos: 1000);

        var linhas = DownloadAudit.VerifyOutput(plano, Relatorio(1080, true, duracao: TimeSpan.FromSeconds(400)), null);

        Assert.Contains(linhas, l => l.Level == LogLevel.Warn && l.Message.Contains("truncado"));
    }

    [Fact]
    public void DuracaoDentroDaTolerancia_NaoAvisa()
    {
        var plano = Plano(1080, duracaoSegundos: 1000);

        var linhas = DownloadAudit.VerifyOutput(plano, Relatorio(1080, true, duracao: TimeSpan.FromSeconds(980)), null);

        Assert.DoesNotContain(linhas, l => l.Level == LogLevel.Warn);
    }

    [Fact]
    public void SomenteAudio_NaoExigeVideo()
    {
        var plano = Plano(1080, audioOnly: true);
        var relatorio = new StreamReport(new[] { new StreamInfo(StreamType.Audio, "mp3", SampleRate: 44100, Channels: "stereo") }, null);

        var linhas = DownloadAudit.VerifyOutput(plano, relatorio, null);

        Assert.DoesNotContain(linhas, l => l.Level == LogLevel.Warn);
    }

    /// <summary>
    /// Nada lido não é "mudo": é "não consegui conferir". Acusar mudo seria falso alarme em
    /// todo arquivo que o ffmpeg não abre.
    /// </summary>
    [Fact]
    public void NadaLido_NaoAcusaMudo()
    {
        var linhas = DownloadAudit.VerifyOutput(Plano(1080), StreamReport.Parse(string.Empty), null);

        var aviso = Assert.Single(linhas);
        Assert.Equal(LogLevel.Warn, aviso.Level);
        Assert.DoesNotContain("SEM AUDIO", aviso.Message);
        Assert.Contains("nao pôde ser conferido", aviso.Message);
    }

    // ----- apoio -----

    private static StreamReport Relatorio(int video, bool audio, TimeSpan? duracao = null)
    {
        var faixas = new List<StreamInfo>
        {
            new(StreamType.Video, "h264", Width: video * 16 / 9, Height: video, BitrateKbps: 4500),
        };

        if (audio) faixas.Add(new StreamInfo(StreamType.Audio, "aac", SampleRate: 48000, Channels: "stereo", BitrateKbps: 128));

        return new StreamReport(faixas, duracao);
    }

    private static DownloadPlan Plano(int altura, double? duracaoSegundos = null, bool audioOnly = false)
    {
        var video = new MediaVariant
        {
            Id = "v",
            Url = new Uri("https://cdn.exemplo.com/v.mp4"),
            Track = TrackKind.Muxed,
            Delivery = DeliveryMode.Progressive,
            Height = altura,
            DurationSeconds = duracaoSegundos,
        };

        return new DownloadPlan
        {
            Source = new MediaSource
            {
                Id = "s",
                Url = new Uri("https://cdn.exemplo.com/v.mp4"),
                Kind = MediaKind.ProgressiveVideo,
                Request = RequestContext.Default,
                Variants = new[] { video },
            },
            Video = video,
            OutputPath = @"C:\Videos\teste.mp4",
            AudioOnly = audioOnly,
        };
    }
}
