using System.Globalization;
using System.Text;
using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Models;

namespace BlobTrap.Probe;

/// <summary>O que se espera que o BlobTrap conclua sobre uma página.</summary>
public enum DetectionExpectation
{
    /// <summary>Basta que algo baixável apareça na lista.</summary>
    QualquerMidia,

    /// <summary>Precisa aparecer um manifesto — HLS, DASH ou Smooth.</summary>
    Stream,

    /// <summary>Precisa aparecer um arquivo progressivo (mp4, webm, mp3…).</summary>
    Arquivo,

    /// <summary>
    /// Precisa aparecer algo que renda vídeo — arquivo, manifesto, ou a página entregue ao
    /// extrator.
    ///
    /// Existe porque <see cref="QualquerMidia"/> é frouxo demais para uma página de vídeo: no
    /// primeiro relatório desta ferramenta o alvo do YouTube "passou" tendo detectado apenas
    /// os três bipes da caixa de busca. Áudio solto é mídia, mas não é o que se foi buscar
    /// numa página de vídeo, e o verde escondia a falha exata que a ferramenta veio caçar.
    /// </summary>
    Video,

    /// <summary>Precisa ser reconhecida como protegida. Baixá-la é que seria a falha.</summary>
    RecusaPorDrm,
}

/// <summary>Uma página real usada como alvo do autoteste.</summary>
/// <param name="Name">Rótulo curto, o que aparece na saída.</param>
/// <param name="Url">Página a navegar. É a página do player, não a URL da mídia.</param>
/// <param name="Expectation">O que essa página tem que provar.</param>
public sealed record DetectionTarget(string Name, string Url, DetectionExpectation Expectation)
{
    /// <summary>
    /// Alvos padrão, um por caminho que o motor sabe percorrer.
    ///
    /// São páginas públicas de propósito: a ferramenta mede o BlobTrap, e um alvo atrás de
    /// login mediria a sessão de quem rodou. Link público sai do ar, e aí a falha vem como
    /// "nada detectado" — indistinguível de uma regressão de verdade. Por isso a lista é
    /// substituível por arquivo (<c>--targets</c>) e a saída sempre repete a URL usada.
    /// </summary>
    public static IReadOnlyList<DetectionTarget> Defaults() => new[]
    {
        new DetectionTarget(
            "YouTube (MSE / blob:)",
            "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
            DetectionExpectation.Video),

        new DetectionTarget(
            "HLS público (Apple)",
            "https://developer.apple.com/streaming/examples/basic-stream-osx-ios5.html",
            DetectionExpectation.Stream),

new DetectionTarget(
            "DASH público (dash.js)",
            "https://reference.dashif.org/dash.js/latest/samples/advanced/monitoring.html",
            DetectionExpectation.Stream),

        new DetectionTarget(
            "mp4 direto (archive.org)",
            "https://archive.org/details/BigBuckBunny_124",
            DetectionExpectation.Arquivo),

        new DetectionTarget(
            "DRM Widevine (tem que recusar)",
            "https://reference.dashif.org/dash.js/latest/samples/drm/widevine.html",
            DetectionExpectation.RecusaPorDrm),
    };
}

/// <summary>Uma mídia que o sniffer registrou durante a visita a um alvo.</summary>
public sealed record DetectionObservation(
    MediaKind Kind,
    Uri Url,
    long? ContentLength,
    bool IsProtected = false,
    string? ProtectionSystem = null,
    int VariantCount = 0,
    string? ResolveError = null);

public enum DetectionOutcome
{
    /// <summary>O alvo provou o que tinha que provar.</summary>
    Passou,

    /// <summary>Nada quebrou, mas o resultado não é o esperado — detecção de menos, ou DRM não reconhecido.</summary>
    Falhou,

    /// <summary>Não deu para concluir: navegação falhou, tempo esgotado, rede fora.</summary>
    Erro,
}

/// <summary>O que uma visita a um alvo produziu.</summary>
public sealed class DetectionResult
{
    public required DetectionTarget Target { get; init; }

    public required DetectionOutcome Outcome { get; init; }

    /// <summary>Uma frase dizendo por que deu esse veredito.</summary>
    public required string Summary { get; init; }

    public TimeSpan Elapsed { get; init; }

    public IReadOnlyList<DetectionObservation> Observations { get; init; } = Array.Empty<DetectionObservation>();

    /// <summary>Preenchido quando o veredito é <see cref="DetectionOutcome.Erro"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// O julgamento e o relatório do autoteste de detecção.
///
/// A regra de "o que conta como passar" mora aqui, separada de quem dirige o navegador, por
/// dois motivos: é a parte que precisa de teste, e ela não depende de WebView2 nenhum. O
/// runner só entrega a lista do que o sniffer viu.
/// </summary>
public static class DetectionCheck
{
    /// <summary>Decide o veredito a partir do que foi observado.</summary>
    public static DetectionResult Judge(
        DetectionTarget target,
        IReadOnlyList<DetectionObservation> observations,
        TimeSpan elapsed,
        string? error = null)
    {
        if (error is not null)
            return new DetectionResult
            {
                Target = target,
                Outcome = DetectionOutcome.Erro,
                Summary = error,
                Error = error,
                Elapsed = elapsed,
                Observations = observations,
            };

        var (ok, summary) = target.Expectation switch
        {
            DetectionExpectation.Stream => Require(
                observations.Any(o => o.Kind.IsStreaming()),
                "manifesto detectado",
                "nenhum manifesto HLS/DASH apareceu"),

            DetectionExpectation.Video => Require(
                observations.Any(RendeVideo),
                "vídeo detectado",
                observations.Count == 0
                    ? "nenhuma mídia detectada"
                    : "só apareceu mídia que não é vídeo (áudio de interface, por exemplo)"),

            DetectionExpectation.Arquivo => Require(
                observations.Any(o => o.Kind is MediaKind.ProgressiveVideo or MediaKind.ProgressiveAudio),
                "arquivo progressivo detectado",
                "nenhum arquivo progressivo apareceu"),

            // Recusar DRM só se prova ao resolver o manifesto: detectar a mídia e não
            // perceber a proteção é o caso ruim, e é diferente de não detectar nada.
            DetectionExpectation.RecusaPorDrm => Require(
                observations.Any(o => o.IsProtected),
                "DRM reconhecido e recusado",
                observations.Count == 0
                    ? "nada detectado — não dá para afirmar que o DRM seria recusado"
                    : "mídia detectada sem reconhecer a proteção"),

            _ => Require(
                observations.Any(o => o.Kind.IsDownloadable()),
                "mídia detectada",
                "nenhuma mídia detectada"),
        };

        return new DetectionResult
        {
            Target = target,
            Outcome = ok ? DetectionOutcome.Passou : DetectionOutcome.Falhou,
            Summary = Describe(summary, observations),
            Elapsed = elapsed,
            Observations = observations,
        };
    }

    /// <summary>
    /// Se esta observação entrega vídeo ao usuário.
    ///
    /// Uma página vale, mas só depois de resolvida. Num site de MSE/SABR — YouTube desde a
    /// migração para UMP — a resposta certa do BlobTrap não é uma URL de mídia: é a página,
    /// que o yt-dlp sabe extrair. Recusar isso reprovaria o comportamento correto.
    ///
    /// O que não vale é a página crua: um <c>PageEmbed</c> sem formato nenhum é uma linha na
    /// lista que falha quando alguém clica — e é assim que aparece quando o yt-dlp não está
    /// instalado. Exigir as qualidades é o que separa "extraiu" de "chutou a página".
    /// </summary>
    private static bool RendeVideo(DetectionObservation observation) =>
        observation.Kind is MediaKind.ProgressiveVideo
        || observation.Kind.IsStreaming()
        || (observation.Kind is MediaKind.PageEmbed && observation.VariantCount > 0);

    private static (bool Ok, string Summary) Require(bool condition, string onPass, string onFail) =>
        (condition, condition ? onPass : onFail);

    private static string Describe(string summary, IReadOnlyList<DetectionObservation> observations)
    {
        if (observations.Count == 0) return summary;

        var kinds = observations
            .GroupBy(o => o.Kind)
            .Select(g => g.Count() == 1 ? g.Key.ToDisplayString() : $"{g.Key.ToDisplayString()} x{g.Count()}");

        return $"{summary} ({string.Join(", ", kinds)})";
    }

    /// <summary>
    /// Monta o relatório em Markdown.
    ///
    /// Toda URL passa por <see cref="Redaction"/>. O relatório é feito para ser colado numa
    /// issue, e as URLs vêm de CDN assinada — <c>hdnts</c> da Akamai, <c>Policy</c> e
    /// <c>Signature</c> do CloudFront, <c>X-Amz-*</c> do S3. Vale aqui a mesma regra do log:
    /// o arquivo que se anexa num relato não carrega a sessão de quem o gerou.
    /// </summary>
    public static string Render(IReadOnlyList<DetectionResult> results, string appVersion, DateTimeOffset when)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();

        var passed = results.Count(r => r.Outcome == DetectionOutcome.Passou);

        text.Append("# Autoteste de detecção\n\n")
            .Append(culture, $"BlobTrap {appVersion} — {when.ToString("yyyy-MM-dd HH:mm:ss zzz", culture)}\n\n")
            .Append(culture, $"**{passed} de {results.Count}** alvos passaram.\n");

        foreach (var result in results)
        {
            text.Append(culture, $"\n## {Marker(result.Outcome)} {result.Target.Name}\n\n")
                .Append(culture, $"- Página: `{Redaction.Scrub(result.Target.Url)}`\n")
                .Append(culture, $"- Esperado: {Expected(result.Target.Expectation)}\n")
                .Append(culture, $"- Resultado: {Redaction.Scrub(result.Summary)}\n")
                .Append(culture, $"- Tempo: {result.Elapsed.TotalSeconds.ToString("0.0", culture)}s\n");

            if (result.Observations.Count == 0) continue;

            text.Append("\n| Tipo | Qualidades | Tamanho | URL |\n| --- | --- | --- | --- |\n");

            foreach (var observation in result.Observations)
            {
                text.Append(culture, $"| {observation.Kind.ToDisplayString()}")
                    .Append(culture, $" | {Qualities(observation)}")
                    .Append(culture, $" | {FormatBytes(observation.ContentLength)}")
                    .Append(culture, $" | `{Redaction.ScrubUrl(observation.Url)}` |\n");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// A coluna que diz o que a resolução concluiu: quantas qualidades, ou por que não houve.
    /// Sem isto, "-" significaria tanto "sem qualidade nenhuma" quanto "DRM", que são
    /// resultados opostos — um é defeito, o outro é o comportamento correto.
    /// </summary>
    private static string Qualities(DetectionObservation observation)
    {
        if (observation.IsProtected) return $"DRM: {observation.ProtectionSystem ?? "desconhecido"}";
        if (observation.VariantCount > 0) return observation.VariantCount.ToString(CultureInfo.InvariantCulture);

        return observation.ResolveError is { } error ? Redaction.Scrub(error) : "-";
    }

    private static string Marker(DetectionOutcome outcome) => outcome switch
    {
        DetectionOutcome.Passou => "[ok]",
        DetectionOutcome.Falhou => "[falhou]",
        _ => "[erro]",
    };

    private static string Expected(DetectionExpectation expectation) => expectation switch
    {
        DetectionExpectation.Stream => "um manifesto HLS ou DASH",
        DetectionExpectation.Arquivo => "um arquivo progressivo",
        DetectionExpectation.Video => "um vídeo — arquivo, manifesto, ou a página já extraída",
        DetectionExpectation.RecusaPorDrm => "reconhecer o DRM e recusar",
        _ => "qualquer mídia baixável",
    };

    private static string FormatBytes(long? bytes)
    {
        if (bytes is not > 0) return "-";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes.Value;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
