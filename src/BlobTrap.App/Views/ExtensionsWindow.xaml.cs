using System.Windows;
using System.Windows.Controls;
using BlobTrap.App.Browser;
using BlobTrap.App.Theming;
using Microsoft.Web.WebView2.Core;

namespace BlobTrap.App.Views;

/// <summary>
/// Stands in for the toolbar button WebView2 does not have: shows an extension's button page
/// in a view that shares the browser's environment, so it talks to the same extension instance.
/// </summary>
public partial class ExtensionsWindow : Window
{
    private readonly CoreWebView2Environment _environment;

    public ExtensionsWindow(CoreWebView2Environment environment, IReadOnlyList<LoadedExtension> extensions)
    {
        InitializeComponent();
        WindowFit.ToWorkArea(this);

        WindowEffects.Attach(this);

        _environment = environment;

        var withPopup = extensions.Where(e => e.PopupUri is not null).ToList();
        if (withPopup.Count == 0)
        {
            ShowEmpty($"Nenhuma extensão com página de botão. Coloque a pasta descompactada da extensão em {BrowserExtensions.Directory} e abra o BlobTrap novamente.");
            return;
        }

        ExtensionCombo.ItemsSource = withPopup;
        ExtensionCombo.Visibility = withPopup.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => ExtensionCombo.SelectedIndex = 0;
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
