using System.IO;
using System.Windows;
using System.Windows.Input;
using BlobTrap.App.Browser;
using BlobTrap.App.Theming;
using BlobTrap.App.ViewModels;
using BlobTrap.Core.Models;
using BlobTrap.Core.Tools;
using Microsoft.Web.WebView2.Core;

namespace BlobTrap.App.Views;

public partial class MainWindow : Window, IMediaPicker
{
    private readonly MainViewModel _viewModel;
    private CdpSniffer? _sniffer;
    private PageMediaProbe? _probe;

    public MainWindow()
        : this(null)
    {
    }

    /// <summary>
    /// Takes a pre-built view model so the design preview can reuse this exact window with
    /// sample content instead of a second copy of the layout that would drift out of step.
    /// </summary>
    public MainWindow(MainViewModel? viewModel)
    {
        InitializeComponent();

        WindowEffects.Attach(this);

        _viewModel = viewModel ?? new MainViewModel(Dispatcher);
        _viewModel.Picker = this;
        DataContext = _viewModel;

        _viewModel.NavigationRequested += OnNavigationRequested;
        _viewModel.FocusAddressRequested += (_, _) => { AddressBox.Focus(); AddressBox.SelectAll(); };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The design preview reviews the interface, not the browser: starting WebView2 would
        // add a network dependency and a blank white pane to every screenshot.
        if (_viewModel.IsDesignSample) return;

        try
        {
            // A dedicated profile keeps the user's own browser data untouched and lets logins
            // persist between BlobTrap sessions.
            var userDataFolder = Path.Combine(ToolLocator.AppDataDirectory, "webview");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "WebView2 indisponível.";
            MessageBox.Show(this,
                "Não foi possível iniciar o navegador embutido.\n\n" +
                "Instale o 'Microsoft Edge WebView2 Runtime' e abra o BlobTrap novamente.\n\n" + ex.Message,
                "BlobTrap", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var core = Browser.CoreWebView2;

        _sniffer = new CdpSniffer(_viewModel.Registry);
        _sniffer.Warning += (_, message) => Dispatcher.BeginInvoke(() => _viewModel.StatusText = message);
        await _sniffer.AttachAsync(core);

        _probe = new PageMediaProbe(_viewModel.Registry);
        _probe.BlobSourceDetected += OnBlobSourceDetected;
        await _probe.AttachAsync(core);

        core.SourceChanged += OnSourceChanged;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.NewWindowRequested += OnNewWindowRequested;
        Browser.NavigationStarting += OnNavigationStarting;

        _sniffer.UpdatePage(SafeUri(core.Source), core.DocumentTitle);

        var home = MainViewModel.NormalizeAddress(_viewModel.Settings.HomePage);
        if (home is not null) Browser.Source = home;
    }

    private void OnNavigationRequested(object? sender, Uri target)
    {
        if (Browser.CoreWebView2 is null)
        {
            Browser.Source = target;
            return;
        }

        Browser.CoreWebView2.Navigate(target.AbsoluteUri);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Only a real top-level navigation means "new page", so in-page fetches keep their context.
        if (!e.IsRedirected && Browser.CoreWebView2 is not null)
        {
            _sniffer?.ResetPageState();
            _viewModel.Registry.Clear();
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var core = Browser.CoreWebView2;
        if (core is null) return;

        _viewModel.AddressText = core.Source;
        _sniffer?.UpdatePage(SafeUri(core.Source), core.DocumentTitle);
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        var core = Browser.CoreWebView2;
        if (core is null) return;

        _viewModel.CurrentPageTitle = core.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "BlobTrap" : $"{core.DocumentTitle} - BlobTrap";
        _sniffer?.UpdatePage(SafeUri(core.Source), core.DocumentTitle);
    }

    /// <summary>Keeps target=_blank links inside the app, where the sniffer can see them.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Browser.CoreWebView2?.Navigate(e.Uri);
    }

    private void OnBlobSourceDetected(object? sender, string blobUrl) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.Candidates.Count > 0) return;

            _viewModel.StatusText = "Player usando blob: (MSE). Comece a reproduzir o vídeo para capturar o manifesto.";
            _viewModel.NotePlayerUsesBlob();
        });

    private static Uri? SafeUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack) Browser.GoBack();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoForward) Browser.GoForward();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e) => Browser.Reload();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        _viewModel.NavigateCommand.Execute(null);
        e.Handled = true;
    }

    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        var window = new ToolsWindow(_viewModel) { Owner = this };
        window.ShowDialog();
        _viewModel.RefreshToolState();
    }

    public Task<DownloadPlan?> PickAsync(MediaSource source, string downloadDirectory)
    {
        var dialog = new QualityWindow(source, _viewModel) { Owner = this };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    public void ShowMessage(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnClosed(object? sender, EventArgs e)
    {
        _sniffer?.Dispose();
        _viewModel.Dispose();
    }
}
