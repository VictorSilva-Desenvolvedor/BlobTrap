using System.Text.Json;
using BlobTrap.App.Browser;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O fetch tem que sair do frame do player: é ele que já buscou essas URLs, com a origem que
/// a CDN aceita por CORS. Do frame errado o mesmo pedido volta bloqueado.
/// </summary>
public class WebViewFetcherTests
{
    private static readonly (string Id, string Url)[] Portal =
    {
        ("top", "https://portal.exemplo.com/lessons?id=1"),
        ("chat", "https://widget.chat.com/embed"),
        ("player", "https://player.exemplo.com/embed/aula-1"),
    };

    [Fact]
    public void RefererIgualAoFrame_EscolheOFrameDoPlayer()
    {
        var frame = WebViewFetcher.PickFrame(Portal, "https://player.exemplo.com/embed/aula-1",
            new Uri("https://cdn.exemplo.com/aula-1/480p/video.m3u8"));

        Assert.Equal("player", frame);
    }

    [Fact]
    public void RefererSoComAOrigem_EscolhePelaOrigem()
    {
        // Referrer-Policy "origin" manda só https://player.exemplo.com/ como Referer.
        var frame = WebViewFetcher.PickFrame(Portal, "https://player.exemplo.com/",
            new Uri("https://cdn.exemplo.com/seg1.ts"));

        Assert.Equal("player", frame);
    }

    [Fact]
    public void SemReferer_UsaOFrameDaOrigemDaMidia()
    {
        var frame = WebViewFetcher.PickFrame(Portal, null, new Uri("https://player.exemplo.com/aula-1/playlist.m3u8"));

        Assert.Equal("player", frame);
    }

    [Fact]
    public void NadaCasa_CaiNaPaginaDeTopo()
    {
        var frame = WebViewFetcher.PickFrame(Portal, "https://outro.com/", new Uri("https://cdn.outro.com/v.mp4"));

        Assert.Equal("top", frame);
    }

    [Fact]
    public void Parse_LeStatusRangeECorpo()
    {
        var inner = JsonSerializer.Serialize(new
        {
            status = 206,
            type = "video/mp4",
            range = "bytes 0-2/10",
            body = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        });
        var evaluate = JsonSerializer.Serialize(new { result = new { type = "string", value = inner } });

        var result = WebViewFetcher.Parse(evaluate, new Uri("https://cdn.exemplo.com/v.mp4"));

        Assert.NotNull(result);
        Assert.Equal(206, result!.Status);
        Assert.Equal("bytes 0-2/10", result.ContentRange);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Body);
    }

    [Fact]
    public void Parse_FetchQueFalhou_DevolveNulo()
    {
        // CORS ou rede: o navegador não conseguiu pedir, e o 403 do servidor segue como resposta.
        var inner = JsonSerializer.Serialize(new { error = "TypeError: Failed to fetch" });
        var evaluate = JsonSerializer.Serialize(new { result = new { type = "string", value = inner } });

        Assert.Null(WebViewFetcher.Parse(evaluate, new Uri("https://cdn.exemplo.com/v.mp4")));
    }

    [Fact]
    public void Script_EscapaAUrl()
    {
        // A URL entra no JavaScript; uma aspa nela não pode fechar a string e virar código.
        var script = WebViewFetcher.Script(new Uri("https://cdn.exemplo.com/v.mp4?a='x'"), "bytes=0-9");

        Assert.Contains("\"https://cdn.exemplo.com/v.mp4?a=\\u0027x\\u0027\"", script);
        Assert.Contains("\"bytes=0-9\"", script);
    }
}
