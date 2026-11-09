using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Puck.App.Firmware;
using Puck.App.Transport;
using Puck.Core;

namespace Puck.App.UI;

public sealed record Choice(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings → every page. Each property reads and writes <see cref="SettingsStore"/> directly.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    readonly SettingsStore _store;
    readonly ConnectionManager? _hub;
    string _apiKeyStatus = "";
    bool _updating;

    public SettingsViewModel(SettingsStore store, AppModel app, ConnectionManager? hub)
    {
        _store = store;
        _hub = hub;
        App = app;
        Firmware = new FirmwareViewModel(hub);
        Log = new LogViewModel();
        ForgetCommand = new RelayCommand(ConfirmForget, () => App.IsConnected);
        PairCommand = new RelayCommand(() => _hub?.StartPairing());
        _store.Changed += (_, _) =>
        {
            // Our own edits already raised their property: refreshing everything then would snap a
            // slider that's mid-drag back to the value saved a moment ago.
            if (_updating) return;
            Application.Current?.Dispatcher.BeginInvoke(() => { Raise(string.Empty); UpdateApiKeyStatus(); });
        };
        UpdateApiKeyStatus();
    }

    PuckSettings S => _store.Current;
    void Update(Action<PuckSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _updating = true;
        try { _store.Update(change); }
        finally { _updating = false; }
        Raise(name);
    }

    void ConfirmForget()
    {
        var name = App.PuckName.Length > 0 ? App.PuckName : "this puck";
        var owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault(w => w.IsVisible);
        var text = $"Forget {name}?\n\nIt forgets this PC too, and stops turning your volume, mouse and the rest " +
                   "until you pair it again: click \"Pair a puck\", then turn the puck to each number it shows and tap.";
        var answer = owner is null
            ? MessageBox.Show(text, "Puck", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : MessageBox.Show(owner, text, "Puck", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _hub?.Forget();
    }

    public AppModel App { get; }
    public FirmwareViewModel Firmware { get; }
    public LogViewModel Log { get; }
    public ICommand ForgetCommand { get; }
    public ICommand PairCommand { get; }
    public string Version => $"Version {ConnectionManager.AppVersion}";

    public static IReadOnlyList<Choice> DimChoices { get; } =
        [new(0, "Never"), new(10, "After 10 seconds"), new(30, "After 30 seconds"), new(60, "After 1 minute"), new(120, "After 2 minutes"), new(300, "After 5 minutes")];

    public static IReadOnlyList<Choice> OffChoices { get; } =
        [new(0, "Never"), new(60, "After 1 minute"), new(120, "After 2 minutes"), new(300, "After 5 minutes"), new(600, "After 10 minutes"), new(1800, "After 30 minutes")];

    // ---- General
    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            if (value == S.StartWithWindows) return;
            Update(s => s.StartWithWindows = value);
            Services.AutoStart.Apply(value);
        }
    }
    public bool PreferUsb { get => S.PreferUsb; set => Update(s => s.PreferUsb = value); }
    public bool ShowVolumeOverlay { get => S.ShowVolumeOverlay; set => Update(s => s.ShowVolumeOverlay = value); }

    // ---- Puck
    public int Brightness { get => S.Brightness; set => Update(s => s.Brightness = Math.Clamp(value, 10, 100)); }
    public bool Sound { get => S.Sound; set => Update(s => s.Sound = value); }
    public int DimAfterSeconds { get => S.DimAfterSeconds; set => Update(s => s.DimAfterSeconds = value); }
    public int OffAfterSeconds { get => S.OffAfterSeconds; set => Update(s => s.OffAfterSeconds = value); }
    public double TurnSensitivity { get => S.TurnSensitivity; set => Update(s => s.TurnSensitivity = Math.Round(Math.Clamp(value, 0.25, 4), 2)); }
    public bool FlipTurn { get => S.FlipTurn; set => Update(s => s.FlipTurn = value); }

    // ---- Mouse
    public double TrackpadSpeed { get => S.TrackpadSpeed; set => Update(s => s.TrackpadSpeed = Math.Round(value, 2)); }
    public double PointerSpeed { get => S.PointerSpeed; set => Update(s => s.PointerSpeed = Math.Round(value, 1)); }
    public double ScrollSpeed { get => S.ScrollSpeed; set => Update(s => s.ScrollSpeed = Math.Round(value, 2)); }
    public bool MouseAcceleration { get => S.MouseAcceleration; set => Update(s => s.MouseAcceleration = value); }

    // ---- Claude
    public string ClaudeModel { get => S.ClaudeModel; set => Update(s => s.ClaudeModel = value.Trim()); }
    public string ClaudeSystemPrompt { get => S.ClaudeSystemPrompt; set => Update(s => s.ClaudeSystemPrompt = value); }
    public bool SpeakReplies { get => S.SpeakReplies; set => Update(s => s.SpeakReplies = value); }
    public string SpeechLanguage { get => S.SpeechLanguage; set => Update(s => s.SpeechLanguage = value.Trim()); }
    public int ClaudeCodePort { get => S.ClaudeCodePort; set => Update(s => s.ClaudeCodePort = Math.Clamp(value, 1024, 65535)); }
    public int ClaudeCodeTimeoutSeconds { get => S.ClaudeCodeTimeoutSeconds; set => Update(s => s.ClaudeCodeTimeoutSeconds = Math.Clamp(value, 10, 590)); }
    public bool ClaudeCodeNotifications { get => S.ClaudeCodeNotifications; set => Update(s => s.ClaudeCodeNotifications = value); }

    public string ApiKeyStatus { get => _apiKeyStatus; private set => Set(ref _apiKeyStatus, value); }

    public bool HasStoredApiKey => !string.IsNullOrEmpty(S.ClaudeApiKeyProtected);

    /// <summary>Saves (or with an empty string, clears) the API key, encrypted for this Windows user.</summary>
    public void SaveApiKey(string key)
    {
        key = key.Trim();
        _store.Update(s => s.ClaudeApiKeyProtected = key.Length == 0 ? null : Secrets.Protect(key));
        UpdateApiKeyStatus();
    }

    void UpdateApiKeyStatus()
    {
        if (HasStoredApiKey)
            ApiKeyStatus = Secrets.Unprotect(S.ClaudeApiKeyProtected) is { Length: > 0 } k
                ? $"Saved, encrypted for your Windows account (…{k[^4..]})"
                : "Saved key can't be read on this account: enter it again";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            ApiKeyStatus = "Using ANTHROPIC_API_KEY from the environment";
        else
            ApiKeyStatus = "Not set. Get a key at console.anthropic.com";
        Raise(nameof(HasStoredApiKey));
    }
}

/// <summary>A port in the installer's picker: what's on it, as far as the app knows.</summary>
public sealed record PortChoice(string Port, bool IsEspressif, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings → Firmware.</summary>
public sealed class FirmwareViewModel : ObservableObject
{
    // The longest an install may take (download, erase, write, verify) before it's given up and the
    // port handed back to the connection.
    static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(6);

    readonly ConnectionManager? _hub;
    readonly FirmwareBundle? _bundle;
    PortChoice? _port;
    string? _wantedPort;
    bool _eraseFirst, _busy;
    double _progress;
    string _status = "";
    string _logText = "";
    string _portHint = "";
    bool _failed, _succeeded;
    CancellationTokenSource? _cts;

    public FirmwareViewModel(ConnectionManager? hub)
    {
        _hub = hub;
        _bundle = FirmwareBundle.Load();
        InstallCommand = new AsyncCommand(InstallAsync);
        RefreshCommand = new RelayCommand(RefreshPorts);
        CancelCommand = new RelayCommand(Cancel, () => _busy);
        RefreshPorts();
    }

    public ObservableCollection<PortChoice> Ports { get; } = [];
    public AsyncCommand InstallCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }

    public bool HasBundle => _bundle is not null;
    public string BundleText => _bundle is null
        ? "This copy of Puck has no firmware bundled. Build it with build.ps1, or use the web installer."
        : $"Puck firmware {(_bundle.Version.Length > 0 ? _bundle.Version : "")} · {_bundle.Parts.Count} files, {_bundle.TotalBytes / 1024.0 / 1024.0:0.0} MB";

    public PortChoice? SelectedPort
    {
        get => _port;
        set
        {
            if (Set(ref _port, value))
            {
                Raise(nameof(CanInstall));
                PortHint = value is null ? PortHint : "";
            }
        }
    }

    /// <summary>Why no port is chosen yet (several boards plugged in), or "".</summary>
    public string PortHint { get => _portHint; private set { if (Set(ref _portHint, value)) Raise(nameof(HasPortHint)); } }
    public bool HasPortHint => _portHint.Length > 0;

    public bool EraseFirst { get => _eraseFirst; set => Set(ref _eraseFirst, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(CanInstall)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool CanInstall => HasBundle && !_busy && _port is not null;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }
    public bool Succeeded { get => _succeeded; private set => Set(ref _succeeded, value); }

    /// <summary>Preselects a port (the flyout's "Install Puck" for a board that isn't running it).</summary>
    public void SelectPort(string port)
    {
        _wantedPort = port;
        RefreshPorts();
    }

    /// <summary>
    /// Lists the ports and picks one only when it's clear which board is meant: the one asked for,
    /// the connected puck's, or the only ESP32-S3 plugged in. With several ESP32 boards (a puck and
    /// another project), nothing is preselected: flashing the wrong one would wipe it.
    /// </summary>
    void RefreshPorts()
    {
        var keep = _wantedPort ?? _port?.Port;
        _wantedPort = null;
        var puckPort = _hub?.PuckUsbPort;
        var foreign = _hub?.ForeignPorts ?? [];
        var puckName = _hub?.Puck?.Name;
        Ports.Clear();
        foreach (var p in UsbPorts.AllPorts())
        {
            string label = p.Port;
            if (string.Equals(p.Port, puckPort, StringComparison.OrdinalIgnoreCase)) label += $" · {(puckName is { Length: > 0 } n ? n : "Puck")}";
            else if (foreign.Contains(p.Port, StringComparer.OrdinalIgnoreCase)) label += " · ESP32-S3, not running Puck";
            else if (p.IsEspressif) label += " · ESP32-S3 USB";
            Ports.Add(new PortChoice(p.Port, p.IsEspressif, label));
        }

        var espressif = Ports.Where(p => p.IsEspressif).ToList();
        SelectedPort = Ports.FirstOrDefault(p => string.Equals(p.Port, keep, StringComparison.OrdinalIgnoreCase))
            ?? Ports.FirstOrDefault(p => string.Equals(p.Port, puckPort, StringComparison.OrdinalIgnoreCase))
            ?? (espressif.Count == 1 ? espressif[0] : null);
        PortHint = SelectedPort is not null ? ""
            : espressif.Count > 1 ? "Several ESP32 boards are plugged in. Choose the one to install Puck on."
            : Ports.Count == 0 ? "No board found. Plug it in with a USB-C data cable, then Refresh."
            : "Choose the board's port.";
    }

    async Task InstallAsync()
    {
        if (_bundle is null) return;
        var port = SelectedPort;
        RefreshPorts();
        // keep the user's choice if it's still plugged in
        if (port is not null) SelectedPort = Ports.FirstOrDefault(p => p.Port == port.Port);
        port = SelectedPort;
        if (port is null)
        {
            Fail(PortHint.Length > 0 ? PortHint : "No board found. Plug it in with a USB-C data cable.");
            return;
        }

        IsBusy = true;
        Failed = Succeeded = false;
        Progress = 0;
        LogText = "";
        var lines = new List<string>();
        void Log(string line) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            lines.Add(line);
            if (lines.Count > 200) lines.RemoveRange(0, lines.Count - 200);
            LogText = string.Join(Environment.NewLine, lines);
        });

        using var cts = new CancellationTokenSource(InstallTimeout);
        _cts = cts;
        // Given back when this method ends, however it ends (done, failed, cancelled, timed out)
        using var release = _hub?.SuspendUsb();
        try
        {
            Status = "Finding esptool…";
            var esptool = await Esptool.FindAsync(s => Application.Current.Dispatcher.BeginInvoke(() => Status = s), cts.Token);
            if (esptool is null)
            {
                Fail("Couldn't get esptool. Check your internet connection, or put esptool.exe in the tools folder next to Puck.exe.");
                return;
            }
            Log($"Using {esptool.Description}");
            AppLog.Instance.Info("firmware", $"Installing {_bundle.Version} on {port.Port} with {esptool.Description}");

            var progress = new Progress<FlashProgress>(p => { Progress = p.Fraction; Status = p.Status; });
            bool ok = await FirmwareInstaller.InstallAsync(esptool, _bundle, port.Port, EraseFirst, progress, Log, cts.Token);
            if (ok)
            {
                Succeeded = true;
                Progress = 1;
                Status = "Installed. The puck restarts and connects by itself.";
                AppLog.Instance.Info("firmware", "Install finished");
            }
            else
            {
                Fail("The install didn't finish. Hold BOOT while plugging in the puck, then try again.");
            }
        }
        catch (OperationCanceledException)
        {
            Fail(cts.IsCancellationRequested && !_cancelledByUser
                ? $"The install took longer than {InstallTimeout.TotalMinutes:0} minutes and was stopped. Hold BOOT while plugging in the puck, then try again."
                : "Install cancelled. The puck may need it again before it works: press Install when you're ready.");
        }
        catch (Exception e)
        {
            AppLog.Instance.Error("firmware", "Install failed", e);
            Fail($"{e.Message}. Hold BOOT while plugging in the puck, then try again.");
        }
        finally
        {
            _cts = null;
            _cancelledByUser = false;
            IsBusy = false;
        }
    }

    bool _cancelledByUser;

    /// <summary>The Cancel button.</summary>
    public void Cancel()
    {
        _cancelledByUser = true;
        _cts?.Cancel();
    }

    void Fail(string message)
    {
        Failed = true;
        Status = message;
        AppLog.Instance.Warn("firmware", message);
    }
}

/// <summary>Settings → Log: the app's recent log, live.</summary>
public sealed class LogViewModel : ObservableObject
{
    const int Max = 1500;
    bool _attached;

    public ObservableCollection<LogEntry> Entries { get; } = [];
    public ICommand CopyCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public LogViewModel()
    {
        CopyCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, Entries.Select(e => e.ToString()))); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        });
        OpenFolderCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo(AppLog.Instance.Folder) { UseShellExecute = true }); }
            catch (Exception) { }
        });
    }

    /// <summary>Starts following the log (when the page is first shown).</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        foreach (var e in AppLog.Instance.Recent.TakeLast(Max)) Entries.Add(e);
        AppLog.Instance.EntryAdded += e => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Entries.Add(e);
            while (Entries.Count > Max) Entries.RemoveAt(0);
        });
    }
}
