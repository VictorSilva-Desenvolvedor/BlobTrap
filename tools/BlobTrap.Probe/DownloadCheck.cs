using System.Globalization;
using System.Text;
using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Models;

namespace BlobTrap.Probe;

public enum DownloadOutcome
{
    /// <summary>O arquivo final saiu inteiro.</summary>
    Baixou,

    /// <summary>
    /// Bytes chegando quando o orçamento acabou. Conta como sucesso do que se mede aqui: um
    /// 403 de CDN aparece no primeiro request, não no minuto dois.
    /// </summary>
    Baixando,

    /// <summary>Tentou e falhou — a mensagem é o que o usuário veria.</summary>
    Falhou,

    /// <summary>Nada baixável foi detectado, então não há download a medir.</summary>
    SemCandidato,
}

/// <summary>Uma tentativa de baixar um candidato com o executor do app.</summary>
public sealed record DownloadAttempt(
    MediaKind Kind,
    Uri Url,
    string? ResolvedBy,
    string? Quality,
    DownloadOutcome Outcome,
    long Bytes,
    TimeSpan Elapsed,
    string? Error);

/// <summary>O que o download de um alvo deu: a primeira tentativa que funcionou, ou todas as que falharam.</summary>
public sealed record DownloadResult(DetectionTarget Target, IReadOnlyList<DownloadAttempt> Attempts)
{
    public DownloadOutcome Outcome =>
        Attempts.Count == 0 ? DownloadOutcome.SemCandidato
        : Attempts.FirstOrDefault(a => a.Outcome is DownloadOutcome.Baixou or DownloadOutcome.Baixando)?.Outcome
          ?? DownloadOutcome.Falhou;
}

public static class DownloadCheck
{
    public static string Render(IReadOnlyList<DownloadResult> results, string appVersion, DateTimeOffset when)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();

        var ok = results.Count(r => r.Outcome is DownloadOutcome.Baixou or DownloadOutcome.Baixando);

        text.AppendLine(culture, $"# Autoteste de download — BlobTrap {appVersion}");
        text.AppendLine();
        text.AppendLine(culture, $"{when:yyyy-MM-dd HH:mm} · {ok}/{results.Count} baixaram");
        text.AppendLine();
        text.AppendLine("| Alvo | Resultado | Via | Bytes | Erro |");
        text.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var result in results)
        {
            var best = result.Attempts.FirstOrDefault(a => a.Outcome != DownloadOutcome.Falhou)
                       ?? result.Attempts.FirstOrDefault();

            text.AppendLine(culture,
                $"| {result.Target.Name} | {result.Outcome} | {best?.ResolvedBy ?? "-"} | " +
                $"{(best is null ? "-" : FormatBytes(best.Bytes))} | {Cell(best?.Error)} |");
        }

        foreach (var result in results.Where(r => r.Attempts.Count > 0))
        {
            text.AppendLine();
            text.AppendLine(culture, $"## {result.Target.Name}");
            text.AppendLine();
            text.AppendLine(culture, $"- Página: `{result.Target.Url}`");

            foreach (var a in result.Attempts)
            {
                text.AppendLine(culture,
                    $"- {a.Outcome} · {a.Kind} via {a.ResolvedBy ?? "-"} · {a.Quality ?? "-"} · " +
                    $"{FormatBytes(a.Bytes)} em {a.Elapsed.TotalSeconds:0}s · `{Redaction.ScrubUrl(a.Url)}`");
                if (a.Error is not null) text.AppendLine(culture, $"  - erro: {Cell(a.Error)}");
            }
        }

        return text.ToString();
    }

    private static string Cell(string? value) =>
        value is null ? "-" : Redaction.Scrub(value).ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 20
            ? (bytes / (double)(1 << 20)).ToString("0.0 'MB'", CultureInfo.InvariantCulture)
            : (bytes / 1024.0).ToString("0.0 'KB'", CultureInfo.InvariantCulture);
}
