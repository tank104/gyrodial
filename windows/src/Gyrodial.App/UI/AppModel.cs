using System.Collections.ObjectModel;
using Gyrodial.App.Transport;
using Gyrodial.Core;

namespace Gyrodial.App.UI;

/// <summary>
/// Everything the flyout and Settings show about the dial, on the UI thread. Filled by
/// <see cref="AppController"/> from the live services, or with sample data for screenshots.
/// </summary>
public sealed class AppModel : ObservableObject
{
    LinkState _state = LinkState.Searching;
    string _dialName = "", _dialId = "", _firmware = "", _transport = "", _face = "";
    int _battery = -1, _volume = -1;
    bool _charging, _usbPowered, _muted, _playing;
    string _title = "", _artist = "", _source = "";
    string? _pairingName, _bluetoothProblem;

    public ObservableCollection<IntegrationItem> Integrations { get; } = [];

    // ---- link
    public LinkState State
    {
        get => _state;
        set
        {
            bool wasSearching = IsSearching;
            if (Set(ref _state, value))
            {
                if (IsSearching && !wasSearching) _searchingSince = DateTime.UtcNow;
                RaiseDerived();
            }
        }
    }

    public bool IsConnected => _state == LinkState.Connected;
    public bool IsPairing => _state == LinkState.Pairing;
    public bool IsSearching => _state is LinkState.Searching or LinkState.Connecting;

    // ---- what the flyout's top part shows: exactly one of these
    /// <summary>A pairing attempt ended (cancelled, refused, timed out) and nothing's connected.</summary>
    public bool ShowNotice => IsSearching && _notice is not null;
    /// <summary>Looking (or connecting) with nothing else to say.</summary>
    public bool ShowSearch => IsSearching && _notice is null;
    /// <summary>The searching panel's footer: "Pair a dial", or the open pairing window's countdown.</summary>
    public bool ShowPairButton => ShowSearch && !_pairingMode;
    public bool ShowPairingWindow => ShowSearch && _pairingMode;

    public string PairingName
    {
        get => _pairingName ?? "";
        set { if (Set(ref _pairingName, value)) Raise(nameof(PairingTitle)); }
    }

    public string PairingTitle => string.IsNullOrEmpty(_pairingName) ? "Pair your dial" : $"Pair {_pairingName}";

    public string? BluetoothProblem
    {
        get => _bluetoothProblem;
        set { if (Set(ref _bluetoothProblem, value)) Raise(nameof(SearchingDetail)); }
    }

    bool _pairingMode;

    /// <summary>The user asked to pair a dial, and the app is taking any dial it finds.</summary>
    public bool PairingMode
    {
        get => _pairingMode;
        set
        {
            if (Set(ref _pairingMode, value))
            {
                Raise(nameof(SearchingTitle));
                Raise(nameof(SearchingDetail));
                Raise(nameof(ShowPairButton));
                Raise(nameof(ShowPairingWindow));
            }
        }
    }

    TimeSpan _pairingLeft;

    /// <summary>"1:42": how long the pairing window stays open.</summary>
    public TimeSpan PairingWindowLeft
    {
        get => _pairingLeft;
        set
        {
            var whole = TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
            if (Set(ref _pairingLeft, whole)) Raise(nameof(PairingCountdown));
        }
    }

    public string PairingCountdown => $"{(int)_pairingLeft.TotalMinutes}:{_pairingLeft.Seconds:00}";

    int _pairingProgress;

    /// <summary>Numbers dialled in on the dial so far (0..3).</summary>
    public int PairingProgress
    {
        get => _pairingProgress;
        set
        {
            if (Set(ref _pairingProgress, Math.Clamp(value, 0, 3)))
            {
                Raise(nameof(PairingProgressText));
                Raise(nameof(Step1Done));
                Raise(nameof(Step2Done));
                Raise(nameof(Step3Done));
            }
        }
    }

    public string PairingProgressText => _pairingProgress switch
    {
        0 => "0 of 3 · turn to the first number, then tap",
        3 => "3 of 3 · pairing…",
        var n => $"{n} of 3 · now the next number",
    };

    public bool Step1Done => _pairingProgress >= 1;
    public bool Step2Done => _pairingProgress >= 2;
    public bool Step3Done => _pairingProgress >= 3;

    // ---- how the last pairing ended
    PairingNotice? _notice;
    DateTime _now = DateTime.UtcNow;

    public PairingNotice? Notice
    {
        get => _notice;
        set
        {
            if (Set(ref _notice, value)) RaiseNotice();
        }
    }

    /// <summary>The clock the countdowns read (set every half second by the controller).</summary>
    public DateTime NowUtc
    {
        get => _now;
        set
        {
            _now = value;
            if (_notice is not null) { Raise(nameof(CanRetry)); Raise(nameof(RetryLabel)); Raise(nameof(NoticeDetail)); }
            Raise(nameof(ShowCantFind));
        }
    }

    public string NoticeTitle => _notice?.Kind switch
    {
        PairingNoticeKind.Cancelled => "Pairing cancelled on the dial",
        PairingNoticeKind.Refused => "The dial isn't pairing yet",
        PairingNoticeKind.TimedOut => "Pairing timed out",
        _ => "",
    };

    public string NoticeDetail
    {
        get
        {
            if (_notice is null) return "";
            int wait = RetryWait;
            return _notice.Kind switch
            {
                PairingNoticeKind.Cancelled => $"Someone pressed the side button on {_notice.DialName}. Try again when you're ready.",
                PairingNoticeKind.Refused => wait > 0
                    ? $"Pairing was cancelled on {_notice.DialName} a moment ago. Try again in {wait} s."
                    : $"{_notice.DialName} will pair again now.",
                PairingNoticeKind.TimedOut => $"Nobody turned {_notice.DialName} to its numbers within 3 minutes.",
                _ => "",
            };
        }
    }

    int RetryWait => _notice is null ? 0 : Math.Max(0, (int)Math.Ceiling((_notice.RetryAtUtc - _now).TotalSeconds));
    public bool CanRetry => _notice is not null && RetryWait == 0;
    public string RetryLabel => RetryWait > 0 ? $"Try again in {RetryWait} s" : "Try again";

    void RaiseNotice()
    {
        Raise(nameof(ShowNotice));
        Raise(nameof(ShowSearch));
        Raise(nameof(ShowPairButton));
        Raise(nameof(ShowPairingWindow));
        Raise(nameof(NoticeTitle));
        Raise(nameof(NoticeDetail));
        Raise(nameof(CanRetry));
        Raise(nameof(RetryLabel));
    }

    // ---- a board that doesn't answer as a dial
    string _foreignPort = "";

    /// <summary>"COM4": an ESP32-S3 plugged in that isn't running Gyrodial, or "".</summary>
    public string ForeignPort
    {
        get => _foreignPort;
        set { if (Set(ref _foreignPort, value ?? "")) { Raise(nameof(HasForeignBoard)); Raise(nameof(ForeignText)); } }
    }

    public bool HasForeignBoard => _foreignPort.Length > 0 && IsSearching;
    public string ForeignText => $"Found a board on {_foreignPort} that isn't running Gyrodial.";

    // ---- "Can't find it?" after a while of looking
    DateTime _searchingSince = DateTime.UtcNow;
    public bool ShowCantFind => ShowSearch && !_pairingMode && _now - _searchingSince > TimeSpan.FromSeconds(15);

    public string SearchingTitle => _state == LinkState.Connecting ? "Connecting…"
        : _pairingMode ? "Looking for a dial to pair"
        : "Looking for your dial";

    public string SearchingDetail => _bluetoothProblem is { Length: > 0 } p
        ? $"Plug it in with USB. {p}, so it can't be found nearby."
        : _pairingMode ? "Turn on the dial you want to pair, near this PC, or plug it in with USB."
        : "Plug it in with USB, or turn it on nearby.";

    // ---- the dial
    public string DialName { get => _dialName; set { if (Set(ref _dialName, value)) Raise(nameof(Title)); } }
    public string DialId { get => _dialId; set => Set(ref _dialId, value); }
    public string Firmware { get => _firmware; set => Set(ref _firmware, value); }

    public string Transport
    {
        get => _transport;
        set { if (Set(ref _transport, value)) RaiseDerived(); }
    }

    public string Face
    {
        get => _face;
        set { if (Set(ref _face, value)) Raise(nameof(FaceName)); }
    }

    public string FaceName => FriendlyFace(_face);

    public int Battery
    {
        get => _battery;
        set { if (Set(ref _battery, value)) RaiseDerived(); }
    }

    public bool Charging
    {
        get => _charging;
        set { if (Set(ref _charging, value)) RaiseDerived(); }
    }

    public bool UsbPowered
    {
        get => _usbPowered;
        set { if (Set(ref _usbPowered, value)) RaiseDerived(); }
    }

    public string Title => IsConnected && _dialName.Length > 0 ? _dialName : "Gyrodial";

    public string ConnectionText => _state switch
    {
        LinkState.Connected => _transport == "usb" ? "Connected over USB" : "Connected over Bluetooth",
        LinkState.Pairing => "Waiting to pair",
        LinkState.Connecting => "Connecting…",
        _ => "Not connected",
    };

    public bool HasBattery => _battery >= 0;

    public string BatteryText => _battery < 0
        ? (_usbPowered ? "On USB power" : "")
        : _charging ? $"{_battery} % · Charging"
        : $"{_battery} %";

    public bool BatteryLow => _battery is >= 0 and < 15 && !_charging;

    // ---- sound
    public int Volume { get => _volume; set { if (Set(ref _volume, value)) Raise(nameof(VolumeText)); } }
    public bool Muted { get => _muted; set { if (Set(ref _muted, value)) Raise(nameof(VolumeText)); } }
    public string VolumeText => _volume < 0 ? "" : _muted ? "Muted" : _volume.ToString();

    // ---- now playing
    public string NowTitle { get => _title; set { if (Set(ref _title, value)) Raise(nameof(HasMedia)); } }
    public string NowArtist { get => _artist; set => Set(ref _artist, value); }
    public string NowSource { get => _source; set { if (Set(ref _source, value)) Raise(nameof(NowSourceUpper)); } }
    public string NowSourceUpper => _source.ToUpperInvariant();
    public bool NowPlaying { get => _playing; set { if (Set(ref _playing, value)) Raise(nameof(PlayGlyph)); } }
    public bool HasMedia => _title.Length > 0;
    /// <summary>Segoe Fluent Icons: Pause / Play.</summary>
    public string PlayGlyph => _playing ? "" : "";

    void RaiseDerived()
    {
        Raise(nameof(IsConnected));
        Raise(nameof(IsPairing));
        Raise(nameof(IsSearching));
        Raise(nameof(ShowNotice));
        Raise(nameof(ShowSearch));
        Raise(nameof(ShowPairButton));
        Raise(nameof(ShowPairingWindow));
        Raise(nameof(HasForeignBoard));
        Raise(nameof(ShowCantFind));
        Raise(nameof(SearchingTitle));
        Raise(nameof(ConnectionText));
        Raise(nameof(Title));
        Raise(nameof(HasBattery));
        Raise(nameof(BatteryText));
        Raise(nameof(BatteryLow));
    }

    public static string FriendlyFace(string face) => face switch
    {
        Protocol.Faces.Home => "Home",
        Protocol.Faces.Trackpad => "Trackpad",
        Protocol.Faces.Pointer => "Pointer",
        Protocol.Faces.Claude => "Claude",
        Protocol.Faces.Allow => "Approve",
        Protocol.Faces.DialKit => "DialKit",
        Protocol.Faces.Game => "Game",
        Protocol.Faces.Call => "Call",
        Protocol.Faces.Focus => "Focus",
        Protocol.Faces.Spin => "Spin",
        Protocol.Faces.Dice => "Dice",
        Protocol.Faces.Pong => "Pong",
        Protocol.Faces.Pair => "Pairing",
        Protocol.Faces.Picker => "Faces",
        Protocol.Faces.Offline => "Offline",
        "" => "",
        _ => char.ToUpperInvariant(face[0]) + face[1..],
    };
}

/// <summary>One integration row: its toggle (bound to its setting), status and buttons.</summary>
public sealed class IntegrationItem : ObservableObject
{
    readonly IDialIntegration _integration;
    readonly SettingsStore _settings;
    string _status;
    string _message = "";

    public IntegrationItem(IDialIntegration integration, SettingsStore settings)
    {
        _integration = integration;
        _settings = settings;
        _status = SafeStatus();
        Actions = integration.Actions.Select(a => new ActionItem(a, this)).ToList();
        integration.StatusChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Status = SafeStatus());
        settings.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Raise(nameof(Enabled)); Raise(nameof(ShownStatus)); });
    }

    public string Id => _integration.Id;
    public string DisplayName => _integration.DisplayName;
    public IReadOnlyList<ActionItem> Actions { get; }
    public bool HasActions => Actions.Count > 0;

    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(ShownStatus)); } }

    bool _dialConnected = true;

    /// <summary>Set by the controller: the flyout says "Waiting for dial" while nothing's connected.</summary>
    public bool DialConnected
    {
        get => _dialConnected;
        set { if (Set(ref _dialConnected, value)) Raise(nameof(ShownStatus)); }
    }

    /// <summary>The flyout's line: the integration's status, or that it's waiting for the dial.</summary>
    public string ShownStatus => _dialConnected || !Enabled ? Status : "Waiting for dial";

    /// <summary>The last action's result.</summary>
    public string Message
    {
        get => _message;
        set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); }
    }

    public bool HasMessage => _message.Length > 0;

    /// <summary>Segoe Fluent Icons glyph per integration.</summary>
    public string Glyph => Id switch
    {
        "claude" => "",       // Message
        "claude-code" => "",  // Code
        "dialkit" => "",      // Equalizer
        "teams" => "",        // Microphone
        _ => "",              // AllApps
    };

    public bool HasToggle => Id is "claude" or "claude-code" or "dialkit" or "teams";

    public bool Enabled
    {
        get
        {
            var s = _settings.Current;
            return Id switch
            {
                "claude" => s.ClaudeEnabled,
                "claude-code" => s.ClaudeCodeEnabled,
                "dialkit" => s.DialKitEnabled,
                "teams" => s.TeamsEnabled,
                _ => true,
            };
        }
        set
        {
            if (value == Enabled) return;
            _settings.Update(s =>
            {
                switch (Id)
                {
                    case "claude": s.ClaudeEnabled = value; break;
                    case "claude-code": s.ClaudeCodeEnabled = value; break;
                    case "dialkit": s.DialKitEnabled = value; break;
                    case "teams": s.TeamsEnabled = value; break;
                }
            });
            Raise();
        }
    }

    string SafeStatus()
    {
        try { return _integration.Status; }
        catch (Exception) { return ""; }
    }
}

public sealed class ActionItem
{
    public ActionItem(IntegrationAction action, IntegrationItem owner)
    {
        Label = action.Label;
        Command = new AsyncCommand(async () =>
        {
            owner.Message = "";
            try
            {
                var result = await action.Run();
                owner.Message = result ?? "";
            }
            catch (Exception e)
            {
                owner.Message = e.Message;
                AppLog.Instance.Error(owner.Id, $"{action.Label} failed", e);
            }
        });
    }

    public string Label { get; }
    public AsyncCommand Command { get; }
}
