using System.Windows.Threading;
using Puck.App.Services;
using Puck.App.Transport;
using Puck.Core;

namespace Puck.App.UI;

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

    public AppController(ConnectionManager hub, AudioService audio, MediaService media, IEnumerable<IPuckIntegration> integrations,
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
        var puck = _hub.Puck;
        m.NowUtc = DateTime.UtcNow;
        m.State = _hub.State;
        m.PairingName = _hub.PairingPuckName ?? "";
        m.PairingProgress = _hub.PairingProgress;
        m.BluetoothProblem = _hub.BluetoothProblem;
        m.PairingMode = _hub.PairingWindowOpen;
        m.PairingWindowLeft = _hub.PairingWindowRemaining;
        m.Notice = _hub.Notice;
        m.ForeignPort = _hub.ForeignPorts.FirstOrDefault() ?? "";
        foreach (var item in m.Integrations) item.PuckConnected = m.IsConnected;
        if (puck is not null)
        {
            m.PuckName = puck.Name;
            m.PuckId = puck.Id;
            m.Firmware = puck.Firmware;
            m.Transport = puck.Transport;
            m.Face = puck.Face;
            m.Battery = puck.Battery;
            m.Charging = puck.Charging;
            m.UsbPowered = puck.UsbPowered;
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
            LinkState.Connected => $"Puck — {m.ConnectionText}{(m.HasBattery ? $" · {m.BatteryText}" : "")}",
            LinkState.Pairing => $"Puck — Pairing: {m.PairingProgress} of 3 numbers dialled on the puck",
            _ when m.ShowNotice => $"Puck — {m.NoticeTitle}",
            LinkState.Connecting => "Puck — Connecting…",
            _ => m.PairingMode ? "Puck — Looking for a puck to pair" : "Puck — Looking for your puck",
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
