using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BlobTrap.App.Browser;
using BlobTrap.App.Theming;
using BlobTrap.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace BlobTrap.App.Views;

/// <summary>
/// Stands in for the toolbar button WebView2 does not have: shows an extension's button page
/// in a view that shares the browser's environment, so it talks to the same extension instance.
/// </summary>
public partial class ExtensionsWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly Func<Task<IReadOnlyList<LoadedExtension>>> _reload;

    /// <param name="reload">Loads the extensions folder into the browser again, after an install.</param>
    public ExtensionsWindow(
        CoreWebView2Environment environment,
        IReadOnlyList<LoadedExtension> extensions,
        Func<Task<IReadOnlyList<LoadedExtension>>> reload)
    {
        InitializeComponent();
        WindowFit.ToWorkArea(this);

        WindowEffects.Attach(this);

        _environment = environment;
        _reload = reload;

        Loaded += (_, _) => ShowExtensions(extensions);
    }

    private void ShowExtensions(IReadOnlyList<LoadedExtension> extensions)
    {
        var withPopup = extensions.Where(e => e.PopupUri is not null).ToList();
        if (withPopup.Count == 0)
        {
            ShowEmpty("Nenhuma extensão instalada. Instale o Video DownloadHelper ou coloque a pasta " +
                      $"descompactada de outra extensão em {BrowserExtensions.Directory}.");
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        Popup.Visibility = Visibility.Visible;
        ExtensionCombo.ItemsSource = withPopup;
        ExtensionCombo.Visibility = withPopup.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ExtensionCombo.SelectedIndex = 0;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        InstallButton.Content = "Baixando…";

        try
        {
            var target = Path.Combine(BrowserExtensions.Directory, "video-downloadhelper");
            Directory.CreateDirectory(BrowserExtensions.Directory);
            await CrxPackage.InstallFromStoreAsync(VideoDownloadHelper.Id, target, CancellationToken.None);
            Log.Info("extensions", "Video DownloadHelper instalado da Chrome Web Store.");

            ShowExtensions(await _reload());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn("extensions", "Falha ao instalar o Video DownloadHelper", ex);
            EmptyText.Text = $"Não foi possível instalar: {ex.Message}";
        }
        finally
        {
            InstallButton.IsEnabled = true;
            InstallButton.Content = "Instalar Video DownloadHelper";
        }
    }

    private async void OnExtensionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ExtensionCombo.SelectedItem is not LoadedExtension extension) return;

        try
        {
            if (Popup.CoreWebView2 is null)
            {
                await Popup.EnsureCoreWebView2Async(_environment);
                await Popup.CoreWebView2!.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionTabShim.Script);
            }

            Popup.CoreWebView2.Navigate(extension.PopupUri!.AbsoluteUri);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ShowEmpty($"Não foi possível abrir {extension.Name}: {ex.Message}");
        }
    }

    private void ShowEmpty(string message)
    {
        Popup.Visibility = Visibility.Collapsed;
        EmptyText.Text = message;
        EmptyState.Visibility = Visibility.Visible;
    }
}
