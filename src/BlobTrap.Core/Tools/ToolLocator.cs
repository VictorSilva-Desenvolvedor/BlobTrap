namespace BlobTrap.Core.Tools;

public enum ExternalTool
{
    Ffmpeg,
    Ffprobe,
    YtDlp,
}

/// <summary>
/// Finds the external binaries BlobTrap drives. Looks in its own bin folder first so a
/// managed copy always wins over whatever happens to be on PATH.
/// </summary>
public static class ToolLocator
{
    /// <summary>%LOCALAPPDATA%\BlobTrap - where downloaded tools and settings live.</summary>
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlobTrap");

    public static string BinDirectory { get; } = Path.Combine(AppDataDirectory, "bin");

    public static string FileNameFor(ExternalTool tool) => tool switch
    {
        ExternalTool.Ffmpeg => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg",
        ExternalTool.Ffprobe => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe",
        ExternalTool.YtDlp => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <summary>Full path to the tool, or null when it is not installed anywhere we can see.</summary>
    public static string? Find(ExternalTool tool)
    {
        var fileName = FileNameFor(tool);

        var managed = Path.Combine(BinDirectory, fileName);
        if (File.Exists(managed)) return managed;

        var beside = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(beside)) return beside;

        return FindOnPath(fileName);
    }

    public static bool IsAvailable(ExternalTool tool) => Find(tool) is not null;

    /// <summary>Where <see cref="ToolInstaller"/> will place the tool.</summary>
    public static string ManagedPath(ExternalTool tool) => Path.Combine(BinDirectory, FileNameFor(tool));

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not a reason to stop looking.
            }
        }

        return null;
    }
}
