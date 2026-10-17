using System.Globalization;
using BlobTrap.Core.Models;

namespace BlobTrap.Core.Diagnostics;

/// <summary>
/// Decide quando um relatório de progresso merece uma linha no log: ao mudar de etapa
/// (vídeo, áudio, finalização) e a cada 10% dentro dela.
///
/// Existe porque o progresso chega dezenas de vezes por segundo. Registrar todos afogaria o
/// log; registrar nenhum deixava quem acompanha o download sem saber em que faixa ele está.
/// Seguro para chamadas concorrentes: os segmentos reportam de várias threads.
/// </summary>
public sealed class ProgressMilestones
{
    private readonly object _sync = new();
    private string? _stage;
    private int _lastDecile = -1;

    /// <returns>A linha a gravar, ou null quando este relatório não cruzou nenhum marco.</returns>
    public string? Next(DownloadProgress progress)
    {
        var stage = progress.Stage ?? string.Empty;
        var decile = progress.Fraction is { } fraction ? (int)(fraction * 10) : -1;

        lock (_sync)
        {
            if (!string.Equals(stage, _stage, StringComparison.Ordinal))
            {
                _stage = stage;
                _lastDecile = decile;
                return $"etapa: {(stage.Length == 0 ? "?" : stage)}";
            }

            if (decile <= _lastDecile) return null;
            _lastDecile = decile;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{stage} {decile * 10}% - {progress.ProgressLabel}, {progress.SpeedLabel}");
    }
}
