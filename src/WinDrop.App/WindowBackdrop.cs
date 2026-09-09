using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinDrop.App;

/// <summary>
/// Turns a plain WPF window into real glass, using DWM rather than imitation.
///
/// WPF cannot sample what is behind a window, so the usual "acrylic" in a WPF app is a
/// translucent brush over nothing — it goes grey on a grey desktop and gives the game
/// away the moment you drag it over something colourful. Windows 11 will do the real
/// thing if asked: DWM composites an actual blurred backdrop behind the client area.
///
/// The cost is that <c>AllowsTransparency</c> has to stay off. It forces WPF into a
/// layered window, which DWM will not composite a backdrop behind, and it is also what
/// most WPF samples use to fake rounded corners. So DWM owns the corners too.
///
/// Everything here degrades quietly. On a build without these attributes the calls
/// return a failure HRESULT that we ignore, and the window falls back to the solid
/// background set in XAML, which is still a perfectly reasonable dark window.
/// </summary>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerRound = 2;

    /// <summary>Acrylic: a heavier blur that samples the desktop behind the window.</summary>
    private const int BackdropAcrylic = 3;

    /// <summary>Mica: tints from the wallpaper, much subtler. The fallback.</summary>
    private const int BackdropMica = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Call from the window's SourceInitialized handler — the HWND does not exist before
    /// that, and setting the backdrop after the first render leaves a visible flash.
    /// </summary>
    public static void Apply(Window window, bool preferAcrylic = true)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Dark mode first: it decides how DWM tints the backdrop and draws the border,
        // and setting it after the backdrop can leave a light border on a dark window.
        Set(hwnd, DwmwaUseImmersiveDarkMode, 1);
        Set(hwnd, DwmwaWindowCornerPreference, CornerRound);

        // Extend the frame across the entire client area. A borderless window has no
        // non-client region, and DWM will not composite a backdrop behind a window that
        // has no frame to composite it into — which is why setting the backdrop type
        // alone leaves a flat black window. Negative margins mean "the whole thing".
        var sheet = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref sheet);

        int backdrop = preferAcrylic ? BackdropAcrylic : BackdropMica;

        if (Set(hwnd, DwmwaSystemBackdropType, backdrop) != 0 && preferAcrylic)
            Set(hwnd, DwmwaSystemBackdropType, BackdropMica);

        // WPF paints its own opaque background over whatever DWM composited, so both the
        // composition target and the window itself have to stand aside.
        if (HwndSource.FromHwnd(hwnd) is { } source)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        window.Background = Brushes.Transparent;
    }

    private static int Set(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
