namespace BlobTrap.App.Browser;

/// <summary>
/// Points an extension's button page at the browser's page instead of at itself.
///
/// Button pages ask <c>chrome.tabs.query({ active: true, currentWindow: true })</c> for the tab
/// they act on. In Chrome the popup is not a tab, so that is the page behind it; in WebView2
/// every view is its own window, so the answer is the popup's own chrome-extension:// page and
/// the extension reports no media. Measured with Video DownloadHelper: without this the list
/// stays on "waiting for media", with it the page's video shows up and downloads.
///
/// BlobTrap has a single browser view, so "the first tab that is not an extension page" is it.
/// </summary>
public static class ExtensionTabShim
{
    public const string Script = """
        (() => {
          if (!globalThis.chrome?.tabs?.query) return;
          const query = chrome.tabs.query.bind(chrome.tabs);
          const isPage = tab => !(tab.url ?? '').startsWith('chrome-extension://');
          chrome.tabs.query = async (filter = {}, callback) => {
            const wantsCurrent = filter.active && (filter.currentWindow || filter.lastFocusedWindow);
            const tabs = wantsCurrent ? (await query({})).filter(isPage).slice(0, 1) : await query(filter);
            if (typeof callback === 'function') callback(tabs);
            return tabs;
          };
        })();
        """;
}
