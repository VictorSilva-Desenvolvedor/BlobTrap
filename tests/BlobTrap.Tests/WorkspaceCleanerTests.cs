using System.IO;
using BlobTrap.Core.Download;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A varredura apaga pastas com gigabytes dentro. É o tipo de código em que o erro não
/// aparece como falha, e sim como trabalho do usuário que sumiu — então o que ela NÃO deve
/// apagar é testado com mais cuidado do que o que ela apaga.
/// </summary>
public class WorkspaceCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "blobtrap-sweep-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkspaceCleanerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void PastaAntiga_EhApagadaEOEspacoContado()
    {
        var orphan = Work("abandonada", ageHours: 48, bytes: 4096);

        var freed = WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);

        Assert.False(Directory.Exists(orphan));
        Assert.Equal(4096, freed);
    }

    /// <summary>
    /// A garantia que importa: uma segunda instância do BlobTrap abrindo no meio de um
    /// download da primeira não pode levar embora os segmentos que ela está escrevendo.
    /// </summary>
    [Fact]
    public void PastaRecente_EhPreservada()
    {
        var live = Work("em-uso", ageHours: 0, bytes: 1024);

        var freed = WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);

        Assert.True(Directory.Exists(live));
        Assert.Equal(0, freed);
    }

    [Fact]
    public void ANovaIdadeContaDaUltimaEscrita_NaoDaCriacao()
    {
        // Um download longo cria a pasta hoje de manha e ainda escreve nela agora. Se a idade
        // viesse de CreationTime, uma varredura no meio do dia comeria o trabalho em curso.
        var directory = Work("longo", ageHours: 0, bytes: 512);
        Directory.SetCreationTimeUtc(directory, DateTime.UtcNow.AddDays(-3));

        WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);

        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void PastaExatamenteNoLimite_NaoEhApagada()
    {
        var borderline = Work("no-limite", ageHours: 0, bytes: 128);
        Directory.SetLastWriteTimeUtc(borderline, DateTime.UtcNow - WorkspaceCleaner.MinimumAge + TimeSpan.FromMinutes(1));

        WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);

        Assert.True(Directory.Exists(borderline));
    }

    [Fact]
    public void VariasPastas_SoAsOrfasSaem()
    {
        var old1 = Work("velha-1", ageHours: 24, bytes: 100);
        var old2 = Work("velha-2", ageHours: 72, bytes: 200);
        var fresh = Work("nova", ageHours: 1, bytes: 400);

        var freed = WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);

        Assert.False(Directory.Exists(old1));
        Assert.False(Directory.Exists(old2));
        Assert.True(Directory.Exists(fresh));
        Assert.Equal(300, freed);
    }

    [Fact]
    public void RaizInexistente_NaoEhErro()
    {
        var missing = Path.Combine(_root, "nao-existe");

        Assert.Equal(0, WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, missing));
    }

    [Fact]
    public void RodarDuasVezes_DaOMesmoResultado()
    {
        Work("orfa", ageHours: 24, bytes: 2048);
        Work("viva", ageHours: 0, bytes: 64);

        var first = WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);
        var second = WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);

        // Idempotencia (regra 5): a segunda passada nao acha mais nada para apagar.
        Assert.Equal(2048, first);
        Assert.Equal(0, second);
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public void ArquivoSoltoNaRaiz_EhIgnorado()
    {
        // A varredura mexe em subpastas de job. Um arquivo solto na raiz nao e' dela.
        var stray = Path.Combine(_root, "settings-backup.json");
        File.WriteAllText(stray, "{}");
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-30));

        WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow, _root);

        Assert.True(File.Exists(stray));
    }

    private string Work(string name, double ageHours, int bytes)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "video.ts"), new byte[bytes]);

        var when = DateTime.UtcNow.AddHours(-ageHours);
        Directory.SetLastWriteTimeUtc(directory, when);

        return directory;
    }
}
