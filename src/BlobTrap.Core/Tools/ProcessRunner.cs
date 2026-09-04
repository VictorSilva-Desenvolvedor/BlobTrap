using System.Diagnostics;
using System.Text;

namespace BlobTrap.Core.Tools;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs a console tool, streaming its output back line by line and killing it on cancel.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // GetDirectoryName returns "" (not null) for a bare file name, and an empty
            // WorkingDirectory means "inherit ours" - which is the behaviour we want there.
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? string.Empty,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onStandardOutput?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // ffmpeg writes its whole log to stderr, so keep only a tail for diagnostics.
            if (stderr.Length < 32_000) stderr.AppendLine(e.Data);
            onStandardError?.Invoke(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill; nothing left to stop.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The OS refused the kill (already terminating). The caller is unwinding anyway.
        }
    }
}
