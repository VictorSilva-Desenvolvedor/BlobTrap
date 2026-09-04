using BlobTrap.Core.Models;
using BlobTrap.Core.Net;
using BlobTrap.Core.Sniffing;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Som de interface nao e midia baixavel. Os bipes da caixa de busca do YouTube sozinhos
/// faziam a pagina marcar verde num autoteste, escondendo o defeito de verdade que estava
/// atras deles.
/// </summary>
public class RuidoDeInterfaceTests
{
// ===== 2. Bipes da interface =====

    [Theory]
    [InlineData("https://www.youtube.com/s/search/audio/no_input.mp3")]
    [InlineData("https://www.youtube.com/s/search/audio/success.mp3")]
    [InlineData("https://www.youtube.com/s/search/audio/failure.mp3")]
    public void SonsDaCaixaDeBusca_NaoEntramNaLista(string url)
    {
        var registry = new MediaRegistry();

        var candidato = registry.Observe(
            new Uri(url), "audio/mpeg", RequestContext.Default, contentLength: 6_800);

        Assert.Null(candidato);
    }

    [Fact]
    public void AudioPequenoDeConteudo_ContinuaAparecendo()
    {
        // O corte para os bipes e' por caminho, e nao por tamanho: audio curto de verdade -
        // recado de voz, sample, trecho de podcast - tem que continuar na lista.
        var registry = new MediaRegistry();

        var candidato = registry.Observe(
            new Uri("https://cdn.exemplo.com/recados/2026-09-04.mp3"),
            "audio/mpeg", RequestContext.Default, contentLength: 6_800);

        Assert.NotNull(candidato);
        Assert.Equal(MediaKind.ProgressiveAudio, candidato!.Kind);
    }
}
