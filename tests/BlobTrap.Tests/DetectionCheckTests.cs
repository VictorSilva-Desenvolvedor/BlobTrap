using System;
using System.Collections.Generic;
using BlobTrap.Core.Models;
using BlobTrap.Probe;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O veredito do autoteste de páginas reais.
///
/// A ferramenta (<c>tools/BlobTrap.Probe</c>) não é parte do produto, mas o que ela conclui
/// vira decisão sobre o produto: um "passou" errado esconde uma regressão de detecção, e um
/// relatório que vaza token transforma um anexo de issue na sessão de quem o gerou. As duas
/// coisas são regra pura, sem navegador, e é o que se testa aqui.
/// </summary>
public class DetectionCheckTests
{
    private static readonly TimeSpan Instantaneo = TimeSpan.FromSeconds(1);

    private static DetectionTarget Alvo(DetectionExpectation esperado) =>
        new("alvo", "https://exemplo.test/assistir", esperado);

    private static DetectionObservation Midia(MediaKind kind, int variantes = 3) =>
        new(kind, new Uri("https://cdn.exemplo.test/media"), 5_000_000, VariantCount: variantes);

    [Fact]
    public void Stream_passa_com_manifesto()
    {
        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.Stream),
            new[] { Midia(MediaKind.HlsPlaylist) },
            Instantaneo);

        Assert.Equal(DetectionOutcome.Passou, resultado.Outcome);
    }

    /// <summary>
    /// Achar um .mp4 numa página de HLS não é o que o alvo pede. Aceitar isso deixaria passar
    /// a regressão em que o parser de manifesto para de funcionar e sobra só o preview.
    /// </summary>
    [Fact]
    public void Stream_falha_quando_so_apareceu_arquivo()
    {
        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.Stream),
            new[] { Midia(MediaKind.ProgressiveVideo) },
            Instantaneo);

        Assert.Equal(DetectionOutcome.Falhou, resultado.Outcome);
    }

    [Fact]
    public void Qualquer_midia_falha_com_lista_vazia()
    {
        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.QualquerMidia),
            Array.Empty<DetectionObservation>(),
            Instantaneo);

        Assert.Equal(DetectionOutcome.Falhou, resultado.Outcome);
        Assert.Contains("nenhuma mídia detectada", resultado.Summary);
    }

    /// <summary>Fragmento não é baixável sozinho; contá-lo como detecção seria mentir.</summary>
    [Fact]
    public void Qualquer_midia_nao_aceita_fragmento()
    {
        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.QualquerMidia),
            new[] { Midia(MediaKind.MediaSegment) },
            Instantaneo);

        Assert.Equal(DetectionOutcome.Falhou, resultado.Outcome);
    }

    [Fact]
    public void Drm_passa_quando_a_protecao_foi_reconhecida()
    {
        var protegida = Midia(MediaKind.DashManifest, variantes: 0) with
        {
            IsProtected = true,
            ProtectionSystem = "Widevine",
        };

        var resultado = DetectionCheck.Judge(Alvo(DetectionExpectation.RecusaPorDrm), new[] { protegida }, Instantaneo);

        Assert.Equal(DetectionOutcome.Passou, resultado.Outcome);
    }

    /// <summary>
    /// O caso grave: a mídia protegida foi detectada e resolvida como se fosse livre. É
    /// diferente de não detectar nada, e as duas mensagens precisam dizer qual foi.
    /// </summary>
    [Fact]
    public void Drm_falha_quando_a_midia_passou_como_livre()
    {
        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.RecusaPorDrm),
            new[] { Midia(MediaKind.DashManifest) },
            Instantaneo);

        Assert.Equal(DetectionOutcome.Falhou, resultado.Outcome);
        Assert.Contains("sem reconhecer a proteção", resultado.Summary);
    }

    [Fact]
    public void Drm_sem_deteccao_nenhuma_nao_conta_como_recusa()
    {
        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.RecusaPorDrm),
            Array.Empty<DetectionObservation>(),
            Instantaneo);

        Assert.Equal(DetectionOutcome.Falhou, resultado.Outcome);
        Assert.Contains("nada detectado", resultado.Summary);
    }

    /// <summary>Erro de navegação não é "não detectou": a diferença é rede fora vs. regressão.</summary>
    [Fact]
    public void Erro_de_navegacao_nao_vira_falha_de_deteccao()
    {
        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.Stream),
            Array.Empty<DetectionObservation>(),
            Instantaneo,
            error: "navegação falhou (HostNameNotResolved)");

        Assert.Equal(DetectionOutcome.Erro, resultado.Outcome);
        Assert.Equal("navegação falhou (HostNameNotResolved)", resultado.Error);
    }

    /// <summary>
    /// O relatório é feito para ser colado numa issue, e as URLs vêm de CDN assinada. Se um
    /// token sobreviver até aqui, o anexo entrega a sessão de quem rodou a ferramenta.
    /// </summary>
    [Fact]
    public void Relatorio_nao_carrega_token_de_cdn()
    {
        var assinada = new Uri(
            "https://cdn.exemplo.test/v.m3u8?hdnts=exp=1788552631~hmac=SEGREDO&Signature=OUTROSEGREDO&itag=140");

        var resultado = DetectionCheck.Judge(
            Alvo(DetectionExpectation.Stream),
            new[] { new DetectionObservation(MediaKind.HlsPlaylist, assinada, 1024) },
            Instantaneo);

        var relatorio = DetectionCheck.Render(new[] { resultado }, "1.0.0", DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("SEGREDO", relatorio, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTROSEGREDO", relatorio, StringComparison.Ordinal);

        // O que sobra ainda tem que servir para depurar: host, caminho e o que não é segredo.
        Assert.Contains("cdn.exemplo.test/v.m3u8", relatorio, StringComparison.Ordinal);
        Assert.Contains("itag=140", relatorio, StringComparison.Ordinal);
    }

    [Fact]
    public void Relatorio_conta_quantos_alvos_passaram()
    {
        var resultados = new List<DetectionResult>
        {
            DetectionCheck.Judge(Alvo(DetectionExpectation.Stream), new[] { Midia(MediaKind.HlsPlaylist) }, Instantaneo),
            DetectionCheck.Judge(Alvo(DetectionExpectation.Stream), Array.Empty<DetectionObservation>(), Instantaneo),
        };

        var relatorio = DetectionCheck.Render(resultados, "1.0.0", DateTimeOffset.UnixEpoch);

        Assert.Contains("**1 de 2**", relatorio, StringComparison.Ordinal);
        Assert.Contains("[falhou]", relatorio, StringComparison.Ordinal);
    }

    /// <summary>
    /// "-" na coluna de qualidades significaria tanto "sem formato nenhum" quanto "DRM", que
    /// são resultados opostos — um é defeito, o outro é o comportamento correto.
    /// </summary>
    [Fact]
    public void Relatorio_distingue_drm_de_ausencia_de_formatos()
    {
        var protegida = Midia(MediaKind.DashManifest, variantes: 0) with
        {
            IsProtected = true,
            ProtectionSystem = "Widevine",
        };

        var relatorio = DetectionCheck.Render(
            new[] { DetectionCheck.Judge(Alvo(DetectionExpectation.RecusaPorDrm), new[] { protegida }, Instantaneo) },
            "1.0.0",
            DateTimeOffset.UnixEpoch);

        Assert.Contains("DRM: Widevine", relatorio, StringComparison.Ordinal);
    }

    /// <summary>Cada alvo padrão precisa de uma URL absoluta; um typo aqui vira "erro" no relatório.</summary>
    [Fact]
    public void Alvos_padrao_tem_url_valida()
    {
        var alvos = DetectionTarget.Defaults();

        Assert.NotEmpty(alvos);

        foreach (var alvo in alvos)
        {
            Assert.False(string.IsNullOrWhiteSpace(alvo.Name));
            Assert.True(Uri.TryCreate(alvo.Url, UriKind.Absolute, out var url), alvo.Name);
            Assert.Equal(Uri.UriSchemeHttps, url!.Scheme);
        }
    }
}
