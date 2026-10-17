using System.Buffers.Binary;
using System.IO;
using BlobTrap.App.Browser;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O .crx da Chrome Web Store é um zip atrás de um cabeçalho assinado. Errar o deslocamento
/// extrai lixo — ou, pior, falha só no meio, com metade da extensão em disco.
/// </summary>
public class CrxPackageTests
{
    private static byte[] Crx(uint version, params uint[] lengths)
    {
        var header = new byte[8 + 4 * lengths.Length];
        "Cr24"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), version);
        for (var i = 0; i < lengths.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8 + 4 * i), lengths[i]);

        var body = new byte[lengths.Sum(l => (long)l)];
        return [.. header, .. body, .. "PK\u0003\u0004"u8];
    }

    [Fact]
    public void Crx3_PulaOCabecalhoProtobuf()
    {
        Assert.Equal(12 + 1308, CrxPackage.ZipOffset(Crx(3, 1308)));
    }

    [Fact]
    public void Crx2_PulaChavePublicaEAssinatura()
    {
        Assert.Equal(16 + 294 + 256, CrxPackage.ZipOffset(Crx(2, 294, 256)));
    }

    [Fact]
    public void SemMagicCr24_Recusa()
    {
        Assert.Throws<InvalidDataException>(() => CrxPackage.ZipOffset("PK\u0003\u0004 um zip comum"u8));
    }

    [Fact]
    public void VersaoDesconhecida_Recusa()
    {
        Assert.Throws<InvalidDataException>(() => CrxPackage.ZipOffset(Crx(4, 10)));
    }

    [Fact]
    public void CabecalhoApontandoParaForaDoArquivo_Recusa()
    {
        // Download cortado: o cabeçalho promete 1308 bytes que não chegaram.
        var truncated = Crx(3, 1308)[..200];
        Assert.Throws<InvalidDataException>(() => CrxPackage.ZipOffset(truncated));
    }

    [Fact]
    public void UrlDaLoja_LevaOIdDaExtensao()
    {
        Assert.Contains("id%3Dlmjnegcaeklhafolokijcfjliaokphfk%26uc",
            CrxPackage.StoreDownloadUri(VideoDownloadHelper.Id).AbsoluteUri);
    }
}
