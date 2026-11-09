using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Puck.App.UI;

/// <summary>
/// The notification-area icon: a round puck drawn at runtime at the size Windows wants (so it is
/// crisp at every scale), yellow-ringed while a puck is connected. Left click opens the flyout,
/// right click the menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu;
    Icon? _current;
    (bool Connected, bool Low, bool DarkTaskbar, int Size)? _drawn;

    public event Action? OpenRequested;
    public event Action? PairRequested;
    public event Action? FirmwareRequested;
    public event Action? SettingsRequested;
    public event Action? LogRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9f) };
        _menu.Items.Add("Open Puck", null, (_, _) => OpenRequested?.Invoke());
        _menu.Items.Add("Pair a puck…", null, (_, _) => PairRequested?.Invoke());
        _menu.Items.Add("Install firmware…", null, (_, _) => FirmwareRequested?.Invoke());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        _menu.Items.Add("Log", null, (_, _) => LogRequested?.Invoke());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit Puck", null, (_, _) => QuitRequested?.Invoke());
        foreach (Forms.ToolStripItem item in _menu.Items) item.Padding = new Forms.Padding(4, 3, 16, 3);
        ApplyMenuTheme();
        ThemeManager.Changed += ApplyMenuTheme;

        _icon = new Forms.NotifyIcon { ContextMenuStrip = _menu, Text = "Puck" };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        Update(connected: false, batteryLow: false, "Puck — Looking for your puck");
        _icon.Visible = true;
    }

    public void Update(bool connected, bool batteryLow, string tooltip)
    {
        // NotifyIcon.Text is limited to 127 characters
        _icon.Text = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;

        int size = Forms.SystemInformation.SmallIconSize.Width;
        var key = (connected, batteryLow, ThemeManager.TaskbarIsDark, size);
        if (_drawn == key) return;
        _drawn = key;
        var next = Draw(size, connected, batteryLow, ThemeManager.TaskbarIsDark);
        _icon.Icon = next;
        var old = _current;
        _current = next;
        if (old is not null) DestroyOwned(old);
    }

    public void ShowNotification(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    /// <summary>The puck from above: a ring (its rim) round dark glass, with a dot that is its face.</summary>
    public static Icon Draw(int size, bool connected, bool batteryLow, bool darkTaskbar)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var glyph = darkTaskbar ? Color.FromArgb(245, 245, 247) : Color.FromArgb(28, 28, 30);
            float s = size;
            float stroke = Math.Max(1.5f, s / 8f);
            var outer = new RectangleF(stroke / 2 + 0.5f, stroke / 2 + 0.5f, s - stroke - 1, s - stroke - 1);

            using (var glass = new SolidBrush(Color.FromArgb(darkTaskbar ? 255 : 235, 8, 8, 9)))
                g.FillEllipse(glass, outer);
            var ringColor = connected ? Color.FromArgb(255, 214, 10) : Color.FromArgb(darkTaskbar ? 170 : 150, glyph);
            using (var ring = new Pen(ringColor, stroke))
                g.DrawEllipse(ring, outer);

            // the face: two little eyes when connected, a dash (asleep) when not
            float eye = Math.Max(1.5f, s / 9f);
            float cy = s / 2, dx = s / 6.5f;
            using var face = new SolidBrush(connected ? Color.FromArgb(245, 245, 247) : Color.FromArgb(150, 245, 245, 247));
            if (connected)
            {
                g.FillEllipse(face, s / 2 - dx - eye / 2, cy - eye, eye, eye * 1.8f);
                g.FillEllipse(face, s / 2 + dx - eye / 2, cy - eye, eye, eye * 1.8f);
            }
            else
            {
                g.FillRectangle(face, s / 2 - dx - eye * 0.7f, cy - eye / 4, eye * 1.4f, Math.Max(1, eye / 2));
                g.FillRectangle(face, s / 2 + dx - eye * 0.7f, cy - eye / 4, eye * 1.4f, Math.Max(1, eye / 2));
            }

            if (batteryLow)
            {
                float d = Math.Max(4, s * 0.38f);
                using var red = new SolidBrush(Color.FromArgb(255, 69, 58));
                using var edge = new Pen(darkTaskbar ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243), Math.Max(1, s / 16f));
                var dot = new RectangleF(s - d - 0.5f, s - d - 0.5f, d, d);
                g.FillEllipse(red, dot);
                g.DrawEllipse(edge, dot);
            }
        }
        var hicon = bmp.GetHicon();
        return Icon.FromHandle(hicon);
    }

    static void DestroyOwned(Icon icon)
    {
        var handle = icon.Handle;
        icon.Dispose();
        DestroyIcon(handle);
    }

    void ApplyMenuTheme()
    {
        bool dark = ThemeManager.IsDark;
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(new MenuColors(dark)) { RoundedEdges = true };
        _menu.BackColor = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        _menu.ForeColor = dark ? Color.White : Color.FromArgb(26, 26, 26);
        foreach (Forms.ToolStripItem item in _menu.Items) item.ForeColor = _menu.ForeColor;
    }

    sealed class MenuColors(bool dark) : Forms.ProfessionalColorTable
    {
        readonly Color _bg = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        readonly Color _hover = dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
        readonly Color _border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
        readonly Color _sep = dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(225, 225, 225);

        public override Color ToolStripDropDownBackground => _bg;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _hover;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color SeparatorDark => _sep;
        public override Color SeparatorLight => _sep;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(nint handle);

    public void Dispose()
    {
        ThemeManager.Changed -= ApplyMenuTheme;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (_current is not null) DestroyOwned(_current);
    }
}
