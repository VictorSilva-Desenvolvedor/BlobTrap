using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O YouTube não serve mais faixa combinada: o vídeo de 26 min usado como referência tem 31
/// faixas só de vídeo, 10 só de áudio e ZERO muxadas. Toda opção de qualidade é, por
/// definição, "só vídeo" — e é o pareamento que faz o arquivo final ter som.
///
/// Estes testes existem porque a interface chegou a anunciar "sem áudio" em cada uma das 31
/// linhas, o que dizia ao usuário exatamente o oposto do que ia acontecer.
/// </summary>
public class PareamentoDeAudioTests
{
    [Fact]
    public void TodaFaixaSoDeVideo_RecebeUmAudioParaParear()
    {
        var source = FonteEstiloYoutube();

        Assert.All(source.VideoVariants, v =>
        {
            Assert.Equal(TrackKind.VideoOnly, v.Track);
            Assert.NotNull(source.BestAudioFor(v));
        });
    }

    [Fact]
    public void OMelhorAudio_EhODeMaiorBitrate()
    {
        var source = FonteEstiloYoutube();
        var video = source.BestVideo!;

        var escolhido = source.BestAudioFor(video);

        Assert.NotNull(escolhido);
        Assert.Equal(141_000, escolhido!.Bandwidth);
    }

    [Fact]
    public void FaixaMuxada_NaoPedeAudioSeparado()
    {
        var muxada = new MediaVariant
        {
            Id = "m", Url = new Uri("https://e.com/completo.mp4"),
            Track = TrackKind.Muxed, Delivery = DeliveryMode.Progressive, Height = 720,
        };

        var source = Fonte(new[] { muxada });

        Assert.Null(source.BestAudioFor(muxada));
    }

    /// <summary>
    /// O único caso em que "sem áudio" é informação de verdade: não há o que parear, e o
    /// arquivo sai mudo. É o que o aviso do diálogo passou a cobrir.
    /// </summary>
    [Fact]
    public void SemNenhumAudioNaOrigem_NaoHaOQueParear()
    {
        var video = new MediaVariant
        {
            Id = "v", Url = new Uri("https://e.com/v.mp4"),
            Track = TrackKind.VideoOnly, Delivery = DeliveryMode.DashSegments, Height = 1080,
        };

        var source = Fonte(new[] { video });

        Assert.Empty(source.AudioVariants);
        Assert.Null(source.BestAudioFor(video));
    }

    [Fact]
    public void RotuloDeFaixaSoDeVideo_NaoAnunciaAusenciaDeAudio()
    {
        var video = FonteEstiloYoutube().BestVideo!;

        // O rotulo descreve a faixa. O que acontece com o audio e' dito uma vez, ao lado do
        // seletor, e nao repetido em cada linha da lista.
        Assert.DoesNotContain("sem áudio", video.Label);
        Assert.Contains("1080p", video.Label);
    }

    // ----- apoio -----

    /// <summary>Reproduz a forma real: só vídeo e só áudio, nenhuma faixa combinada.</summary>
    private static MediaSource FonteEstiloYoutube()
    {
        var variantes = new List<MediaVariant>();

        foreach (var (altura, banda, codec) in new[]
                 {
                     (1080, 5_146_000L, "avc1.64002a"),
                     (1080, 4_250_000L, "vp9"),
                     (1080, 4_171_000L, "av01.0.09M.08"),
                     (720, 2_000_000L, "avc1.4d401f"),
                 })
        {
            variantes.Add(new MediaVariant
            {
                Id = $"v{altura}-{codec}",
                Url = new Uri("https://rr7.googlevideo.com/videoplayback?itag=" + altura),
                Track = TrackKind.VideoOnly,
                Delivery = DeliveryMode.Progressive,
                Height = altura,
                Bandwidth = banda,
                Codecs = codec,
            });
        }

        foreach (var (banda, codec) in new[]
                 {
                     (49_000L, "mp4a.40.5"),
                     (129_000L, "mp4a.40.2"),
                     (141_000L, "opus"),
                 })
        {
            variantes.Add(new MediaVariant
            {
                Id = $"a{banda}",
                Url = new Uri("https://rr7.googlevideo.com/videoplayback?itag=a" + banda),
                Track = TrackKind.AudioOnly,
                Delivery = DeliveryMode.Progressive,
                Bandwidth = banda,
                Codecs = codec,
            });
        }

        return Fonte(variantes);
    }

    private static MediaSource Fonte(IReadOnlyList<MediaVariant> variantes) => new()
    {
        Id = "s",
        Url = new Uri("https://www.youtube.com/watch?v=abc"),
        Kind = MediaKind.PageEmbed,
        Request = RequestContext.Default,
        Variants = variantes,
    };
}
