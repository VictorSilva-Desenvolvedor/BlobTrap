using System.Windows.Threading;
using BlobTrap.App.ViewModels;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A dica do painel vazio precisa mudar com o que já aconteceu.
///
/// Ela dizia "Abra a página do vídeo e dê play" para quem já tinha dado play e estava com o
/// vídeo rodando na tela — mandava fazer o que já fora feito, enquanto a barra de status já
/// sabia que o player usava `blob:` e não dizia o que fazer com isso.
/// </summary>
public class EstadoVazioTests
{
    [Fact]
    public void SemNada_ADicaPedeOPlay()
    {
        var vm = Modelo();

        Assert.Contains("dê play", vm.EmptyStateHint);
    }

    [Fact]
    public void PlayerBlobDetectado_ADicaPassaAApontarOCaminhoUtil()
    {
        var vm = Modelo();

        vm.NotePlayerUsesBlob();

        // Repetir "dê play" aqui seria mandar fazer o que ja foi feito.
        Assert.DoesNotContain("dê play", vm.EmptyStateHint);
        Assert.Contains("Baixar esta página", vm.EmptyStateHint);
        Assert.Contains("blob:", vm.EmptyStateHint);
    }

    [Fact]
    public void ComMidiaJaDetectada_ADicaNaoMuda()
    {
        // O painel vazio nem aparece nesse caso; mexer na dica so' criaria estado inconsistente
        // para o momento em que a lista for limpa.
        var vm = MainViewModel.CreateDesignSample(Dispatcher.CurrentDispatcher);
        var antes = vm.EmptyStateHint;

        vm.NotePlayerUsesBlob();

        Assert.Equal(antes, vm.EmptyStateHint);
    }

    private static MainViewModel Modelo() => new(Dispatcher.CurrentDispatcher);
}
