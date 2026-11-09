using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Gyrodial.App.UI;

/// <summary>What the flyout's buttons do (the app wires them to the hub and Settings).</summary>
public sealed record FlyoutActions(
    Action OpenSettings,
    Action Pair,
    Action CancelPairing,
    Action<string?> InstallFirmware,
    Action OpenLog)
{
    public static FlyoutActions None { get; } = new(() => { }, () => { }, () => { }, _ => { }, () => { });
}

/// <summary>
/// The panel that opens from the tray icon, like Windows 11's quick settings: anchored to the
/// corner next to the taskbar, Acrylic behind it, gone as soon as it loses focus.
/// </summary>
public partial class FlyoutWindow : Window
{
    readonly AppModel _model;
    readonly FlyoutActions _actions;
    DateTime _hiddenAt = DateTime.MinValue;
    bool _backdrop;

    public FlyoutWindow(AppModel model, FlyoutActions actions)
    {
        InitializeComponent();
        DataContext = model;
        _model = model;
        _actions = actions;
        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.Changed += ApplyChrome;
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };
        SizeChanged += (_, _) => { if (IsVisible) Place(); };
    }

    void ApplyChrome()
    {
        WindowEffects.SetRoundCorners(this);
        _backdrop = WindowEffects.TryApplyBackdrop(this, Backdrop.Acrylic, ThemeManager.IsDark);
        // Without a system backdrop, an opaque background (the tint alone would show black behind it)
        Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, _backdrop ? "Brush.FlyoutTint" : "Brush.Window");
    }

    /// <summary>Opens it, or closes it if it's open (a second click on the tray icon).</summary>
    public void Toggle()
    {
        // The click on the tray icon deactivates the flyout first: that click shouldn't reopen it.
        if (IsVisible || DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300))
        {
            HideFlyout();
            return;
        }
        ShowFlyout();
    }

    public void ShowFlyout()
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        Place();
        Activate();
        Focus();

        var slide = new TranslateTransform(0, 10);
        Root.RenderTransform = slide;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    /// <summary>
    /// Puts the window in the work-area corner next to the taskbar, on the monitor under the mouse.
    /// Done in physical pixels with SetWindowPos: WPF's own Left/Top are DIPs of whichever monitor
    /// the window is on, which goes wrong when the taskbar's monitor has a different scale.
    /// </summary>
    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var wa = screen.WorkingArea;
        var b = screen.Bounds;

        // first hop onto that monitor so WPF rescales for its DPI, then measure
        SetWindowPos(hwnd, HWND_TOPMOST, wa.Left + 1, wa.Top + 1, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int margin = (int)Math.Round(12 * GetDpiForWindow(hwnd) / 96.0);

        int x = wa.Right - w - margin, y = wa.Bottom - h - margin;  // taskbar at the bottom (or hidden)
        if (wa.Top > b.Top) y = wa.Top + margin;                     // at the top
        else if (wa.Left > b.Left) { x = wa.Left + margin; }         // on the left
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void OnSettings(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _actions.OpenSettings();
    }

    void OnPair(object sender, RoutedEventArgs e) => _actions.Pair();

    void OnCancelPairing(object sender, RoutedEventArgs e) => _actions.CancelPairing();

    void OnMore(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void OnInstall(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _actions.InstallFirmware(null);
    }

    void OnInstallForeign(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _actions.InstallFirmware(_model.ForeignPort.Length > 0 ? _model.ForeignPort : null);
    }

    void OnLog(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _actions.OpenLog();
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.Changed -= ApplyChrome;
        base.OnClosed(e);
    }

    // ---- Win32
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
}
