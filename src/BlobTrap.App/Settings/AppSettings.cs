using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Tools;

namespace BlobTrap.App.Settings;

/// <summary>User preferences, persisted as JSON next to the downloaded tools.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(ToolLocator.AppDataDirectory, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DownloadDirectory { get; set; } = DefaultDownloadDirectory();

    public string HomePage { get; set; } = "https://www.google.com";

    public int MaxConcurrentDownloads { get; set; } = 2;

    public int SegmentParallelism { get; set; } = 8;

    public bool HideSmallFiles { get; set; } = true;

    public bool IncludeAudioOnly { get; set; } = true;

    public bool IncludeSubtitles { get; set; } = true;

    /// <summary>Files under this size are usually preview loops or ad bumpers, not content.</summary>
    public long SmallFileThresholdBytes { get; set; } = 512 * 1024;

    public static string DefaultDownloadDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var root = string.IsNullOrWhiteSpace(videos)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : videos;

        return Path.Combine(root, "BlobTrap");
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file must never stop the app from starting.
            Log.Warn("config", "settings.json ilegivel; usando os padroes", ex);
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ToolLocator.AppDataDirectory);

            // Write then swap: a crash mid-save would otherwise leave a truncated file, and
            // Load treats a corrupt file as "no settings" - silently losing every preference.
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing preferences is preferable to crashing on shutdown.
            Log.Warn("config", "nao foi possivel gravar settings.json", ex);
        }
    }
}
