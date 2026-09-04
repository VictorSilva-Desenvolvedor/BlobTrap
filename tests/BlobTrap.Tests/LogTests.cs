using System.IO;
using BlobTrap.Core.Diagnostics;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O log tem duas obrigações que se contradizem: registrar o suficiente para diagnosticar, e
/// nunca gravar segredo. As duas são testadas aqui — a segunda com a asserção mais forte que
/// existe, lendo o arquivo do disco e procurando o segredo dentro dele.
/// </summary>
public class LogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "blobtrap-log-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _previous;
    private readonly LogLevel _previousLevel;

    public LogTests()
    {
        _previous = Log.Directory;
        _previousLevel = Log.MinimumLevel;
        Log.Directory = _directory;
    }

    public void Dispose()
    {
        Log.Directory = _previous!;
        Log.MinimumLevel = _previousLevel;

        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Info_EscreveNoArquivoDoDia()
    {
        Log.Info("teste", "primeira linha");

        var content = File.ReadAllText(Log.CurrentFile);

        Assert.Contains("primeira linha", content);
        Assert.Contains("INFO", content);
        Assert.Contains("teste", content);
    }

    /// <summary>
    /// A garantia que justifica o log existir de forma segura: o arquivo é o que o usuário
    /// anexa num relato de bug, e o BlobTrap manipula o Cookie de sessão dele.
    /// </summary>
    [Fact]
    public void OQueVaiParaODisco_JaEstaRedigido()
    {
        Log.Error("download", "falhou em https://cdn.exemplo.com/s.ts?token=SEGREDO_ABC "
                            + "(Cookie: sess=SEGREDO_XYZ)");

        var content = File.ReadAllText(Log.CurrentFile);

        Assert.DoesNotContain("SEGREDO_ABC", content);
        Assert.DoesNotContain("SEGREDO_XYZ", content);

        // E ainda serve para diagnosticar: o host continua la.
        Assert.Contains("cdn.exemplo.com", content);
    }

    [Fact]
    public void MensagemDeExcecao_TambemPassaPelaRedacao()
    {
        var error = new InvalidOperationException("token=SEGREDO_NA_EXCECAO em https://cdn.x/y?sig=SEGREDO_SIG");

        Log.Error("download", "falhou", error);

        var content = File.ReadAllText(Log.CurrentFile);

        Assert.DoesNotContain("SEGREDO_SIG", content);
        Assert.Contains("InvalidOperationException", content);
    }

    [Fact]
    public void Debug_FicaDeForaPorPadrao()
    {
        // Debug registra cada segmento: num filme sao dezenas de milhares de linhas.
        Log.Debug("teste", "ruido");

        Assert.False(File.Exists(Log.CurrentFile));
    }

    [Fact]
    public void Debug_ApareceQuandoONivelBaixa()
    {
        Log.MinimumLevel = LogLevel.Debug;
        Log.Debug("teste", "detalhe");

        Assert.Contains("detalhe", File.ReadAllText(Log.CurrentFile));
    }

    [Fact]
    public void VariasLinhas_TodasSobrevivem()
    {
        for (var i = 0; i < 50; i++) Log.Info("teste", $"linha {i}");

        var lines = File.ReadAllLines(Log.CurrentFile);

        Assert.Equal(50, lines.Length);
        Assert.Contains("linha 0", lines[0]);
        Assert.Contains("linha 49", lines[49]);
    }

    [Fact]
    public void TrimOldFiles_ApagaOVencidoEPreservaORecente()
    {
        Directory.CreateDirectory(_directory);

        var old = Path.Combine(_directory, "blobtrap-2020-01-01.log");
        var recent = Path.Combine(_directory, "blobtrap-2026-09-04.log");

        File.WriteAllText(old, "antigo");
        File.WriteAllText(recent, "recente");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-Log.RetentionDays - 1));

        Log.TrimOldFiles(DateTimeOffset.UtcNow);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void TrimOldFiles_NaoMexeEmArquivoQueNaoEhLog()
    {
        Directory.CreateDirectory(_directory);

        var other = Path.Combine(_directory, "anotacoes.txt");
        File.WriteAllText(other, "importante");
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddYears(-2));

        Log.TrimOldFiles(DateTimeOffset.UtcNow);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void TrimOldFiles_EmPastaInexistente_NaoEhErro()
    {
        Log.Directory = Path.Combine(_directory, "nao-existe");

        Log.TrimOldFiles(DateTimeOffset.UtcNow);
    }
}
