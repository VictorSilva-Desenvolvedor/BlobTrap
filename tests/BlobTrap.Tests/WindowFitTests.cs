using BlobTrap.App.Theming;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A janela principal pede 1420x860; num notebook de 1366x768 (720 úteis) ela nascia com o
/// conteúdo fora da tela. Os números aqui são os dessa máquina.
/// </summary>
public class WindowFitTests
{
    [Fact]
    public void TelaMenorQueAJanela_EncolheComFolga()
    {
        Assert.Equal(1366 - WindowFit.Margin, WindowFit.Fit(1420, 1366));
        Assert.Equal(720 - WindowFit.Margin, WindowFit.Fit(860, 720));
    }

    [Fact]
    public void TelaGrande_MantemOTamanhoPedido()
    {
        Assert.Equal(1420, WindowFit.Fit(1420, 1920));
        Assert.Equal(860, WindowFit.Fit(860, 1040));
    }

    [Fact]
    public void NuncaFicaAbaixoDoMinimoDaJanela()
    {
        Assert.Equal(600, WindowFit.Fit(860, 500, minimum: 600));
    }
}
