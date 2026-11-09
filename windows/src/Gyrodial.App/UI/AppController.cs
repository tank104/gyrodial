using System.Windows.Threading;
using Gyrodial.App.Services;
using Gyrodial.App.Transport;
using Gyrodial.Core;

namespace Gyrodial.App.UI;

/// <summary>Keeps <see cref="AppModel"/> and the tray icon in step with the live services.</summary>
public sealed class AppController : IDisposable
{
    readonly ConnectionManager _hub;
    readonly AudioService _audio;
    readonly MediaService _media;
    readonly TrayIcon _tray;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _poll;

    public AppModel Model { get; } = new();

    public AppController(ConnectionManager hub, AudioService audio, MediaService media, IEnumerable<IDialIntegration> integrations,
        SettingsStore settings, TrayIcon tray, Dispatcher ui)
    {
        _hub = hub;
        _audio = audio;
        _media = media;
        _tray = tray;
        _ui = ui;
        foreach (var i in integrations) Model.Integrations.Add(new IntegrationItem(i, settings));

        hub.StatusChanged += OnHubChanged;
        audio.Changed += OnAudioChanged;
        media.Changed += OnMediaChanged;
        ThemeManager.Changed += Sync;

        // Bluetooth's radio state has no event worth wiring, and the pairing countdowns tick: look
        // twice a second
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Sync(), ui);
        _poll.Start();
        Sync();
    }

    void OnHubChanged(object? sender, EventArgs e) => _ui.BeginInvoke(Sync);
    void OnAudioChanged(int vol, bool mute) => _ui.BeginInvoke(() => { Model.Volume = vol; Model.Muted = mute; });
    void OnMediaChanged(NowPlaying now) => _ui.BeginInvoke(() => ApplyMedia(now));

    void ApplyMedia(NowPlaying now)
    {
        Model.NowTitle = now.Title;
        Model.NowArtist = now.Artist;
        Model.NowSource = now.Source;
        Model.NowPlaying = now.Playing;
    }

    void Sync()
    {
        var m = Model;
        var dial = _hub.Dial;
        m.NowUtc = DateTime.UtcNow;
        m.State = _hub.State;
        m.PairingName = _hub.PairingDialName ?? "";
        m.PairingProgress = _hub.PairingProgress;
        m.BluetoothProblem = _hub.BluetoothProblem;
        m.PairingMode = _hub.PairingWindowOpen;
        m.PairingWindowLeft = _hub.PairingWindowRemaining;
        m.Notice = _hub.Notice;
        m.ForeignPort = _hub.ForeignPorts.FirstOrDefault() ?? "";
        foreach (var item in m.Integrations) item.DialConnected = m.IsConnected;
        if (dial is not null)
        {
            m.DialName = dial.Name;
            m.DialId = dial.Id;
            m.Firmware = dial.Firmware;
            m.Transport = dial.Transport;
            m.Face = dial.Face;
            m.Battery = dial.Battery;
            m.Charging = dial.Charging;
            m.UsbPowered = dial.UsbPowered;
        }
        else
        {
            m.Battery = -1;
            m.Charging = m.UsbPowered = false;
            m.Face = "";
        }
        if (_audio.Volume >= 0) { m.Volume = _audio.Volume; m.Muted = _audio.Muted; }
        ApplyMedia(_media.Current);

        var tooltip = m.State switch
        {
            LinkState.Connected => $"Gyrodial — {m.ConnectionText}{(m.HasBattery ? $" · {m.BatteryText}" : "")}",
            LinkState.Pairing => $"Gyrodial — Pairing: {m.PairingProgress} of 3 numbers dialled on the dial",
            _ when m.ShowNotice => $"Gyrodial — {m.NoticeTitle}",
            LinkState.Connecting => "Gyrodial — Connecting…",
            _ => m.PairingMode ? "Gyrodial — Looking for a dial to pair" : "Gyrodial — Looking for your dial",
        };
        _tray.Update(m.IsConnected, m.BatteryLow, tooltip);
    }

    public void Dispose()
    {
        _poll.Stop();
        _hub.StatusChanged -= OnHubChanged;
        _audio.Changed -= OnAudioChanged;
        _media.Changed -= OnMediaChanged;
        ThemeManager.Changed -= Sync;
    }
}
