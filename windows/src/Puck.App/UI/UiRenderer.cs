using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Puck.App.Transport;
using Puck.Core;

namespace Puck.App.UI;

/// <summary>
/// <c>Puck.exe --render-ui DIR</c>: renders the flyout, Settings pages and the OSD with sample data
/// to PNGs (1.5× scale, both themes), for checking the UI without a puck. DWM materials (Mica,
/// Acrylic) aren't part of a WPF render, so these show the solid fallback backgrounds.
/// </summary>
public static class UiRenderer
{
    const double Scale = 1.5;

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), $"puck-render-{Environment.ProcessId}.json"));
        foreach (var dark in new[] { true, false })
        {
            ThemeManager.Force(dark);
            string theme = dark ? "dark" : "light";

            var model = SampleModel(settings, LinkState.Connected);
            RenderFlyout(model, Path.Combine(dir, $"flyout-connected-{theme}.png"));
            var pairing = SampleModel(settings, LinkState.Pairing);
            pairing.PairingProgress = 1;
            RenderFlyout(pairing, Path.Combine(dir, $"flyout-pairing-{theme}.png"));

            var searching = SampleModel(settings, LinkState.Searching);
            searching.NowUtc = DateTime.UtcNow.AddSeconds(20);  // long enough to offer "Can't find it?"
            RenderFlyout(searching, Path.Combine(dir, $"flyout-searching-{theme}.png"));

            var pairWindow = SampleModel(settings, LinkState.Searching);
            pairWindow.PairingMode = true;
            pairWindow.PairingWindowLeft = TimeSpan.FromSeconds(102);
            RenderFlyout(pairWindow, Path.Combine(dir, $"flyout-pairwindow-{theme}.png"));

            var refused = SampleModel(settings, LinkState.Connecting);
            refused.Notice = new PairingNotice(PairingNoticeKind.Refused, "Puck-1A2B", DateTime.UtcNow.AddSeconds(42));
            refused.NowUtc = DateTime.UtcNow;
            RenderFlyout(refused, Path.Combine(dir, $"flyout-refused-{theme}.png"));

            var timedOut = SampleModel(settings, LinkState.Connecting);
            timedOut.Notice = new PairingNotice(PairingNoticeKind.TimedOut, "Puck-1A2B", DateTime.UtcNow);
            timedOut.NowUtc = DateTime.UtcNow;
            RenderFlyout(timedOut, Path.Combine(dir, $"flyout-timeout-{theme}.png"));

            var foreign = SampleModel(settings, LinkState.Searching);
            foreign.ForeignPort = "COM4";
            RenderFlyout(foreign, Path.Combine(dir, $"flyout-foreign-{theme}.png"));

            var vm = new SettingsViewModel(settings, model, hub: null);
            var window = new SettingsWindow(vm);
            foreach (var page in Enum.GetValues<SettingsPage>())
            {
                if (page == SettingsPage.Log)
                {
                    // sample lines for the screenshot: the log is memory-only in this mode (App.OnStartup),
                    // so they never reach the user's log file
                    AppLog.Instance.Info("usb", "Opened COM7");
                    AppLog.Instance.Info("usb", "COM7: Puck-1A2B (fw 0.1.0), paired");
                    AppLog.Instance.Info("link", "Talking to Puck-1A2B over USB");
                    AppLog.Instance.Debug("usb", "ESP-ROM:esp32s3-20210327");
                }
                window.Nav.SelectedIndex = (int)page;
                RenderWindow(window, window.Root, Path.Combine(dir, $"settings-{page.ToString().ToLowerInvariant()}-{theme}.png"));
            }
            window.Hide();

            var osd = new VolumeOsd();
            osd.ShowLevel(62, false);
            Render(osd.Pill, Path.Combine(dir, $"osd-{theme}.png"), solidBackground: false);
            osd.Close();

            SaveTrayIcon(Path.Combine(dir, $"tray-{theme}.png"), dark);
        }
        try { File.Delete(Path.Combine(Path.GetTempPath(), $"puck-render-{Environment.ProcessId}.json")); } catch (IOException) { }
    }

    static AppModel SampleModel(SettingsStore settings, LinkState state)
    {
        var m = new AppModel
        {
            State = state,
            PuckName = "Puck-1A2B",
            PuckId = "a1b2c3d4e5f60708",
            Firmware = "0.1.0",
            Transport = "usb",
            Face = "home",
            Battery = 87,
            Charging = true,
            UsbPowered = true,
            Volume = 62,
            PairingName = state == LinkState.Pairing ? "Puck-1A2B" : "",
        };
        if (state == LinkState.Connected)
        {
            m.NowTitle = "Teardrop";
            m.NowArtist = "Massive Attack";
            m.NowSource = "Spotify";
            m.NowPlaying = true;
        }
        m.Integrations.Add(new IntegrationItem(new SampleIntegration("claude", "Claude", "Ready · tap the Claude face to talk", "Open conversation"), settings));
        m.Integrations.Add(new IntegrationItem(new SampleIntegration("claude-code", "Claude Code", "Hook installed · listening on 127.0.0.1:47825", "Install Claude Code hook", "Remove hook"), settings));
        m.Integrations.Add(new IntegrationItem(new SampleIntegration("dialkit", "DialKit", "No pages connected"), settings));
        m.Integrations.Add(new IntegrationItem(new SampleIntegration("teams", "Microsoft Teams", "Off"), settings));
        foreach (var item in m.Integrations) item.PuckConnected = state == LinkState.Connected;
        return m;
    }

    static void RenderFlyout(AppModel model, string path)
    {
        var flyout = new FlyoutWindow(model, FlyoutActions.None);
        flyout.Left = -30000;
        flyout.Top = -30000;
        flyout.ShowActivated = false;
        flyout.Show();
        Pump();
        RenderWindow(flyout, flyout.Root, path);
        flyout.Close();
    }

    static void RenderWindow(Window window, FrameworkElement root, string path)
    {
        if (!window.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = -30000;
            window.ShowActivated = false;
            window.Show();
        }
        Render(root, path, solidBackground: true);
    }

    static void Render(FrameworkElement root, string path, bool solidBackground)
    {
        Pump();
        if (solidBackground)
            root.SetResourceReference(root is Border ? Border.BackgroundProperty : Panel.BackgroundProperty, "Brush.Window");
        root.UpdateLayout();
        Pump();

        int w = (int)Math.Ceiling(root.ActualWidth * Scale), h = (int)Math.Ceiling(root.ActualHeight * Scale);
        var bmp = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(path)) png.Save(f);
        Console.WriteLine(path);
    }

    static void SaveTrayIcon(string path, bool dark)
    {
        using var big = new System.Drawing.Bitmap(4 * 40, 40);
        using (var g = System.Drawing.Graphics.FromImage(big))
        {
            g.Clear(dark ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(243, 243, 243));
            int x = 4;
            foreach (var (connected, low, size) in new[] { (true, false, 16), (false, false, 16), (true, true, 24), (true, false, 32) })
            {
                using var icon = TrayIcon.Draw(size, connected, low, dark);
                g.DrawIcon(icon, new System.Drawing.Rectangle(x, (40 - size) / 2, size, size));
                x += 40;
            }
        }
        big.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Lets bindings, templates, layout and the short toggle animations (120 ms) settle.</summary>
    static void Pump()
    {
        var until = DateTime.UtcNow.AddMilliseconds(250);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        } while (DateTime.UtcNow < until);
    }

    sealed class SampleIntegration(string id, string name, string status, params string[] actions) : IPuckIntegration
    {
        public string Id => id;
        public string DisplayName => name;
        public string Status => status;
        public event EventHandler? StatusChanged { add { } remove { } }
        public IReadOnlyList<IntegrationAction> Actions { get; } = actions.Select(a => new IntegrationAction(a, () => Task.FromResult<string?>(null))).ToList();
        public void Start(IPuckHub hub, IIntegrationHost host) { }
        public void Dispose() { }
    }
}
