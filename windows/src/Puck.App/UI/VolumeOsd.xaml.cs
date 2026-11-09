using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Puck.App.UI;

/// <summary>The volume pill: click-through, never takes focus, fades out 1.2 s after the last change.</summary>
public partial class VolumeOsd : Window
{
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    public VolumeOsd()
    {
        InitializeComponent();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { if (Opacity == 0) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) =>
        {
            // WS_EX_TRANSPARENT: clicks fall through; NOACTIVATE + TOOLWINDOW: no focus, no Alt+Tab
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
        };
    }

    /// <summary>Shows the level (call on the UI thread).</summary>
    public void ShowLevel(int volume, bool muted)
    {
        Bar.Value = muted ? 0 : volume;
        Bar.Opacity = muted ? 0.4 : 1;
        Number.Text = muted ? "Muted" : volume.ToString();
        // Segoe Fluent Icons: Mute, Volume0..3
        Glyph.Text = muted || volume == 0 ? "" : volume < 34 ? "" : volume < 67 ? "" : "";

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            Place();
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>
    /// Bottom centre of the work area of the monitor under the mouse (where the user is looking),
    /// clear of the taskbar. Placed in physical pixels, as the flyout is: WPF's Left/Top are DIPs of
    /// whichever monitor the window happens to be on.
    /// </summary>
    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var wa = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        // hop onto that monitor first, so the window takes its DPI, then measure
        SetWindowPos(hwnd, HWND_TOPMOST, wa.Left + 1, wa.Top + 1, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int gap = (int)Math.Round(48 * GetDpiForWindow(hwnd) / 96.0);
        SetWindowPos(hwnd, HWND_TOPMOST, wa.Left + (wa.Width - w) / 2, wa.Bottom - h - gap, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static readonly nint HWND_TOPMOST = -1;
    const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(nint hwnd);

    const int GWL_EXSTYLE = -20;
    const nint WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint value);
}
