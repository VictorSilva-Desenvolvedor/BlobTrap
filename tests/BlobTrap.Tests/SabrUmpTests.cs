using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Sniffing;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O YouTube migrou para SABR/UMP: o player pede /videoplayback por POST e recebe
/// <c>application/vnd.yt-ump</c>, um container proprio. O corpus de <see cref="DeteccaoRealTests"/>
/// foi capturado antes disso - continua valido para o que cobre, e nao pega esta migracao.
/// </summary>
public class SabrUmpTests
{
// ===== 1. SABR/UMP do YouTube =====

    [Theory]
    [InlineData("application/vnd.yt-ump")]
    [InlineData("application/vnd.yt-ump; charset=utf-8")]
    [InlineData("APPLICATION/VND.YT-UMP")]
    public void UmpEhReconhecidoComoProtocoloOpaco(string mime)
    {
        Assert.True(MediaClassifier.IsOpaqueStreamingProtocol(mime));
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("application/dash+xml")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public void MimeComumNaoEhProtocoloOpaco(string? mime)
    {
        Assert.False(MediaClassifier.IsOpaqueStreamingProtocol(mime));
    }

    /// <summary>
    /// O candidato tem que ser A PÁGINA, não a URL do videoplayback: `MediaResolver` manda
    /// `candidate.Url` para o yt-dlp quando o tipo é PageEmbed, e o yt-dlp não extrai nada de
    /// uma URL do googlevideo. Registrar a URL da mídia produziria um candidato que falha.
    /// </summary>
    [Fact]
    public void RespostaSabr_RegistraAPaginaEnaoAUrlDaMidia()
    {
        var registry = new MediaRegistry();
        var pagina = new Uri("https://www.youtube.com/watch?v=dARQxEyPkds");

        var candidato = registry.Observe(
            new Uri("https://rr7---sn-jucj.googlevideo.com/videoplayback?sabr=1&rqh=1&expire=1788552631"),
            "application/vnd.yt-ump",
            RequestContext.Default,
            pageUrl: pagina,
            pageTitle: "Explicando o jogo de TERROR",
            resourceType: "Fetch");

        Assert.NotNull(candidato);
        Assert.Equal(MediaKind.PageEmbed, candidato!.Kind);
        Assert.Equal(pagina, candidato.Url);
    }

    [Fact]
    public void DezenasDeRespostasSabr_ViramUmCandidatoSo()
    {
        var registry = new MediaRegistry();
        var pagina = new Uri("https://www.youtube.com/watch?v=abc");

        for (var i = 0; i < 12; i++)
        {
            registry.Observe(
                new Uri($"https://rr7---sn-jucj.googlevideo.com/videoplayback?sabr=1&range={i}0000-{i}9999"),
                "application/vnd.yt-ump", RequestContext.Default, pageUrl: pagina, resourceType: "Fetch");
        }

        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void SabrSemPaginaConhecida_NaoOfereceNada()
    {
        // Sem a pagina nao ha o que mandar para o yt-dlp; oferecer a URL do googlevideo seria
        // entregar um candidato que nao resolve.
        var registry = new MediaRegistry();

        var candidato = registry.Observe(
            new Uri("https://rr7---sn-jucj.googlevideo.com/videoplayback?sabr=1"),
            "application/vnd.yt-ump", RequestContext.Default, resourceType: "Fetch");

        Assert.Null(candidato);
    }
}
