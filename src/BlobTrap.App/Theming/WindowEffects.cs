using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BlobTrap.App.Theming;

public enum Backdrop
{
    /// <summary>
    /// An opaque themed surface. The right choice for a window hosting a child HWND such as
    /// WebView2: the browser covers most of the window anyway, so a material would barely show,
    /// and any region left unpainted by DWM renders black instead of blending.
    /// </summary>
    Solid,

    /// <summary>The Mica material Windows uses for long-lived windows.</summary>
    Mica,

    /// <summary>The acrylic material Windows uses for dialogs and flyouts.</summary>
    Acrylic,
}

/// <summary>
/// Applies the Windows 11 window chrome - Mica backdrop, dark title bar and rounded corners -
/// through DWM.
///
/// Every call is best effort: these attributes were added across different Windows builds, and
/// an older build simply returns a failure HRESULT. The app then keeps its solid background,
/// which is why the palette always defines one.
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerPreferenceRound = 2;

    /// <summary>DWMSBT_MAINWINDOW - the Mica material used by Settings and Explorer.</summary>
    private const int BackdropMica = 2;

    /// <summary>DWMSBT_TRANSIENTWINDOW - the acrylic material used by dialogs and flyouts.</summary>
    private const int BackdropAcrylic = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    /// <summary>
    /// Hooks a window up to the system look. Call from the constructor: it defers the native
    /// work to SourceInitialized, when the HWND exists, and re-applies on every theme change.
    /// </summary>
    public static void Attach(Window window, Backdrop backdrop = Backdrop.Solid)
    {
        window.SourceInitialized += (_, _) => Apply(window, backdrop);

        void OnThemeChanged(object? sender, EventArgs e) => Apply(window, backdrop);

        ThemeManager.Changed += OnThemeChanged;
        window.Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    public static void Apply(Window window, Backdrop backdrop = Backdrop.Solid)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        SetDarkTitleBar(handle, ThemeManager.Theme == AppTheme.Dark);
        SetRoundedCorners(handle);

        var backdropType = backdrop switch
        {
            Backdrop.Mica => BackdropMica,
            Backdrop.Acrylic => BackdropAcrylic,
            _ => 0,
        };

        if (backdropType != 0 && TryEnableBackdrop(handle, backdropType))
        {
            // DWM only paints the material where the window itself does not, so the client area
            // has to be see-through. Controls on top keep their own opaque brushes.
            ExtendFrame(handle);
            window.Background = Brushes.Transparent;
            return;
        }

        window.SetResourceReference(Window.BackgroundProperty, "AppBackground");
    }

    private static bool TryEnableBackdrop(IntPtr handle, int backdropType)
    {
        var value = backdropType;
        return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref value, sizeof(int)) == 0;
    }

    private static void SetDarkTitleBar(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static void SetRoundedCorners(IntPtr handle)
    {
        var value = CornerPreferenceRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref value, sizeof(int));
    }

    /// <summary>A margin of -1 on every side extends the glass frame across the whole client area.</summary>
    private static void ExtendFrame(IntPtr handle)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);
    }
}
