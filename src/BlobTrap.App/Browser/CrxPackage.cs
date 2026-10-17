using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace BlobTrap.App.Browser;

/// <summary>
/// Unpacks a Chrome Web Store package (.crx) into the folder layout WebView2 loads.
///
/// A .crx is a zip behind a signed header: "Cr24", a version, then the header lengths. Only the
/// zip matters here; the signature is Google's, checked by the store, not by WebView2.
/// </summary>
public static class CrxPackage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Byte offset where the zip starts, after the CRX2 or CRX3 header.</summary>
    public static int ZipOffset(ReadOnlySpan<byte> crx)
    {
        if (crx.Length < 12 || !crx[..4].SequenceEqual("Cr24"u8))
            throw new InvalidDataException("Não é um pacote .crx (cabeçalho Cr24 ausente).");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(crx[4..]);
        long offset = version switch
        {
            3 => 12L + BinaryPrimitives.ReadUInt32LittleEndian(crx[8..]),
            2 when crx.Length >= 16 => 16L + BinaryPrimitives.ReadUInt32LittleEndian(crx[8..])
                                          + BinaryPrimitives.ReadUInt32LittleEndian(crx[12..]),
            _ => throw new InvalidDataException($"Versão de .crx não suportada: {version}."),
        };

        if (offset > crx.Length - 4 || !crx[(int)offset..((int)offset + 2)].SequenceEqual("PK"u8))
            throw new InvalidDataException("Pacote .crx truncado: o zip não começa onde o cabeçalho indica.");

        return (int)offset;
    }

    /// <summary>The store's own update endpoint, the same one Chrome uses to fetch an install.</summary>
    public static Uri StoreDownloadUri(string extensionId) => new(
        "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=140.0" +
        $"&acceptformat=crx2,crx3&x=id%3D{extensionId}%26uc");

    /// <summary>
    /// Downloads and unpacks into <paramref name="target"/>, replacing what is there. Extraction
    /// goes to a sibling folder first, so a failed download never leaves a half-written extension.
    /// </summary>
    public static async Task InstallFromStoreAsync(string extensionId, string target, CancellationToken cancellation)
    {
        var crx = await Http.GetByteArrayAsync(StoreDownloadUri(extensionId), cancellation);
        var offset = ZipOffset(crx);

        var staging = target + ".novo";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        using (var zip = new ZipArchive(new MemoryStream(crx, offset, crx.Length - offset), ZipArchiveMode.Read))
        {
            zip.ExtractToDirectory(staging);
        }

        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(staging, target);
    }
}
