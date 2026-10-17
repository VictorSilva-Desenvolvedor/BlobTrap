using System.Windows;

namespace BlobTrap.App.Theming;

/// <summary>
/// Impede que uma janela nasça maior que a área útil da tela.
///
/// Os tamanhos no XAML foram escolhidos num monitor grande: a janela principal pede 1420x860
/// e a de ferramentas 740 de altura. Num notebook de 1366x768 (720 de área útil, descontada a
/// barra de tarefas) isso empurra o conteúdo para fora da tela, com a barra de status e o
/// botão de fechar inalcançáveis.
/// </summary>
public static class WindowFit
{
    /// <summary>Folga deixada em cada eixo para as bordas do sistema.</summary>
    public const double Margin = 16;

    /// <summary>
    /// O menor entre o tamanho desejado e o disponível menos a folga. Nunca fica abaixo do
    /// mínimo da janela: abaixo dele o layout quebra, e a tela pequena demais para o mínimo
    /// é um problema que reduzir mais não resolve.
    /// </summary>
    public static double Fit(double desired, double available, double minimum = 0) =>
        Math.Max(minimum, Math.Min(desired, available - Margin));

    /// <summary>Aplica <see cref="Fit"/> à largura e à altura, contra a área útil da tela principal.</summary>
    public static void ToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;

        window.Width = Fit(window.Width, area.Width, window.MinWidth);
        window.Height = Fit(window.Height, area.Height, window.MinHeight);
    }
}
