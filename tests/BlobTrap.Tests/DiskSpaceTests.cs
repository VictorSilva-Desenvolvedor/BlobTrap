using System.IO;
using BlobTrap.Core.Download;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// A checagem de espaço tem duas maneiras de errar, e a segunda é a cara: negar um download
/// que caberia deixa o usuário sem o vídeo e sem nada a fazer. Por isso "não sei quanto tem
/// livre" nunca vira "não tem".
/// </summary>
public class DiskSpaceTests
{
    [Fact]
    public void RequiredFor_AplicaAFolgaSobreOTamanhoEstimado()
    {
        var estimate = 4L * 1024 * 1024 * 1024;

        var required = DiskSpace.RequiredFor(estimate);

        // Duas copias vivem no disco ao mesmo tempo: os segmentos concatenados na pasta de
        // trabalho e o arquivo final que o ffmpeg escreve a partir deles.
        Assert.Equal((long)(estimate * DiskSpace.SafetyFactor), required);
        Assert.True(required > estimate * 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1024L)]
    public void RequiredFor_NuncaFicaAbaixoDoPiso(long? estimate)
    {
        Assert.Equal(DiskSpace.MinimumFreeBytes, DiskSpace.RequiredFor(estimate));
    }

    [Fact]
    public void RequiredFor_NaoEstouraComEstimativaAbsurda()
    {
        // Um manifesto malformado pode declarar BANDWIDTH gigante. Multiplicar por 2.2 em long
        // daria a volta e viraria um required negativo - que aprovaria qualquer download.
        var required = DiskSpace.RequiredFor(long.MaxValue);

        Assert.True(required > 0);
        Assert.Equal(long.MaxValue, required);
    }

    [Fact]
    public void AvailableFor_LeOVolumeDeUmCaminhoReal()
    {
        var available = DiskSpace.AvailableFor(Path.GetTempPath());

        Assert.NotNull(available);
        Assert.True(available > 0);
    }

    [Fact]
    public void AvailableFor_DevolveNuloEmCaminhoImpossivel()
    {
        // Nulo significa "nao sei", e EnsureRoomFor trata isso deixando passar.
        Assert.Null(DiskSpace.AvailableFor("\0invalido"));
    }

    [Fact]
    public void EnsureRoomFor_DeixaPassarQuandoCabe()
    {
        var path = Path.Combine(Path.GetTempPath(), "blobtrap-cabe.mp4");

        DiskSpace.EnsureRoomFor(path, estimatedBytes: 1024);
    }

    [Fact]
    public void EnsureRoomFor_EstouraQuandoNaoCabe()
    {
        var path = Path.Combine(Path.GetTempPath(), "blobtrap-nao-cabe.mp4");
        var available = DiskSpace.AvailableFor(path)!.Value;

        var ex = Assert.Throws<InsufficientDiskSpaceException>(
            () => DiskSpace.EnsureRoomFor(path, available * 4));

        Assert.True(ex.RequiredBytes > ex.AvailableBytes);

        // A mensagem tem que dizer quanto falta. "Falhou" sozinho nao deixa o usuario agir.
        Assert.Contains("Espaço insuficiente", ex.Message);
    }

    [Fact]
    public void EnsureRoomFor_NaoBloqueiaQuandoOEspacoNaoPodeSerLido()
    {
        // Volume de rede, unidade removida, permissao negada. Um palpite errado que bloqueia
        // um download possivel e' pior do que a falha que esta checagem evita.
        DiskSpace.EnsureRoomFor("\0invalido", long.MaxValue / 4);
    }
}
