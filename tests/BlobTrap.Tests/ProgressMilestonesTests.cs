using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Models;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O progresso chega dezenas de vezes por segundo; o log só pode receber uma linha por marco.
/// </summary>
public class ProgressMilestonesTests
{
    [Fact]
    public void PrimeiroRelatorio_AnunciaAEtapa()
    {
        var marcos = new ProgressMilestones();

        var linha = marcos.Next(Segmentos("Baixando video", 0, 100));

        Assert.Equal("etapa: Baixando video", linha);
    }

    [Fact]
    public void DentroDaMesmaDezena_NaoRepete()
    {
        var marcos = new ProgressMilestones();
        marcos.Next(Segmentos("Baixando video", 0, 100));

        Assert.Null(marcos.Next(Segmentos("Baixando video", 3, 100)));
        Assert.Null(marcos.Next(Segmentos("Baixando video", 9, 100)));
    }

    [Fact]
    public void CruzarADezena_GravaUmaLinhaComAPorcentagem()
    {
        var marcos = new ProgressMilestones();
        marcos.Next(Segmentos("Baixando video", 0, 100));

        var linha = marcos.Next(Segmentos("Baixando video", 40, 100));

        Assert.NotNull(linha);
        Assert.Contains("Baixando video 40%", linha);
        Assert.Contains("40/100 segmentos", linha);
    }

    [Fact]
    public void MudarDeEtapa_AnunciaDeNovo_EReiniciaAContagem()
    {
        var marcos = new ProgressMilestones();
        marcos.Next(Segmentos("Baixando video", 0, 100));
        marcos.Next(Segmentos("Baixando video", 100, 100));

        Assert.Equal("etapa: Baixando audio", marcos.Next(Segmentos("Baixando audio", 0, 50)));
        Assert.Contains("20%", marcos.Next(Segmentos("Baixando audio", 10, 50)));
    }

    [Fact]
    public void SemTotalConhecido_SoAnunciaAEtapa()
    {
        var marcos = new ProgressMilestones();
        marcos.Next(new DownloadProgress { Stage = "Baixando", BytesReceived = 10 });

        Assert.Null(marcos.Next(new DownloadProgress { Stage = "Baixando", BytesReceived = 99_000_000 }));
    }

    private static DownloadProgress Segmentos(string etapa, int feitos, int total) => new()
    {
        Stage = etapa,
        SegmentsDone = feitos,
        SegmentsTotal = total,
    };
}
