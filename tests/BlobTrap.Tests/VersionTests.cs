using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Tools;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Versão é a primeira pergunta de qualquer relato de bug, e era a única que o usuário não
/// conseguia responder: não aparecia em lugar nenhum da interface, e o
/// <c>YtDlpRunner.GetVersionAsync</c> existia sem nunca ser chamado.
/// </summary>
public class VersionTests
{
    [Fact]
    public void AppVersion_NuncaVemVaziaNemNula()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
    }

    [Fact]
    public void AppVersion_NaoCarregaOHashDoCommit()
    {
        // InformationalVersion pode vir como "1.0.0+abc1234". O sufixo nao diz nada ao
        // usuario e so ocupa espaco na linha do rodape.
        Assert.DoesNotContain('+', AppVersion.Current);
    }

    [Fact]
    public void AppVersion_EhEstavelEntreChamadas()
    {
        Assert.Equal(AppVersion.Current, AppVersion.Current);
    }

    [Theory]
    // Formato dos builds do BtbN, que e' de onde o ToolInstaller baixa.
    [InlineData(
        "ffmpeg version n7.1-latest-win64-gpl Copyright (c) 2000-2024 the FFmpeg developers",
        "n7.1-latest-win64-gpl")]
    // Formato de uma build de distribuicao.
    [InlineData(
        "ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers",
        "6.1.1-3ubuntu5")]
    // Build git, o formato que aparece em quem compila na mao.
    [InlineData(
        "ffmpeg version N-113402-g1b8c1c1d5f Copyright (c) 2000-2024",
        "N-113402-g1b8c1c1d5f")]
    public void ParseVersion_PegaSoOTokenDaVersao(string output, string expected)
    {
        Assert.Equal(expected, FfmpegRunner.ParseVersion(output));
    }

    [Fact]
    public void ParseVersion_UsaSoAPrimeiraLinha()
    {
        // O "-version" despeja dezenas de linhas de configuracao depois do cabecalho.
        var output = "ffmpeg version n7.1 Copyright (c) 2000-2024\n"
                   + "built with gcc 14.2.0\n"
                   + "configuration: --enable-gpl --enable-libx264\n";

        Assert.Equal("n7.1", FfmpegRunner.ParseVersion(output));
    }

    [Fact]
    public void ParseVersion_SemAPalavraVersion_DevolveOCabecalhoTruncado()
    {
        // Um binario que nao segue o formato ainda diz algo util; o corte evita que uma linha
        // enorme empurre o caminho do arquivo para fora do dialogo.
        var result = FfmpegRunner.ParseVersion(new string('x', 200));

        Assert.NotNull(result);
        Assert.Equal(60, result!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   \n  \n")]
    public void ParseVersion_SaidaVazia_DevolveNulo(string output)
    {
        Assert.Null(FfmpegRunner.ParseVersion(output));
    }
}
