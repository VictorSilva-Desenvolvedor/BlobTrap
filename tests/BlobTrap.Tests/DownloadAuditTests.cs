using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O log existe para o usuário conferir "melhor qualidade, com som" enquanto baixa. Estes
/// testes travam as três situações em que a resposta é NÃO — mudo, qualidade abaixo da melhor,
/// ffmpeg ausente — porque são as que não fazem barulho: o download termina sem erro e o
/// arquivo é que sai errado.
/// </summary>
public class DownloadAuditTests
{
    [Fact]
    public void Origem_ListaAsMelhoresOpcoesPrimeiro()
    {
        var texto = DownloadAudit.DescribeSource(Fonte(Video(720), Video(1080), Audio(129_000), Audio(141_000)));

        Assert.Contains("2 video(s), 2 audio(s)", texto);
        Assert.True(texto.IndexOf("1080p", StringComparison.Ordinal) < texto.IndexOf("720p", StringComparison.Ordinal));
    }

    [Fact]
    public void Origem_SoMostraOsTresMelhores_EContaOResto()
    {
        var texto = DownloadAudit.DescribeSource(
            Fonte(Video(1080), Video(720), Video(480), Video(360), Video(240)));

        Assert.Contains("+2", texto);
        Assert.DoesNotContain("240p", texto);
    }

    [Fact]
    public void Origem_NaoGravaAUrlInteira()
    {
        var fonte = Fonte(Video(1080));

        var texto = DownloadAudit.DescribeSource(fonte);

        Assert.Contains("cdn.exemplo.com", texto);
        Assert.DoesNotContain("token=", texto);
    }

    [Fact]
    public void MelhorQualidadeComAudio_NaoGeraAviso()
    {
        var video = Video(1080);
        var audio = Audio(141_000);
        var plano = Plano(Fonte(video, audio), video, audio);

        var linhas = DownloadAudit.DescribePlan(plano, ffmpegAvailable: true);

        Assert.Single(linhas);
        Assert.Equal(LogLevel.Info, linhas[0].Level);
        Assert.Contains("1080p", linhas[0].Message);
    }

    [Fact]
    public void VideoSemAudioPareado_AvisaQueOArquivoSaiMudo()
    {
        var video = Video(1080);
        var plano = Plano(Fonte(video), video, audio: null);

        var linhas = DownloadAudit.DescribePlan(plano, ffmpegAvailable: true);

        Assert.Contains(linhas, l => l.Level == LogLevel.Warn && l.Message.Contains("SEM AUDIO"));
    }

    [Fact]
    public void SomenteAudio_NaoReclamaDeVideoMudo()
    {
        var video = Video(1080);
        var audio = Audio(141_000);
        var plano = Plano(Fonte(video, audio), video, audio, audioOnly: true);

        var linhas = DownloadAudit.DescribePlan(plano, ffmpegAvailable: true);

        Assert.DoesNotContain(linhas, l => l.Level == LogLevel.Warn);
    }

    [Fact]
    public void QualidadeAbaixoDaMelhor_AvisaQualEraAMelhor()
    {
        var melhor = Video(1080);
        var escolhido = Video(720);
        var audio = Audio(141_000);
        var plano = Plano(Fonte(melhor, escolhido, audio), escolhido, audio);

        var linhas = DownloadAudit.DescribePlan(plano, ffmpegAvailable: true);

        var aviso = Assert.Single(linhas, l => l.Level == LogLevel.Warn);
        Assert.Contains("720p", aviso.Message);
        Assert.Contains("1080p", aviso.Message);
    }

    [Fact]
    public void AudioAbaixoDoMelhor_Avisa()
    {
        var video = Video(1080);
        var fraco = Audio(49_000);
        var plano = Plano(Fonte(video, fraco, Audio(141_000)), video, fraco);

        var linhas = DownloadAudit.DescribePlan(plano, ffmpegAvailable: true);

        Assert.Contains(linhas, l => l.Level == LogLevel.Warn && l.Message.Contains("audio abaixo"));
    }

    /// <summary>
    /// O caso desta máquina: sem ffmpeg, video e audio baixam inteiros e ficam em arquivos
    /// separados. Sem este aviso no log, o usuário so descobre ao abrir a pasta.
    /// </summary>
    [Fact]
    public void SemFfmpeg_ComFaixasSeparadas_AvisaQueNaoVaiJuntar()
    {
        var video = Video(1080);
        var audio = Audio(141_000);
        var plano = Plano(Fonte(video, audio), video, audio);

        var linhas = DownloadAudit.DescribePlan(plano, ffmpegAvailable: false);

        Assert.Contains(linhas, l => l.Level == LogLevel.Warn && l.Message.Contains("DOIS arquivos"));
    }

    [Fact]
    public void SemFfmpeg_ComFaixaMuxadaProgressiva_NaoAvisa()
    {
        var muxada = new MediaVariant
        {
            Id = "m", Url = new Uri("https://cdn.exemplo.com/completo.mp4"),
            Track = TrackKind.Muxed, Delivery = DeliveryMode.Progressive, Height = 720,
        };
        var plano = Plano(Fonte(muxada), muxada, audio: null);

        var linhas = DownloadAudit.DescribePlan(plano, ffmpegAvailable: false);

        Assert.DoesNotContain(linhas, l => l.Level == LogLevel.Warn);
    }

    // ----- apoio -----

    private static MediaVariant Video(int altura) => new()
    {
        Id = $"v{altura}",
        Url = new Uri($"https://cdn.exemplo.com/v{altura}.mp4?token=SEGREDO"),
        Track = TrackKind.VideoOnly,
        Delivery = DeliveryMode.DashSegments,
        Height = altura,
        Bandwidth = altura * 4_000L,
        Codecs = "avc1.64002a",
    };

    private static MediaVariant Audio(long banda) => new()
    {
        Id = $"a{banda}",
        Url = new Uri($"https://cdn.exemplo.com/a{banda}.m4a"),
        Track = TrackKind.AudioOnly,
        Delivery = DeliveryMode.DashSegments,
        Bandwidth = banda,
        Codecs = "mp4a.40.2",
    };

    private static MediaSource Fonte(params MediaVariant[] variantes) => new()
    {
        Id = "s",
        Url = new Uri("https://cdn.exemplo.com/manifesto.mpd?token=SEGREDO"),
        Kind = MediaKind.DashManifest,
        Request = RequestContext.Default,
        Variants = variantes,
        ResolvedBy = "dash",
    };

    private static DownloadPlan Plano(MediaSource fonte, MediaVariant video, MediaVariant? audio, bool audioOnly = false) => new()
    {
        Source = fonte,
        Video = video,
        Audio = audio,
        OutputPath = @"C:\Videos\teste.mp4",
        AudioOnly = audioOnly,
    };
}
