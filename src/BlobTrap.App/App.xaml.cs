using System.Windows;
using BlobTrap.App.Theming;
using BlobTrap.Core.Download;
using BlobTrap.App.ViewModels;
using BlobTrap.App.Views;

namespace BlobTrap.App;

public partial class App : Application
{
    /// <summary>
    /// Opens a window filled with sample data instead of the browser, so the interface can be
    /// reviewed and screenshotted without a network round trip or a real download first.
    /// An optional suffix picks which window: "--design-preview:quality" or ":tools".
    /// </summary>
    private const string DesignPreviewFlag = "--design-preview";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Brushes have to exist before the first window resolves its DynamicResource bindings.
        ThemeManager.Initialize();

        // Em segundo plano: a varredura percorre disco e o app nao deve esperar por ela para
        // abrir. Nao ha nada para relatar se falhar - o proprio SweepOrphans ja engole o que
        // nao conseguir apagar, e a proxima abertura tenta de novo.
        _ = Task.Run(() => WorkspaceCleaner.SweepOrphans(DateTimeOffset.UtcNow));

        var preview = e.Args.FirstOrDefault(a => a.StartsWith(DesignPreviewFlag, StringComparison.OrdinalIgnoreCase));

        if (preview is null)
        {
            new MainWindow().Show();
            return;
        }

        ShowDesignPreview(preview[DesignPreviewFlag.Length..].TrimStart(':', '='));
    }

    private void ShowDesignPreview(string target)
    {
        // The preview reuses the real windows, so what gets reviewed is what ships.
        var viewModel = MainViewModel.CreateDesignSample(Dispatcher);

        Window window = target.ToLowerInvariant() switch
        {
            "quality" => new QualityWindow(DesignData.Source(), viewModel),
            "tools" => new ToolsWindow(viewModel),
            _ => new MainWindow(viewModel),
        };

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Shutdown();
        base.OnExit(e);
    }
}
