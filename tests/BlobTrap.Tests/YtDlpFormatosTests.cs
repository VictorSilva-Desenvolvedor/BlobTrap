using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Tools;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A lista de qualidade só serve se cada linha for uma escolha diferente.
///
/// O YouTube publica a mesma faixa por mais de um caminho — HLS e DASH, ou uma variante
/// "premium" com os mesmos números — e o yt-dlp devolve todas. Na tela isso aparecia como
/// linhas idênticas: mesma resolução, mesmo bitrate, mesmo codec, mesmo tamanho. Escolher
/// entre duas linhas iguais não é escolha.
/// </summary>
public class YtDlpFormatosTests
{
    [Fact]
    public void FormatosVisualmenteIdenticos_ViramUmaLinhaSo()
    {
        // Os dois primeiros diferem so' no protocolo e no format_id - nada que apareca na tela.
        var json = Probe("""
            {"format_id":"617","protocol":"m3u8_native","ext":"mp4","height":1080,"tbr":12364.882,
             "vcodec":"vp09.00.41.08","acodec":"none","url":"https://e.com/a","filesize":8200000000},
            {"format_id":"303","protocol":"https","ext":"mp4","height":1080,"tbr":12364.882,
             "vcodec":"vp09.00.41.08","acodec":"none","url":"https://e.com/b","filesize":8200000000},
            {"format_id":"299","protocol":"https","ext":"mp4","height":1080,"tbr":5145.933,
             "vcodec":"avc1.64002a","acodec":"none","url":"https://e.com/c","filesize":1010253448}
            """);

        var video = json.VideoVariants.ToList();

        Assert.Equal(2, video.Count);
        Assert.Equal("617", video.First(v => v.Label.Contains("VP9")).ExternalFormatId);
    }

    [Fact]
    public void FormatosQueDiferemNoRotulo_ContinuamOsDois()
    {
        var source = Probe("""
            {"format_id":"a","protocol":"https","ext":"mp4","height":1080,"tbr":5000,
             "vcodec":"avc1.64002a","acodec":"none","url":"https://e.com/a","filesize":100},
            {"format_id":"b","protocol":"https","ext":"mp4","height":1080,"tbr":4000,
             "vcodec":"avc1.64002a","acodec":"none","url":"https://e.com/b","filesize":90}
            """);

        Assert.Equal(2, source.VideoVariants.Count());
    }

    [Fact]
    public void MesmoRotuloMasContainerDiferente_ContinuamOsDois()
    {
        // webm e mp4 sao escolhas de verdade: mudam o arquivo que o usuario recebe.
        var source = Probe("""
            {"format_id":"a","protocol":"https","ext":"webm","height":1080,"tbr":4000,
             "vcodec":"vp9","acodec":"none","url":"https://e.com/a","filesize":100},
            {"format_id":"b","protocol":"https","ext":"mp4","height":1080,"tbr":4000,
             "vcodec":"vp9","acodec":"none","url":"https://e.com/b","filesize":100}
            """);

        Assert.Equal(2, source.VideoVariants.Count());
    }

    [Fact]
    public void AudioEVideoComOsMesmosNumeros_NaoSeAnulam()
    {
        // A chave inclui o tipo da faixa: um audio nunca pode eliminar um video.
        var source = Probe("""
            {"format_id":"v","protocol":"https","ext":"mp4","height":1080,"tbr":4000,
             "vcodec":"vp9","acodec":"none","url":"https://e.com/v","filesize":100},
            {"format_id":"a","protocol":"https","ext":"m4a","tbr":4000,
             "vcodec":"none","acodec":"mp4a.40.2","url":"https://e.com/a","filesize":100}
            """);

        Assert.Single(source.VideoVariants);
        Assert.Single(source.AudioVariants);
    }

    [Fact]
    public void BitrateVemEmKbpsEViraBitsPorSegundo()
    {
        // yt-dlp reporta tbr em kbps. Ler como bps daria 12 kbps num video de 12 Mbps.
        var source = Probe("""
            {"format_id":"a","protocol":"https","ext":"mp4","height":1080,"tbr":12364.882,
             "vcodec":"vp9","acodec":"none","url":"https://e.com/a","filesize":100}
            """);

        Assert.Equal(12_364_882, source.VideoVariants.Single().Bandwidth);
    }

    private static MediaSource Probe(string formatos) =>
        YtDlpRunner.ParseProbe(
            $$"""{"id":"abc","title":"Teste","duration":1571,"formats":[{{formatos}}]}""",
            new Uri("https://www.youtube.com/watch?v=abc"),
            RequestContext.Default);
}
