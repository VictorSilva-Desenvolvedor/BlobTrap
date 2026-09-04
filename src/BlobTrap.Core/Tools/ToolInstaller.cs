using System.IO.Compression;
using System.Security.Cryptography;
using BlobTrap.Core.Net;

namespace BlobTrap.Core.Tools;

public sealed record ToolInstallProgress(string Stage, double? Fraction);

/// <summary>
/// Fetches the external binaries into <see cref="ToolLocator.BinDirectory"/> on request.
/// Nothing here runs on its own - the user asks for it, because it downloads from the network.
/// </summary>
public sealed class ToolInstaller
{
    /// <summary>Single-file build, so installing it is just a download.</summary>
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    /// <summary>Checksum manifest published alongside the release, as "&lt;hash&gt;  &lt;file&gt;" lines.</summary>
    private const string YtDlpChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    /// <summary>BtbN publishes a stable "latest" URL, which keeps the installer free of version pinning.</summary>
    private const string FfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    private const string FfmpegChecksumUrl = FfmpegZipUrl + ".sha256";

    private readonly MediaHttpClient _http;

    public ToolInstaller(MediaHttpClient http) => _http = http;

    public async Task InstallYtDlpAsync(IProgress<ToolInstallProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ToolLocator.BinDirectory);

        var target = ToolLocator.ManagedPath(ExternalTool.YtDlp);
        var expected = await TryGetChecksumAsync(new Uri(YtDlpChecksumsUrl), "yt-dlp.exe", cancellationToken).ConfigureAwait(false);

        await DownloadFileAsync(new Uri(YtDlpUrl), target, "yt-dlp", expected, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ToolInstallProgress("yt-dlp instalado.", 1));
    }

    public async Task InstallFfmpegAsync(IProgress<ToolInstallProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ToolLocator.BinDirectory);

        var zipPath = Path.Combine(Path.GetTempPath(), $"blobtrap-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            var expected = await TryGetChecksumAsync(new Uri(FfmpegChecksumUrl), null, cancellationToken).ConfigureAwait(false);

            await DownloadFileAsync(new Uri(FfmpegZipUrl), zipPath, "ffmpeg", expected, progress, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress("Extraindo ffmpeg...", null));
            ExtractBinaries(zipPath, ToolLocator.BinDirectory);

            progress?.Report(new ToolInstallProgress("ffmpeg instalado.", 1));
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    /// <summary>
    /// Reads the published SHA-256 for an artifact. The checksum comes from the same origin as
    /// the download, so this catches truncation and corruption rather than a compromised
    /// release - which is why a missing checksum warns instead of aborting.
    /// </summary>
    private async Task<string?> TryGetChecksumAsync(Uri url, string? fileName, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _http.GetStringAsync(url, RequestContext.Default, cancellationToken).ConfigureAwait(false);
            return ParseChecksum(text, fileName);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return null; }
    }

    internal static string? ParseChecksum(string text, string? fileName)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var hash = parts[0].Trim();
            if (hash.Length != 64 || !IsHex(hash)) continue;

            // A single-artifact file has no name column; a manifest needs the row that matches.
            if (fileName is null) return hash.ToLowerInvariant();

            if (parts.Length > 1 &&
                parts[^1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }

        return null;
    }

    private static bool IsHex(string value)
    {
        foreach (var ch in value)
            if (!Uri.IsHexDigit(ch)) return false;

        return true;
    }

    private static async Task VerifyChecksumAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"O arquivo baixado nao confere com o checksum publicado (esperado {expected[..12]}..., obtido {actual[..12]}...).");
    }

    /// <summary>Pulls only ffmpeg/ffprobe out of the archive, ignoring its docs and presets.</summary>
    private static void ExtractBinaries(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var wanted = new[] { "ffmpeg.exe", "ffprobe.exe" };
        var extracted = 0;

        foreach (var entry in archive.Entries)
        {
            // Using only the leaf name keeps a crafted archive from escaping the destination.
            var name = Path.GetFileName(entry.FullName);
            if (name.Length == 0) continue;
            if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
            extracted++;
        }

        if (extracted == 0)
            throw new InvalidOperationException("O pacote do ffmpeg nao continha ffmpeg.exe.");
    }

    private async Task DownloadFileAsync(
        Uri url,
        string targetPath,
        string label,
        string? expectedChecksum,
        IProgress<ToolInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = targetPath + ".download";

        try
        {
            using var response = await _http.OpenAsync(url, RequestContext.Default, null, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            long received = 0;

            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[1 << 16];
                int read;

                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    received += read;
                    var fraction = total is > 0 ? (double)received / total.Value : (double?)null;
                    progress?.Report(new ToolInstallProgress($"Baixando {label}...", fraction));
                }
            }

            if (expectedChecksum is not null)
            {
                progress?.Report(new ToolInstallProgress($"Verificando {label}...", null));
                await VerifyChecksumAsync(tempPath, expectedChecksum, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                progress?.Report(new ToolInstallProgress($"{label}: checksum indisponível, seguindo sem verificar.", null));
            }

            // One atomic step, so a crash cannot leave the user with no tool at all.
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is not worth failing an otherwise good install.
        }
        catch (UnauthorizedAccessException)
        {
            // Same - the file is in the temp or bin folder and will be overwritten next time.
        }
    }
}
