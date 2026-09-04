using BlobTrap.Core.Diagnostics;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Redaction é caminho crítico (regra 5 do CLAUDE.md) e ficou crítico no dia em que o log
/// passou a existir: antes, os segredos que o BlobTrap manipula só passavam por memória.
/// Agora vão para um arquivo que sobrevive ao processo e que é, justamente, o que o usuário
/// anexa num relato de bug.
///
/// Os casos abaixo são de CDNs reais — Akamai, CloudFront, S3 assinado, JW Player — porque
/// testar com <c>?token=abc</c> não prova nada sobre o formato que aparece de verdade.
/// </summary>
public class RedactionTests
{
    [Theory]
    // Akamai: token no parametro hdnts, o formato mais comum em stream ao vivo.
    [InlineData(
        "https://cdn.exemplo.com/hls/master.m3u8?hdnts=exp=1735689600~acl=/*~hmac=9f2c1b",
        "https://cdn.exemplo.com/hls/master.m3u8?hdnts=[redigido]")]
    // CloudFront assinado: tres parametros, todos sensiveis.
    [InlineData(
        "https://d1234.cloudfront.net/video/720p.mp4?Policy=eyJTdGF0ZW1lbnQ&Signature=abc123&Key-Pair-Id=APKAI",
        "https://d1234.cloudfront.net/video/720p.mp4?Policy=[redigido]&Signature=[redigido]&Key-Pair-Id=[redigido]")]
    // S3 v4: o que importa e' que X-Amz-Signature e X-Amz-Credential sumam, e a regiao fique.
    [InlineData(
        "https://bucket.s3.amazonaws.com/seg1.ts?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIA%2F20260101&X-Amz-Signature=deadbeef&X-Amz-Expires=3600",
        "https://bucket.s3.amazonaws.com/seg1.ts?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=[redigido]&X-Amz-Signature=[redigido]&X-Amz-Expires=[redigido]")]
    public void ScrubUrl_TiraOTokenESegurraOResto(string input, string expected)
    {
        Assert.Equal(expected, Redaction.ScrubUrl(new Uri(input)));
    }

    [Fact]
    public void ScrubUrl_MantemHostECaminho()
    {
        // O diagnostico depende disso: sem host e caminho, o log nao diz qual CDN recusou nem
        // qual segmento quebrou, e passa a nao servir para nada.
        var scrubbed = Redaction.ScrubUrl(
            new Uri("https://cdn.exemplo.com/hls/1080p/segment-00412.ts?token=segredo"));

        Assert.Contains("cdn.exemplo.com", scrubbed);
        Assert.Contains("segment-00412.ts", scrubbed);
        Assert.DoesNotContain("segredo", scrubbed);
    }

    [Fact]
    public void ScrubUrl_SemQueryFicaIntacta()
    {
        const string url = "https://cdn.exemplo.com/hls/master.m3u8";

        Assert.Equal(url, Redaction.ScrubUrl(new Uri(url)));
    }

    [Fact]
    public void ScrubUrl_PreservaParametroInofensivo()
    {
        var scrubbed = Redaction.ScrubUrl(new Uri("https://cdn.exemplo.com/v.mp4?quality=1080&lang=pt"));

        Assert.Equal("https://cdn.exemplo.com/v.mp4?quality=1080&lang=pt", scrubbed);
    }

    [Theory]
    [InlineData("Cookie")]
    [InlineData("cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("Authorization")]
    [InlineData("X-Auth-Token")]
    [InlineData("X-Api-Key")]
    public void ScrubHeader_RedigeCredencialInteira(string name)
    {
        Assert.Equal(Redaction.Placeholder, Redaction.ScrubHeader(name, "sessao=abc123; user=fulano"));
    }

    [Theory]
    [InlineData("User-Agent")]
    [InlineData("Referer")]
    [InlineData("Origin")]
    public void ScrubHeader_PreservaOQueNaoEhSegredo(string name)
    {
        Assert.Equal("valor-qualquer", Redaction.ScrubHeader(name, "valor-qualquer"));
    }

    [Fact]
    public void Scrub_RedigeLinhaDeCabecalhoNoMeioDeUmTexto()
    {
        // Formato que ffmpeg e yt-dlp usam quando ecoam os headers recebidos.
        var text = "Enviando:\nUser-Agent: Mozilla/5.0\nCookie: sessao=abc123\nReferer: https://exemplo.com/v";

        var scrubbed = Redaction.Scrub(text);

        Assert.DoesNotContain("abc123", scrubbed);
        Assert.Contains("Cookie: " + Redaction.Placeholder, scrubbed);
        Assert.Contains("Mozilla/5.0", scrubbed);
    }

    [Fact]
    public void Scrub_AchaUrlNoMeioDeMensagemDeErro()
    {
        // Exatamente o formato que MediaHttpClient produz ao esgotar as tentativas.
        var message = "Falha ao buscar https://cdn.exemplo.com/seg.ts?token=segredo123 apos 5 tentativas.";

        var scrubbed = Redaction.Scrub(message);

        Assert.DoesNotContain("segredo123", scrubbed);
        Assert.Contains("cdn.exemplo.com", scrubbed);
        Assert.Contains("apos 5 tentativas", scrubbed);
    }

    [Theory]
    [InlineData(@"C:\Users\fulano\Videos\BlobTrap\filme.mp4", @"%USERPROFILE%\Videos\BlobTrap\filme.mp4")]
    [InlineData(@"D:\Users\Maria Silva\Downloads\v.mp4", @"%USERPROFILE%\Downloads\v.mp4")]
    public void ScrubUserPath_TrocaOPerfilPorVariavel(string input, string expected)
    {
        // O nome de usuario nao ajuda a diagnosticar nada e costuma ser o nome real da pessoa.
        Assert.Equal(expected, Redaction.ScrubUserPath(input));
    }

    [Fact]
    public void ScrubUserPath_NaoMexeEmCaminhoQueNaoEhDePerfil()
    {
        const string path = @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";

        Assert.Equal(path, Redaction.ScrubUserPath(path));
    }

    [Fact]
    public void Scrub_CombinaAsTresRegrasNaMesmaLinha()
    {
        var line = @"[a1b2] falhou ao gravar C:\Users\fulano\Videos\f.mp4 "
                 + "de https://cdn.exemplo.com/s.ts?sig=xyz789 (Cookie: sess=q1w2e3)";

        var scrubbed = Redaction.Scrub(line);

        Assert.DoesNotContain(@"C:\Users\fulano", scrubbed);
        Assert.DoesNotContain("xyz789", scrubbed);
        Assert.DoesNotContain("q1w2e3", scrubbed);
        Assert.Contains("cdn.exemplo.com", scrubbed);
        Assert.Contains("[a1b2] falhou", scrubbed);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("__token__")]
    [InlineData("access_token")]
    [InlineData("Signature")]
    [InlineData("X-Amz-Credential")]
    [InlineData("apikey")]
    [InlineData("sessionId")]
    [InlineData("expires")]
    public void IsSecretParameter_ReconhecePorFragmento(string name)
    {
        // Nao ha nome canonico entre CDNs, entao a comparacao e' por "contem" - errar para o
        // lado de redigir demais custa uma linha menos util; errar para o outro vaza a sessao.
        Assert.True(Redaction.IsSecretParameter(name));
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("lang")]
    [InlineData("start")]
    [InlineData("v")]
    public void IsSecretParameter_NaoRedigeParametroComum(string name)
    {
        Assert.False(Redaction.IsSecretParameter(name));
    }

    [Fact]
    public void Scrub_AceitaNuloEVazio()
    {
        Assert.Equal(string.Empty, Redaction.Scrub(null));
        Assert.Equal(string.Empty, Redaction.Scrub(string.Empty));
        Assert.Equal(string.Empty, Redaction.ScrubUrl(null));
    }

    [Fact]
    public void Scrub_EhIdempotente()
    {
        // Regra 5: rodar duas vezes nao pode dar resultado diferente. Importa porque uma
        // mensagem ja redigida pode ser reembrulhada numa excecao e redigida de novo.
        const string line = "erro em https://cdn.exemplo.com/s.ts?token=abc (Cookie: x=1)";

        var once = Redaction.Scrub(line);

        Assert.Equal(once, Redaction.Scrub(once));
    }
}
