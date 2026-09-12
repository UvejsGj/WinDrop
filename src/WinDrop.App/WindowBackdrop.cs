using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinDrop.App;

/// <summary>
/// Window chrome from DWM: dark border, rounded corners, and a shadow, on a window that
/// has no system title bar.
///
/// The window paints its own backdrop now. An earlier design asked DWM for acrylic, but
/// WPF cannot read the pixels DWM composites behind a window, so nothing drawn inside could
/// refract them — glass panels over it could only ever be tinted rectangles. The scene is
/// opaque and in-process instead, which is what lets the glass bend it.
/// </summary>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerRound = 2;
    private const int BackdropNone = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call from SourceInitialized: the HWND does not exist before it.</summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Every call degrades quietly: on a build without an attribute it returns a failure
        // HRESULT, ignored, and the window is merely square-cornered.
        Set(hwnd, DwmwaUseImmersiveDarkMode, 1);
        Set(hwnd, DwmwaWindowCornerPreference, CornerRound);
        Set(hwnd, DwmwaSystemBackdropType, BackdropNone);

        // A borderless window has no frame for DWM to hang a shadow on. Extending the frame
        // into the client area gives it one; the opaque scene then covers what DWM draws.
        var sheet = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref sheet);
    }

    private static int Set(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
