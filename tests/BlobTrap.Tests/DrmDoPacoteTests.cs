using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Sniffing;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A recusa a DRM valia so para o manifesto: os arquivos cifrados do MESMO pacote apareciam
/// como download livre. Baixar um deles entregava 251 MB de bytes que nao tocam em lugar
/// nenhum, e o usuario so descobria depois.
/// </summary>
public class DrmDoPacoteTests
{
// ===== 3. DRM que nao se propagava =====

    [Fact]
    public void ManifestoProtegido_ContaminaOsArquivosDoMesmoPacote()
    {
        var registry = new MediaRegistry();
        const string pasta = "https://media.axprod.net/TestVectors/Cmaf/protected_1080p_h264_cbcs/";

        var video = registry.Observe(
            new Uri(pasta + "video-H264-1080-3000k-video-avc1.mp4"),
            "video/mp4", RequestContext.Default, contentLength: 251_000_000);

        Assert.NotNull(video);
        Assert.False(video!.IsProtected);

        registry.MarkPackageProtected(new Uri(pasta + "manifest.mpd"), "Widevine");

        Assert.True(video.IsProtected);
        Assert.Equal("Widevine", video.ProtectionSystem);
    }

    /// <summary>
    /// A ordem em que o player pede as coisas não pode mudar o resultado: um arquivo que chega
    /// DEPOIS de o manifesto se provar protegido também nasce marcado.
    /// </summary>
    [Fact]
    public void ArquivoQueChegaDepois_JaNasceProtegido()
    {
        var registry = new MediaRegistry();
        const string pasta = "https://media.axprod.net/TestVectors/Cmaf/protected_1080p_h264_cbcs/";

        registry.MarkPackageProtected(new Uri(pasta + "manifest.mpd"), "Widevine");

        var video = registry.Observe(
            new Uri(pasta + "video-H264-720-2100k-video-avc1.mp4"),
            "video/mp4", RequestContext.Default, contentLength: 175_900_000);

        Assert.NotNull(video);
        Assert.True(video!.IsProtected);
    }

    [Fact]
    public void OutroPacoteNoMesmoHost_NaoEhContaminado()
    {
        var registry = new MediaRegistry();

        registry.MarkPackageProtected(
            new Uri("https://media.axprod.net/TestVectors/Cmaf/protected_1080p_h264_cbcs/manifest.mpd"),
            "Widevine");

        var livre = registry.Observe(
            new Uri("https://media.axprod.net/TestVectors/Cmaf/clear_1080p/video.mp4"),
            "video/mp4", RequestContext.Default, contentLength: 100_000_000);

        Assert.NotNull(livre);
        Assert.False(livre!.IsProtected);
    }

    [Fact]
    public void Clear_EsqueceOsPacotesProtegidos()
    {
        // Navegar para outra pagina zera tudo; um prefixo marcado que sobrevivesse esconderia
        // midia livre de outro site que por acaso usasse o mesmo caminho.
        var registry = new MediaRegistry();
        var manifesto = new Uri("https://cdn.exemplo.com/pacote/manifest.mpd");

        registry.MarkPackageProtected(manifesto, "Widevine");
        registry.Clear();

        Assert.Null(registry.ProtectionFor(manifesto));
    }
}
