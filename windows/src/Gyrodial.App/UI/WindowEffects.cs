using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Gyrodial.App.UI;

public enum Backdrop
{
    /// <summary>Mica: the main-window material (Settings).</summary>
    Mica = 2,
    /// <summary>Acrylic: the transient-window material (the flyout).</summary>
    Acrylic = 3,
}

/// <summary>
/// Windows 11 window chrome through DWM: dark title bars, rounded corners and the Mica/Acrylic
/// system backdrops. Every call is a no-op where the attribute doesn't exist (Windows 10, older 11),
/// and <see cref="TryApplyBackdrop"/> says whether the backdrop took, so the caller can fall back to
/// a solid background.
/// </summary>
public static partial class WindowEffects
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMWCP_ROUND = 2;
    const int DWMWCP_ROUNDSMALL = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    /// <summary>System backdrops arrived in Windows 11 22H2 (build 22621).</summary>
    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    public static void SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        int v = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }

    public static void SetRoundCorners(Window window, bool small = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        int v = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
    }

    /// <summary>
    /// Puts the DWM material behind the window. For it to show through, WPF must draw nothing behind
    /// the content: the HwndSource's clear color goes transparent and the frame is extended over the
    /// whole client area. Call after the window has a handle (SourceInitialized).
    /// </summary>
    public static bool TryApplyBackdrop(Window window, Backdrop backdrop, bool dark)
    {
        SetDarkTitleBar(window, dark);
        if (!BackdropSupported) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return false;
        try
        {
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return false;
            int v = (int)backdrop;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int)) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
