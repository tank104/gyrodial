using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Puck.Core;

namespace Puck.App.Services;

/// <summary>
/// The system volume, both ways: the default output device's level and mute go to the puck as
/// "state", and the puck's "vol"/"mute" set them. Follows the default device when it changes
/// (headphones plugged in, Bluetooth headset connecting).
///
/// Core Audio objects are only touched from thread-pool (MTA) threads; WPF's UI thread is STA and
/// would make every call a cross-apartment hop.
/// </summary>
public sealed class AudioService : IDisposable
{
    static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);

    readonly IPuckHub _hub;
    readonly AppLog _log = AppLog.Instance;
    readonly List<IDisposable> _subs = [];
    readonly object _gate = new();
    readonly DeviceWatcher _watcher;
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    DateTime _lastSetByPuck = DateTime.MinValue;
    Timer? _debounce;
    int _lastSentVol = -1;
    bool? _lastSentMute;
    bool _disposed;

    /// <summary>Volume (0–100) and mute after the puck changed them: the OSD shows it.</summary>
    public event Action<int, bool>? ChangedByPuck;

    /// <summary>Any change, from anywhere (for the flyout).</summary>
    public event Action<int, bool>? Changed;

    public int Volume { get; private set; } = -1;
    public bool Muted { get; private set; }

    public AudioService(IPuckHub hub)
    {
        _hub = hub;
        _watcher = new DeviceWatcher(this);
        // In order, on the hub's loop (an MTA thread-pool thread): 30 turns a second must land in sequence
        _subs.Add(hub.On(Protocol.T.Vol, m => SetVolume(m.GetInt("v", -1))));
        _subs.Add(hub.On(Protocol.T.Mute, m => SetMute(m.GetBool("m", false))));
        hub.ConnectionChanged += OnConnectionChanged;
        Task.Run(Bind);
    }

    void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected) return;
        lock (_gate)
        {
            _lastSentVol = -1;
            _lastSentMute = null;
        }
        Task.Run(() => SendState(force: true));
    }

    /// <summary>(Re)attaches to the current default output device.</summary>
    void Bind()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_enumerator is null)
                {
                    _enumerator = new MMDeviceEnumerator();
                    _enumerator.RegisterEndpointNotificationCallback(_watcher);
                }
                if (_device is not null)
                {
                    _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                    _device.Dispose();
                    _device = null;
                }
                if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    _log.Warn("audio", "No output device");
                    return;
                }
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                _log.Info("audio", $"Output: {_device.FriendlyName}");
            }
            catch (Exception e)
            {
                _log.Error("audio", "Couldn't open the output device", e);
                return;
            }
        }
        SendState(force: true);
    }

    void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        // Core Audio fires this for every step of a drag: coalesce into one update
        lock (_gate)
        {
            _debounce ??= new Timer(_ => SendState(force: false));
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void SendState(bool force)
    {
        int vol;
        bool mute;
        lock (_gate)
        {
            if (_device is null) return;
            try
            {
                vol = (int)Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                mute = _device.AudioEndpointVolume.Mute;
            }
            catch (Exception e)
            {
                _log.Debug("audio", $"read: {e.Message}");
                return;
            }
            Volume = vol;
            Muted = mute;
            // The puck owns the number while it's being turned: don't bounce its own change back.
            if (!force && DateTime.UtcNow - _lastSetByPuck < EchoWindow) return;
            if (!force && vol == _lastSentVol && mute == _lastSentMute) return;
            _lastSentVol = vol;
            _lastSentMute = mute;
        }
        Changed?.Invoke(vol, mute);
        _hub.Send(Protocol.T.State, new { vol, mute });
    }

    void SetVolume(int v)
    {
        if (v < 0) return;
        v = Math.Clamp(v, 0, 100);
        bool mute, unmuted = false;
        lock (_gate)
        {
            if (_device is null) return;
            _lastSetByPuck = DateTime.UtcNow;
            try
            {
                var ep = _device.AudioEndpointVolume;
                ep.MasterVolumeLevelScalar = v / 100f;
                // Turning the volume un-mutes, as the volume keys do (either direction)
                if (ep.Mute && v > 0)
                {
                    ep.Mute = false;
                    unmuted = true;
                }
                mute = ep.Mute;
            }
            catch (Exception e)
            {
                _log.Debug("audio", $"set volume: {e.Message}");
                return;
            }
            Volume = v;
            Muted = mute;
            _lastSentVol = v;
            _lastSentMute = mute;
        }
        ChangedByPuck?.Invoke(v, mute);
        Changed?.Invoke(v, mute);
        // The puck owns the number while turning, but not the mute: it would keep showing "muted"
        if (unmuted) _hub.Send(Protocol.T.State, new { mute = false });
    }

    void SetMute(bool m)
    {
        int vol;
        lock (_gate)
        {
            if (_device is null) return;
            _lastSetByPuck = DateTime.UtcNow;
            try
            {
                _device.AudioEndpointVolume.Mute = m;
                vol = (int)Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            }
            catch (Exception e)
            {
                _log.Debug("audio", $"set mute: {e.Message}");
                return;
            }
            Muted = m;
            _lastSentMute = m;
        }
        ChangedByPuck?.Invoke(vol, m);
        Changed?.Invoke(vol, m);
        // the puck asked for it, but tell it the result so its icon is right
        _hub.Send(Protocol.T.State, new { mute = m });
    }

    public void Dispose()
    {
        _hub.ConnectionChanged -= OnConnectionChanged;
        foreach (var s in _subs) s.Dispose();
        lock (_gate)
        {
            _disposed = true;
            _debounce?.Dispose();
            if (_device is not null)
            {
                try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch (Exception) { }
                _device.Dispose();
            }
            if (_enumerator is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch (Exception) { }
                _enumerator.Dispose();
            }
        }
    }

    /// <summary>Core Audio's device notifications; only the default output changing matters.</summary>
    sealed class DeviceWatcher(AudioService owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) Task.Run(owner.Bind);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
