using System.Security.Cryptography;
using BlobTrap.Core.Download;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// AES-128 is where a subtle mistake produces a file that downloads "successfully" and plays
/// as noise, so the round trip is worth pinning down.
/// </summary>
public class SegmentDecryptionTests
{
    private static readonly byte[] Key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Iv = Convert.FromHexString("0f0e0d0c0b0a09080706050403020100");

    private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }

    [Fact]
    public void Decrypt_RecoversTheOriginalSegment()
    {
        var plain = new byte[5000];
        Random.Shared.NextBytes(plain);

        var decrypted = SegmentDownloader.Decrypt(Encrypt(plain, Key, Iv), Key, Iv);

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void Decrypt_HandlesAnExactBlockMultiple()
    {
        var plain = new byte[16 * 40];
        Random.Shared.NextBytes(plain);

        var decrypted = SegmentDownloader.Decrypt(Encrypt(plain, Key, Iv), Key, Iv);

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void Decrypt_WithTheWrongIvDoesNotMatchThePlaintext()
    {
        var plain = new byte[64];
        Random.Shared.NextBytes(plain);

        var wrongIv = new byte[16];
        var decrypted = SegmentDownloader.Decrypt(Encrypt(plain, Key, Iv), Key, wrongIv);

        // CBC only corrupts the first block, so the tail still matches - the head must not.
        Assert.NotEqual(plain.Take(16), decrypted.Take(16));
    }

    [Fact]
    public void Decrypt_RejectsAKeyOfTheWrongLength()
    {
        var data = Encrypt(new byte[32], Key, Iv);

        Assert.Throws<CryptographicException>(() => SegmentDownloader.Decrypt(data, new byte[8], Iv));
    }

    [Fact]
    public void Decrypt_PadsAShortIvInsteadOfThrowing()
    {
        var plain = new byte[32];
        Random.Shared.NextBytes(plain);

        var padded = new byte[16];
        var encrypted = Encrypt(plain, Key, padded);

        var decrypted = SegmentDownloader.Decrypt(encrypted, Key, new byte[4]);

        Assert.Equal(plain, decrypted);
    }
}
