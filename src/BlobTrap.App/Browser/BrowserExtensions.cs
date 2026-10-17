using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlobTrap.Core.Diagnostics;
using BlobTrap.Core.Tools;
using Microsoft.Web.WebView2.Core;

namespace BlobTrap.App.Browser;

/// <summary>An unpacked extension the browser profile has loaded, with the page its toolbar button opens.</summary>
public sealed record LoadedExtension(string Id, string Name, string? PopupPath)
{
    public Uri? PopupUri => PopupPath is null ? null : new Uri($"chrome-extension://{Id}/{PopupPath}");
}

/// <summary>
/// Loads every unpacked extension under <see cref="Directory"/> into the WebView2 profile.
///
/// One folder per extension, each holding its own <c>manifest.json</c>. WebView2 has no
/// toolbar, so an extension's button page is opened by <c>ExtensionsWindow</c> instead.
/// </summary>
public static class BrowserExtensions
{
    public static string Directory { get; } = Path.Combine(ToolLocator.AppDataDirectory, "extensions");

    /// <summary>
    /// Button pages for extensions that set them at run time (<c>chrome.action.setPopup</c>)
    /// instead of in the manifest, so the manifest alone cannot say where the page is.
    /// </summary>
    private static readonly Dictionary<string, string> KnownPopups = new(StringComparer.Ordinal)
    {
        [VideoDownloadHelper.Id] = "content/popup.html",
    };

    /// <summary>
    /// Adding is idempotent on WebView2's side: re-adding a folder already installed returns the
    /// same id, so this runs on every start and picks up folders dropped in since the last one.
    /// </summary>
    public static async Task<IReadOnlyList<LoadedExtension>> LoadAllAsync(CoreWebView2Profile profile)
    {
        var loaded = new List<LoadedExtension>();
        if (!System.IO.Directory.Exists(Directory)) return loaded;

        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
        {
            var manifest = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifest)) continue;

            try
            {
                var extension = await profile.AddBrowserExtensionAsync(folder);
                var popup = PopupFromManifest(await File.ReadAllTextAsync(manifest))
                    ?? KnownPopups.GetValueOrDefault(extension.Id);
                loaded.Add(new LoadedExtension(extension.Id, extension.Name, popup));
                Log.Info("extensions", $"Carregada: {extension.Name} ({extension.Id})");
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or COMException)
            {
                // One broken folder must not keep the browser or the other extensions from starting.
                Log.Warn("extensions", $"Extensão ignorada: {Path.GetFileName(folder)}", ex);
            }
        }

        return loaded;
    }

    /// <summary>
    /// The toolbar page, relative to the extension root: <c>action</c> on Manifest V3,
    /// <c>browser_action</c> on V2. Null when the extension has no button page.
    /// </summary>
    public static string? PopupFromManifest(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson, new JsonDocumentOptions
        {
            // Chrome tolerates comments in manifest.json, and some published extensions ship them.
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        foreach (var key in new[] { "action", "browser_action" })
        {
            if (document.RootElement.TryGetProperty(key, out var action)
                && action.ValueKind == JsonValueKind.Object
                && action.TryGetProperty("default_popup", out var popup)
                && popup.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(popup.GetString()))
            {
                return popup.GetString()!.TrimStart('/');
            }
        }

        return null;
    }
}

/// <summary>The Chrome Web Store listing of Video DownloadHelper; its id is also the one WebView2 assigns.</summary>
public static class VideoDownloadHelper
{
    public const string Id = "lmjnegcaeklhafolokijcfjliaokphfk";
}
