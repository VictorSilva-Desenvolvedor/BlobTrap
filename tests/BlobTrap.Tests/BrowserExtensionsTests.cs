using BlobTrap.App.Browser;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O WebView2 não tem barra de ferramentas, então a página do botão de cada extensão é aberta
/// pelo BlobTrap. Ela vem do manifest — ou não vem, e aí a janela não tem o que mostrar.
/// </summary>
public class BrowserExtensionsTests
{
    [Fact]
    public void ManifestV3_LeActionDefaultPopup()
    {
        Assert.Equal("popup.html", BrowserExtensions.PopupFromManifest(
            """{ "manifest_version": 3, "action": { "default_popup": "popup.html" } }"""));
    }

    [Fact]
    public void ManifestV2_LeBrowserAction()
    {
        Assert.Equal("ui/popup.html", BrowserExtensions.PopupFromManifest(
            """{ "manifest_version": 2, "browser_action": { "default_popup": "ui/popup.html" } }"""));
    }

    [Fact]
    public void BarraInicial_ERemovida()
    {
        // O caminho vira chrome-extension://<id>/<caminho>; uma barra a mais dá "//" na URL.
        Assert.Equal("content/popup.html", BrowserExtensions.PopupFromManifest(
            """{ "action": { "default_popup": "/content/popup.html" } }"""));
    }

    [Fact]
    public void PopupDefinidoEmTempoDeExecucao_DevolveNulo()
    {
        // É o caso do Video DownloadHelper: "action" sem default_popup, que vem de setPopup.
        Assert.Null(BrowserExtensions.PopupFromManifest(
            """{ "action": { "default_title": "Video Download Helper" } }"""));
    }

    [Fact]
    public void ComentarioEVirgulaSobrando_SaoAceitos()
    {
        Assert.Equal("p.html", BrowserExtensions.PopupFromManifest(
            """
            {
              // o Chrome aceita comentário no manifest
              "action": { "default_popup": "p.html", },
            }
            """));
    }
}
