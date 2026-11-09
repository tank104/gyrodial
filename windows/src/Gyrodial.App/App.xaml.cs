using System.Windows;
using System.Windows.Threading;
using Gyrodial.App.Services;
using Gyrodial.App.Transport;
using Gyrodial.App.UI;
using Gyrodial.Core;

namespace Gyrodial.App;

/// <summary>
/// Gyrodial for Windows: a tray app. Starts the connection to the dial, the built-in services (volume,
/// media, mouse) and the integrations, and owns the tray icon, flyout, OSD and Settings.
///
///   Gyrodial.exe                  start and open the flyout
///   Gyrodial.exe --tray           start hidden (Start with Windows)
///   Gyrodial.exe --render-ui DIR  write screenshots of the UI with sample data to DIR, then exit
///   Gyrodial.exe --self-test      check the connection logic against a fake dial, then exit
///   Gyrodial.exe --uninstall      remove the Start with Windows entry and the Claude Code hook (the uninstaller runs this)
/// </summary>
public partial class App : Application
{
    // A property, not a field: --self-test and --render-ui switch the log to memory-only first
    static AppLog Log => AppLog.Instance;
    readonly List<IDisposable> _disposables = [];
    readonly List<IDialIntegration> _integrations = [];
    Mutex? _instance;
    SettingsStore _settings = null!;
    ConnectionManager _hub = null!;
    TrayIcon _tray = null!;
    FlyoutWindow _flyout = null!;
    SettingsViewModel _settingsVm = null!;
    SettingsWindow? _settingsWindow;
    VolumeOsd _osd = null!;

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int render = Array.FindIndex(e.Args, a => a.Equals("--render-ui", StringComparison.OrdinalIgnoreCase));
        if (render >= 0)
        {
            AppLog.UseMemoryOnly();
            var dir = render + 1 < e.Args.Length ? e.Args[render + 1] : "ui-shots";
            int code = 0;
            try { UiRenderer.Run(dir); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                code = 1;
            }
            IsExiting = true;
            Shutdown(code);
            return;
        }

        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            // Run by the uninstaller: undo what the app set up outside its own folder
            AppLog.UseMemoryOnly();
            AutoStart.Apply(false);
#if !NO_INTEGRATIONS
            try { new Gyrodial.Integrations.ClaudeCode.HookInstaller().Remove(); }
            catch (Exception) { }  // a settings file it can't rewrite is left alone, as Remove says
#endif
            IsExiting = true;
            Shutdown(0);
            return;
        }

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.UseMemoryOnly();
            int code = Diagnostics.SelfTest.Run();
            IsExiting = true;
            Shutdown(code);
            return;
        }

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.SignalFirstInstance();
            IsExiting = true;
            Shutdown();
            return;
        }

        // Before the log or the settings are opened: 0.3.0 renamed the app from Puck to Gyrodial
        var moved = AppPaths.MigrateOldDataDir();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("app", "Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("app", $"Unhandled exception: {ex.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Warn("app", $"Unobserved task exception: {ex.Exception.GetBaseException().Message}");
            ex.SetObserved();
        };

        Log.Info("app", $"Gyrodial {ConnectionManager.AppVersion} starting on Windows {Environment.OSVersion.Version}");
        ThemeManager.Initialize();
        if (moved is not null) Log.Info("app", $"{moved} (the app used to be called Puck)");
        _settings = new SettingsStore();
        if (AutoStart.MigrateOldName(_settings.Current.StartWithWindows))
            Log.Info("app", "Start with Windows now starts Gyrodial instead of Puck");
        // The Run key is written only when the user flips "Start with Windows" (SettingsViewModel),
        // never here: launching from a build folder or a zip must not register that path.

        _tray = new TrayIcon();
        _disposables.Add(_tray);
        var host = new IntegrationHost(_settings, _tray, Dispatcher);

        _hub = new ConnectionManager(_settings);
        _hub.Paired += name => host.ShowNotification("Gyrodial paired", $"{name} is paired with this PC.");
        var audio = new AudioService(_hub);
        var media = new MediaService(_hub);
        _disposables.Add(audio);
        _disposables.Add(media);
        _disposables.Add(new MouseService(_hub, _settings));
        _disposables.Add(new DialEventsService(_hub, host));

        StartIntegrations(host);

        var controller = new AppController(_hub, audio, media, _integrations, _settings, _tray, Dispatcher);
        _disposables.Add(controller);
        _settingsVm = new SettingsViewModel(_settings, controller.Model, _hub);
        _flyout = new FlyoutWindow(controller.Model, new FlyoutActions(
            OpenSettings: () => OpenSettings(SettingsPage.General),
            Pair: _hub.StartPairing,
            CancelPairing: _hub.CancelPairing,
            InstallFirmware: port =>
            {
                if (port is not null) _settingsVm.Firmware.SelectPort(port);
                OpenSettings(SettingsPage.Firmware);
            },
            OpenLog: () => OpenSettings(SettingsPage.Log)));
        _osd = new VolumeOsd();
        audio.ChangedByDial += (vol, mute) =>
        {
            if (_settings.Current.ShowVolumeOverlay) Dispatcher.BeginInvoke(() => _osd.ShowLevel(vol, mute));
        };

        _tray.OpenRequested += () => _flyout.Toggle();
        _tray.PairRequested += () => { _hub.StartPairing(); _flyout.ShowFlyout(); };
        _tray.FirmwareRequested += () => OpenSettings(SettingsPage.Firmware);
        _tray.SettingsRequested += () => OpenSettings(SettingsPage.General);
        _tray.LogRequested += () => OpenSettings(SettingsPage.Log);
        _tray.QuitRequested += Quit;
        SingleInstance.Listen(() => Dispatcher.BeginInvoke(() => _flyout.ShowFlyout()));

        _hub.Start();

        if (!e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.BeginInvoke(() => _flyout.ShowFlyout(), DispatcherPriority.ApplicationIdle);
    }

    void StartIntegrations(IIntegrationHost host)
    {
#if !NO_INTEGRATIONS
        IReadOnlyList<IDialIntegration> all;
        try
        {
            all = Gyrodial.Integrations.IntegrationCatalog.CreateAll();
        }
        catch (Exception e)
        {
            Log.Error("app", "Couldn't create the integrations", e);
            return;
        }
        foreach (var integration in all)
        {
            try
            {
                integration.Start(_hub, host);
                _integrations.Add(integration);
                Log.Info("app", $"Started {integration.DisplayName}");
            }
            catch (Exception e)
            {
                Log.Error("app", $"{integration.Id} failed to start", e);
                try { integration.Dispose(); } catch (Exception) { }
            }
        }
#else
        _ = host;
        Log.Info("app", "Built without integrations");
#endif
    }

    void OpenSettings(SettingsPage page)
    {
        _flyout.HideFlyout();
        _settingsWindow ??= new SettingsWindow(_settingsVm);
        _settingsWindow.ShowPage(page);
    }

    void Quit()
    {
        if (IsExiting) return;
        IsExiting = true;
        Log.Info("app", "Quitting");
        foreach (var integration in _integrations)
        {
            try { integration.Dispose(); }
            catch (Exception e) { Log.Warn("app", $"{integration.Id} dispose: {e.Message}"); }
        }
        foreach (var d in Enumerable.Reverse(_disposables))
        {
            try { d.Dispose(); }
            catch (Exception e) { Log.Warn("app", $"dispose: {e.Message}"); }
        }
        _hub.Dispose();
        _settingsWindow?.Close();
        _instance?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Quit();
        base.OnSessionEnding(e);
    }
}
