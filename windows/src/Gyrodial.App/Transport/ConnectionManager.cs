using System.Reflection;
using System.Threading.Channels;
using Gyrodial.Core;

namespace Gyrodial.App.Transport;

public enum LinkState
{
    /// <summary>No dial in sight.</summary>
    Searching,
    /// <summary>A port is open or a BLE link is coming up, but no handshake yet.</summary>
    Connecting,
    /// <summary>A dial that doesn't know this PC is showing its pairing numbers.</summary>
    Pairing,
    /// <summary>A paired dial is talking.</summary>
    Connected,
}

public enum PairingNoticeKind
{
    /// <summary>Someone pressed the side button on the dial while it showed the numbers.</summary>
    Cancelled,
    /// <summary>The dial won't pair with this PC for a while (it was cancelled there recently).</summary>
    Refused,
    /// <summary>Nobody dialled the numbers in within the dial's 3 minutes.</summary>
    TimedOut,
}

/// <summary>How the last pairing attempt ended, for the flyout. <paramref name="RetryAtUtc"/>: when "Try again" works.</summary>
public sealed record PairingNotice(PairingNoticeKind Kind, string DialName, DateTime RetryAtUtc);

/// <summary>
/// Finds the dial over USB and Bluetooth LE, does the hello/pairing handshake, keeps the link alive,
/// and hands the paired dial's messages to everyone else (it is the app's <see cref="IDialHub"/>).
///
/// All state lives on one loop: transport callbacks, timers and public calls are posted to it, so
/// nothing here needs locks except <see cref="Send(DialMessage)"/>, which only reads the active link.
/// </summary>
public sealed class ConnectionManager : IDialHub, IDisposable
{
    static readonly TimeSpan UsbScanInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan UsbHelloTimeout = TimeSpan.FromSeconds(8);
    static readonly TimeSpan BleHelloTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan TimeResend = TimeSpan.FromMinutes(10);
    static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(2);
    // A hello that arrives longer than this after ours wasn't an answer to it: the dial (re)started,
    // or BLE just subscribed. Firmware that marks those with "new":true is recognised directly.
    static readonly TimeSpan HelloReplyWindow = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan HelloAnswerEvery = TimeSpan.FromSeconds(2);
    static readonly TimeSpan PairStartEvery = TimeSpan.FromSeconds(5);
    // The firmware lets an explicit "pair start" through this long after a cancel on the dial
    static readonly TimeSpan RetryAfterCancel = TimeSpan.FromSeconds(5);

    public static string AppVersion { get; } =
        typeof(ConnectionManager).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    readonly SettingsStore _settings;
    readonly AppLog _log = AppLog.Instance;
    readonly Channel<Action> _inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    readonly BleScanner _scanner = new();
    readonly object _handlersGate = new();
    readonly List<(string Type, Action<DialMessage> Handler)> _handlers = [];

    // ---- loop state
    readonly List<DialLink> _links = [];
    readonly Dictionary<string, DateTime> _portRetryAt = new(StringComparer.OrdinalIgnoreCase);
    // Ports with an ESP32-S3 that never answered hello (another project's firmware): each miss doubles
    // the wait, so Gyrodial doesn't keep grabbing a dev board's port. Cleared when the board is unplugged.
    readonly Dictionary<string, int> _portMisses = new(StringComparer.OrdinalIgnoreCase);
    // Ports dropped on purpose (forgotten, pairing cancelled here): until unplugged or "Pair a dial"
    readonly HashSet<string> _blockedPorts = new(StringComparer.OrdinalIgnoreCase);
    // Espressif ports that never answered hello: another project's board, or one without Gyrodial
    // firmware. Left alone until unplugged (or a firmware install), and offered for "Install Gyrodial".
    readonly HashSet<string> _foreignPorts = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _blockedDialIds = new(StringComparer.OrdinalIgnoreCase); // pairing declined, this session
    readonly HashSet<ulong> _blockedBle = [];
    readonly Dictionary<ulong, (DateTime RetryAt, int Failures)> _bleBackoff = [];
    readonly HashSet<ulong> _bleFailLogged = [];
    ulong? _bleConnecting;
    DialLink? _pairingLink;
    string? _activeDialId;
    DateTime _pairingModeUntil = DateTime.MinValue;
    string? _pairedRaisedFor;
    DateTime _pairedRaisedAt = DateTime.MinValue;
    DateTime _lastUsbScan = DateTime.MinValue;
    DateTime _lastTimeSent = DateTime.MinValue;
    string? _lastCfg;
    int _usbSuspended;
    bool _started;

    // ---- published state
    volatile DialLink? _active;
    volatile DialInfo? _dial;
    volatile string? _face;
    volatile string? _pairingName;
    volatile PairingNotice? _notice;
    volatile string[] _foreignSnapshot = [];
    volatile string? _usbPort;
    int _pairingProgress;
    LinkState _state = LinkState.Searching;

    readonly bool _discovery;

    /// <param name="discovery">false: never scan USB or Bluetooth (links come only from <see cref="AttachLink"/>; the self-test).</param>
    public ConnectionManager(SettingsStore settings, bool discovery = true)
    {
        _settings = settings;
        _discovery = discovery;
        _settings.Changed += (_, _) => Post(OnSettingsChanged);
        _scanner.Seen += s => Post(() => OnSighting(s));
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;
    }

    // ================================================================ IDialHub

    public bool IsConnected => _active is not null;
    public DialInfo? Dial => _dial;
    public string? CurrentFace => _face;

    public event EventHandler<DialMessage>? MessageReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler? StatusChanged;

    public void Send(DialMessage message)
    {
        var link = _active;
        if (link is { IsClosed: false, Paired: true }) link.Send(message.ToJson());
    }

    public void Send(string type, object? fields = null) => Send(DialMessage.Create(type, fields));

    public IDisposable On(string type, Action<DialMessage> handler)
    {
        var entry = (type, handler);
        lock (_handlersGate) _handlers.Add(entry);
        return new Unsubscriber(() => { lock (_handlersGate) _handlers.Remove(entry); });
    }

    // ================================================================ for the app's UI

    public LinkState State => _state;

    /// <summary>The dial showing its pairing numbers ("Gyrodial-1A2B"), while <see cref="State"/> is Pairing.</summary>
    public string? PairingDialName => _pairingName;

    /// <summary>The user clicked Pair and any dial nearby will be taken (for two minutes).</summary>
    public bool PairingWindowOpen => DateTime.UtcNow < _pairingModeUntil;

    /// <summary>How long the pairing window stays open.</summary>
    public TimeSpan PairingWindowRemaining
    {
        get
        {
            var left = _pairingModeUntil - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Numbers dialled in on the dial so far (0..3), while <see cref="State"/> is Pairing.</summary>
    public int PairingProgress => Volatile.Read(ref _pairingProgress);

    /// <summary>How the last pairing attempt ended (cancelled, refused, timed out), until the next one.</summary>
    public PairingNotice? Notice => _notice;

    /// <summary>Espressif boards plugged in that don't answer as a dial ("COM4").</summary>
    public IReadOnlyList<string> ForeignPorts => _foreignSnapshot;

    /// <summary>The COM port of the dial that's connected (or answering) over USB, or null.</summary>
    public string? DialUsbPort => _usbPort;

    /// <summary>Why Bluetooth can't scan, or null.</summary>
    public string? BluetoothProblem => _scanner.Problem;

    /// <summary>A dial finished pairing with this PC (its name).</summary>
    public event Action<string>? Paired;

    public void Start()
    {
        if (_started) return;
        _started = true;
        // First run: no dial paired yet, so the first one that turns up nearby is the one to pair
        if (_settings.Current.Dials.Count == 0) _pairingModeUntil = DateTime.UtcNow + PairingWindow;
        _ = Task.Run(RunLoopAsync);
        _ = Task.Run(TickLoopAsync);
    }

    /// <summary>
    /// The user asked to pair: forget this session's refusals and accept any dial for two minutes.
    /// (Ports that never answered as a dial stay alone: that's a different board, not a refusal.)
    /// </summary>
    public void StartPairing() => Post(() =>
    {
        _blockedDialIds.Clear();
        _blockedBle.Clear();
        _blockedPorts.Clear();
        _bleBackoff.Clear();
        _lastUsbScan = DateTime.MinValue;
        _pairingModeUntil = DateTime.UtcNow + PairingWindow;
        _notice = null;
        foreach (var link in _links.Where(l => !l.IsClosed && l.HelloReceived && !l.Paired))
        {
            link.RefusedUntil = DateTime.MinValue;  // the dial answers "refused" again if it still means it
            SendPairStart(link);
        }
        _log.Info("link", "Pairing: looking for a dial to pair with");
        PublishState();
    });

    /// <summary>
    /// The user cancelled pairing in the flyout. The dial is told (`pair a:cancel`: it leaves the
    /// pairing face without refusing this PC), then the link carrying the pairing is dropped.
    /// </summary>
    public void CancelPairing() => Post(() =>
    {
        _pairingModeUntil = DateTime.MinValue;
        _notice = null;
        foreach (var link in _links.Where(l => !l.IsClosed && l.HelloReceived && !l.Paired && l.PairingShown).ToList())
        {
            if (link is BleLink b) _blockedBle.Add(b.Address);
            link.Send(DialMessage.Create(Protocol.T.Pair, new { a = "cancel" }).ToJson());
            // let the cancel go out before the link is dropped
            _ = Task.Delay(300).ContinueWith(_ => Post(() => Close(link, "pairing cancelled here", blockPort: true)));
        }
        _pairingLink = null;
        _log.Info("link", "Pairing cancelled");
        PublishState();
    });

    /// <summary>Unpairs the connected dial (it forgets this PC; this PC forgets it).</summary>
    public void Forget() => Post(() =>
    {
        var id = _active?.DialId ?? _activeDialId;
        if (id is null) return;
        foreach (var link in _links.Where(l => l.DialId == id).ToList())
        {
            link.Send(DialMessage.Create(Protocol.T.Unpair).ToJson());
            link.Paired = false;
            // let the unpair go out before the link is dropped
            _ = Task.Delay(600).ContinueWith(_ => Post(() => Close(link, "forgotten", blockPort: true)));
        }
        _blockedDialIds.Add(id);
        _settings.Update(s => s.Dials.RemoveAll(p => p.Id == id));
        _log.Info("link", $"Forgot dial {id}");
        SelectActive();
    });

    /// <summary>Releases the USB port (for esptool) until the returned handle is disposed.</summary>
    public IDisposable SuspendUsb()
    {
        Interlocked.Increment(ref _usbSuspended);
        var done = new TaskCompletionSource();
        Post(() =>
        {
            foreach (var link in _links.OfType<SerialLink>().ToList()) Close(link, "released for firmware install");
            done.TrySetResult();
        });
        done.Task.Wait(TimeSpan.FromSeconds(2));
        return new Unsubscriber(() =>
        {
            Interlocked.Decrement(ref _usbSuspended);
            Post(() =>
            {
                // After an install the board may be a dial now: look at every port afresh
                _portRetryAt.Clear();
                _portMisses.Clear();
                _blockedPorts.Clear();
                _foreignPorts.Clear();
                _lastUsbScan = DateTime.MinValue;
            });
        });
    }

    /// <summary>Adopts a link opened elsewhere (the self-test's fake dial) as if discovery had found it.</summary>
    internal void AttachLink(DialLink link) => Post(() =>
    {
        Attach(link);
        SendHello(link);
    });

    // ================================================================ the loop

    void Post(Action action) => _inbox.Writer.TryWrite(action);

    async Task RunLoopAsync()
    {
        await foreach (var action in _inbox.Reader.ReadAllAsync())
        {
            try { action(); }
            catch (Exception e) { _log.Error("link", "loop", e); }
        }
    }

    async Task TickLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Post(Tick);
            try { await Task.Delay(500, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    bool PairingMode => DateTime.UtcNow < _pairingModeUntil;

    void Tick()
    {
        var now = DateTime.UtcNow;
        if (_discovery && Volatile.Read(ref _usbSuspended) == 0 && now - _lastUsbScan >= UsbScanInterval)
        {
            _lastUsbScan = now;
            ScanUsb();
        }

        foreach (var link in _links.ToList())
        {
            if (!link.HelloReceived)
            {
                var timeout = link is SerialLink ? UsbHelloTimeout : BleHelloTimeout;
                if (now - link.OpenedAt > timeout)
                {
                    // Something on that port isn't a dial running our firmware (another project's
                    // board, or a dial stuck in its bootloader). Let go so other tools can use the
                    // port, and leave it alone until it's unplugged: the flyout offers "Install Gyrodial".
                    if (link is SerialLink s)
                    {
                        _portMisses[s.PortName] = _portMisses.GetValueOrDefault(s.PortName) + 1;
                        _foreignPorts.Add(s.PortName);
                        _log.Info("usb", $"{s.PortName} doesn't answer as a dial; leaving it alone until it's unplugged");
                    }
                    if (link is BleLink b) BackOff(b.Address);
                    Close(link, "no answer to hello");
                    continue;
                }
                if (now - link.LastHelloSent >= HelloInterval) SendHello(link);
                continue;
            }

            if (now - link.LastRx > Protocol.LinkTimeout)
            {
                Close(link, "timed out");
                continue;
            }
            // It spoke first (boot, BLE subscribe) and our answer got no reply yet: say hello again
            if (!link.HelloReplied && now - link.LastHelloSent >= HelloInterval) SendHello(link);
            if (link is BleLink declined && !link.Paired && link.DialId is not null && _blockedDialIds.Contains(link.DialId))
            {
                // A declined dial over BLE would hold its only connection: give it back. Over USB the
                // link just stays quiet (the dial is plugged in here, and "Try again" reuses it).
                _blockedBle.Add(declined.Address);
                Close(link, "not pairing with this dial");
                continue;
            }
            if (now - link.LastPingSent >= Protocol.PingInterval)
            {
                link.LastPingSent = now;
                link.Send(DialMessage.Create(Protocol.T.Ping).ToJson());
            }
            // While the user is pairing, keep asking an unpaired dial that hasn't started (busy with
            // another PC, or refusing for a while): hello first, in case it restarted meanwhile.
            if (PairingMode && !link.Paired && !link.PairingShown && now >= link.RefusedUntil
                && now - link.LastPairStartSent >= PairStartEvery
                && (link.DialId is null || !_blockedDialIds.Contains(link.DialId)))
                SendPairStart(link);
        }

        if (_active is not null && now - _lastTimeSent >= TimeResend) SendTime();

        // Scan while there's nothing to talk to, or while pairing a new one. Scanning costs power and
        // makes some adapters' existing connections laggy, so it stops once a dial is connected.
        _scanner.SetActive(_discovery && (_active is null || PairingMode));
        PublishState();
    }

    // ================================================================ USB

    void ScanUsb()
    {
        IReadOnlyList<string> ports;
        try { ports = UsbPorts.FindDialPorts(); }
        catch (Exception e) { _log.Debug("usb", $"scan: {e.Message}"); return; }

        // a port that went away was unplugged: a refusal tied to it no longer holds
        _blockedPorts.RemoveWhere(p => !ports.Contains(p, StringComparer.OrdinalIgnoreCase));
        _foreignPorts.RemoveWhere(p => !ports.Contains(p, StringComparer.OrdinalIgnoreCase));
        foreach (var gone in _portMisses.Keys.Where(p => !ports.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
            _portMisses.Remove(gone);

        foreach (var port in ports)
        {
            if (_links.Any(l => l is SerialLink s && string.Equals(s.PortName, port, StringComparison.OrdinalIgnoreCase))) continue;
            if (_blockedPorts.Contains(port) || _foreignPorts.Contains(port)) continue;
            if (_portRetryAt.TryGetValue(port, out var at) && DateTime.UtcNow < at) continue;
            try
            {
                var link = SerialLink.Open(port);
                _log.Info("usb", $"Opened {port}");
                Attach(link);
                SendHello(link);
            }
            catch (Exception e)
            {
                // Busy (another app, esptool) or just vanished
                _portRetryAt[port] = DateTime.UtcNow.AddSeconds(10);
                _log.Debug("usb", $"{port}: {e.Message}");
            }
        }
    }

    // ================================================================ BLE

    void OnSighting(BleSighting s)
    {
        if (_bleConnecting is not null || _cts.IsCancellationRequested) return;
        if (_links.Any(l => l is BleLink b && b.Address == s.Address)) return;
        if (_blockedBle.Contains(s.Address)) return;
        if (_bleBackoff.TryGetValue(s.Address, out var back) && DateTime.UtcNow < back.RetryAt) return;

        // The same dial is already here on its cable: one link to it is enough
        if (s.Name.Length > 0 && _links.Any(l => l is SerialLink && !l.IsClosed && l.DialName == s.Name)) return;

        var known = _settings.Current.Dials;
        bool knownAddress = known.Any(p => p.BleAddress == s.Address);
        // paired over USB first: we only know its name ("Gyrodial-1A2B" = the end of its BT address)
        bool knownName = !knownAddress && s.Name.Length > 0 && known.Any(p => p.BleAddress == 0 && p.Name == s.Name);
        bool forPairing = !knownAddress && !knownName;

        // Someone else's dial in range is left alone unless the user is pairing right now: our hello
        // would put its pairing numbers up and hold its only BLE connection.
        if (forPairing && !PairingMode) return;
        if (_active is not null && (!PairingMode || !forPairing)) return;

        _bleConnecting = s.Address;
        _ = ConnectBleAsync(s, forPairing);
    }

    async Task ConnectBleAsync(BleSighting s, bool forPairing)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var link = await BleLink.ConnectAsync(s.Address, s.Name, timeout.Token);
            Post(() =>
            {
                _bleConnecting = null;
                if (_cts.IsCancellationRequested) { link.Dispose(); return; }
                link.OpenedForPairing = forPairing;
                _log.Info("ble", $"Connected to {(s.Name.Length > 0 ? s.Name : link.Description)}");
                Attach(link);
                SendHello(link);
            });
        }
        catch (Exception e)
        {
            Post(() =>
            {
                _bleConnecting = null;
                BackOff(s.Address);
                // The first failure per dial is worth seeing in the log file; the retries aren't
                // WinRT errors often come with an empty message: the type and HRESULT are what tell them apart
                var why = $"{(e.Message.Length > 0 ? e.Message : e.GetType().Name)} (0x{e.HResult:X8})";
                if (_bleFailLogged.Add(s.Address))
                    _log.Info("ble", $"Couldn't connect to {(s.Name.Length > 0 ? s.Name : BleLink.FormatAddress(s.Address))}: {why}");
                else
                    _log.Debug("ble", $"connect {BleLink.FormatAddress(s.Address)}: {why}");
            });
        }
    }

    void BackOff(ulong address)
    {
        var failures = _bleBackoff.TryGetValue(address, out var b) ? b.Failures + 1 : 1;
        var delay = TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, failures - 1)));
        _bleBackoff[address] = (DateTime.UtcNow + delay, failures);
    }

    // ================================================================ links

    void Attach(DialLink link)
    {
        _links.Add(link);
        link.LineReceived += (l, line) => Post(() => OnLine(l, line));
        link.Closed += (l, reason) => Post(() => Removed(l, reason));
        PublishState();
    }

    void Close(DialLink link, string reason, bool blockPort = false)
    {
        if (blockPort && link is SerialLink s) _blockedPorts.Add(s.PortName);
        link.Dispose();
        Removed(link, reason);
    }

    void Removed(DialLink link, string reason)
    {
        if (!_links.Remove(link)) return;
        _log.Info(link.Kind, $"{link.Description} closed: {reason}");
        if (link is SerialLink failed && !link.HelloReceived && !_foreignPorts.Contains(failed.PortName))
        {
            // The port failed before anything answered (another project's board resetting, a tool
            // holding it): back off, doubling up to 5 min, instead of grabbing it every 2 s
            var misses = _portMisses.GetValueOrDefault(failed.PortName) + 1;
            _portMisses[failed.PortName] = misses;
            _portRetryAt[failed.PortName] = DateTime.UtcNow.AddSeconds(Math.Min(300, 10 << Math.Min(misses - 1, 5)));
        }
        if (_pairingLink == link)
        {
            // The dial moves its pairing to its other transport when the same PC is there too (BLE
            // dropped, USB still up): keep showing it through that link.
            _pairingLink = link.DialId is null ? null : _links.FirstOrDefault(l =>
                !l.IsClosed && l.DialId == link.DialId && l.HelloReceived && !l.Paired && l.PairingShown);
        }
        if (link is BleLink b)
        {
            // Never reconnect straight away (an expired backoff from earlier would let the next
            // advert in at once): at least 2 s, more if it was already backing off.
            var at = DateTime.UtcNow.AddSeconds(2);
            var cur = _bleBackoff.GetValueOrDefault(b.Address);
            _bleBackoff[b.Address] = (cur.RetryAt > at ? cur.RetryAt : at, cur.Failures);
        }
        SelectActive();
    }

    /// <summary>Asks the dial to (re)start pairing: hello first, in case it restarted and forgot us.</summary>
    void SendPairStart(DialLink link)
    {
        var now = DateTime.UtcNow;
        if (now - link.LastHelloSent > HelloReplyWindow) SendHello(link);
        link.LastPairStartSent = now;
        link.Send(DialMessage.Create(Protocol.T.Pair, new { a = "start" }).ToJson());
    }

    void SendHello(DialLink link)
    {
        link.LastHelloSent = DateTime.UtcNow;
        link.Send(DialMessage.Create(Protocol.T.Hello, new
        {
            v = Protocol.Version,
            app = AppVersion,
            host = Environment.MachineName,
            id = _settings.Current.HostId,
        }).ToJson());
    }

    void OnLine(DialLink link, string line)
    {
        if (link.IsClosed || !_links.Contains(link)) return;
        link.LastRx = DateTime.UtcNow;

        var msg = DialMessage.TryParse(line);
        if (msg is null)
        {
            // boot messages and printf debugging from the firmware
            _log.Debug(link.Kind, line.Length > 300 ? line[..300] : line);
            return;
        }

        switch (msg.Type)
        {
            case Protocol.T.Hello: OnHello(link, msg); break;
            case Protocol.T.Pair: OnPair(link, msg); break;
            case Protocol.T.Bat:
                link.Battery = msg.GetInt("p", link.Battery);
                link.Charging = msg.GetBool("chg", link.Charging);
                link.UsbPowered = msg.GetBool("usb", link.UsbPowered);
                break;
            case Protocol.T.Face:
                link.Face = msg.GetString("f", link.Face);
                break;
        }

        if (link == _active && link.Paired)
        {
            RefreshInfo();
            Dispatch(msg);
        }
    }

    void OnHello(DialLink link, DialMessage msg)
    {
        var now = DateTime.UtcNow;
        bool first = !link.HelloReceived;
        // Not an answer to ours: the dial restarted (its USB port stays open across a reset), or BLE
        // just subscribed. That session doesn't know this PC yet, so its "paired" means nothing:
        // answer, and act on the reply to that.
        // A late hello that agrees we're paired is the dial confirming a pairing it just finished,
        // not a restart (a restarted dial marks its hello "new").
        bool confirmsPairing = link.Paired && msg.GetBool("paired", false);
        bool unprompted = msg.GetBool("new", false) || (now - link.LastHelloSent > HelloReplyWindow && !confirmsPairing);
        bool restarted = unprompted && !first;
        link.HelloReceived = true;
        if (link is SerialLink answered)
        {
            _portMisses.Remove(answered.PortName);
            _foreignPorts.Remove(answered.PortName);
        }
        if (link is BleLink okBle) _bleBackoff.Remove(okBle.Address);
        link.DialId = msg.GetString("id") ?? link.DialId;
        link.DialName = msg.GetString("name") ?? link.DialName;
        link.Firmware = msg.GetString("fw") ?? link.Firmware;
        link.Battery = msg.GetInt("bat", link.Battery);
        link.Charging = msg.GetBool("chg", link.Charging);
        link.UsbPowered = msg.GetBool("usb", link.UsbPowered);
        link.Face = msg.GetString("face", link.Face);

        if (unprompted)
        {
            if (restarted)
            {
                _log.Info(link.Kind, $"{link.DialName ?? link.Description} restarted");
                // whatever it was showing (pairing numbers included) is gone
                link.PairingShown = false;
                link.PairProgress = 0;
                link.HelloReplied = false;
                if (link.Paired) link.NeedsRelink = true;
            }
            else
            {
                _log.Info(link.Kind, $"{link.Description}: {link.DialName} (fw {link.Firmware}) says hello");
            }
            if (now - link.LastHelloAnswered >= HelloAnswerEvery)
            {
                link.LastHelloAnswered = now;
                SendHello(link);
            }
            if (_pairingLink == link && !link.PairingShown) _pairingLink = null;
            PublishState();
            return;
        }

        bool firstReply = !link.HelloReplied;
        link.HelloReplied = true;
        bool paired = msg.GetBool("paired", false);
        var known = link.DialId is null ? null : _settings.Current.Dials.FirstOrDefault(p => p.Id == link.DialId);

        if (firstReply) _log.Info(link.Kind, $"{link.Description}: {link.DialName} (fw {link.Firmware}), {(paired ? "paired" : "not paired")}");

        if (link is BleLink ble && known is not null && known.BleAddress != ble.Address)
            _settings.Update(s => { var k = s.Dials.FirstOrDefault(p => p.Id == known.Id); if (k is not null) k.BleAddress = ble.Address; });

        if (paired)
        {
            link.Paired = true;
            link.PairingShown = false;
            if (known is null) Remember(link);  // it knows us, but our settings were reset
            if (_pairingLink == link) _pairingLink = null;
            if (_notice?.DialName == link.DialName) _notice = null;
            if (link.NeedsRelink)
            {
                // It restarted and lost everything we'd told it: once it's the active link again,
                // say it all again (time, cfg, then every service's state).
                link.NeedsRelink = false;
                if (_active == link) Relink(link);
            }
            SelectActive();
            return;
        }

        link.Paired = false;
        if (link.DialId is not null && _blockedDialIds.Contains(link.DialId))
        {
            if (link is BleLink declined)
            {
                _blockedBle.Add(declined.Address);
                Close(link, "pairing was declined");
                return;
            }
            PublishState();  // USB: stays open and quiet until "Try again"
            return;
        }
        // Found by name over BLE, but it's someone else's dial: leave it alone.
        if (link is BleLink b2 && !b2.OpenedForPairing && known is null)
        {
            _blockedBle.Add(b2.Address);
            Close(link, "not our dial");
            return;
        }
        // The dial starts pairing by itself on our hello and says "pair start"; that's what puts the
        // instructions up. While the user is pairing, ask explicitly too (it may be refusing, or
        // busy with another PC), but not again right after the last ask.
        if (PairingMode && now >= link.RefusedUntil && now - link.LastPairStartSent >= PairStartEvery)
        {
            link.LastPairStartSent = now;
            link.Send(DialMessage.Create(Protocol.T.Pair, new { a = "start" }).ToJson());
        }
        SelectActive();
    }

    /// <summary>The active dial restarted: everything it was told before is gone.</summary>
    void Relink(DialLink link)
    {
        _log.Info("link", $"Sending everything to {link.DialName} again");
        _lastCfg = null;
        SendTime();
        SendCfg(force: true);
        Raise(ConnectionChanged, true);  // services send their state from here
    }

    void OnPair(DialLink link, DialMessage msg)
    {
        var now = DateTime.UtcNow;
        var name = link.DialName ?? "your dial";
        switch (msg.GetString("s"))
        {
            case "start":
                if (link.Paired) break;
                if (!link.PairingShown) _log.Info("link", $"{name} is showing its pairing numbers");
                link.PairingShown = true;
                link.PairProgress = 0;
                link.RefusedUntil = DateTime.MinValue;
                _pairingLink = link;
                _notice = null;
                break;
            case "progress":
                link.PairProgress = Math.Clamp(msg.GetInt("n", 0), 0, 3);
                if (!link.Paired && !link.PairingShown)
                {
                    // a progress before any start (the start got lost): it is pairing all the same
                    link.PairingShown = true;
                    _pairingLink = link;
                }
                break;
            case "ok":
            {
                bool alreadyPaired = link.Paired;
                link.Paired = true;
                link.PairingShown = false;
                if (_pairingLink == link) _pairingLink = null;
                if (link.DialId is not null) _blockedDialIds.Remove(link.DialId);
                Remember(link);
                _activeDialId = link.DialId;  // the one just paired takes over
                _pairingModeUntil = DateTime.MinValue;
                _notice = null;
                // The dial says "ok" on every transport the PC is on (USB and BLE): one notification
                if (!alreadyPaired && (_pairedRaisedFor != link.DialId || now - _pairedRaisedAt > TimeSpan.FromSeconds(30)))
                {
                    _pairedRaisedFor = link.DialId;
                    _pairedRaisedAt = now;
                    _log.Info("link", $"Paired with {name}");
                    Paired?.Invoke(name);
                }
                break;
            }
            case "fail":
                if (_pairingLink == link) _pairingLink = null;
                link.PairingShown = false;
                if (link.DialId is not null) _blockedDialIds.Add(link.DialId);
                _pairingModeUntil = DateTime.MinValue;
                _notice = new PairingNotice(PairingNoticeKind.Cancelled, name, now + RetryAfterCancel);
                _log.Info("link", $"Pairing with {name} was cancelled on the dial");
                if (link is BleLink b)
                {
                    // BLE: give the dial its only connection back
                    _blockedBle.Add(b.Address);
                    Close(link, "pairing cancelled on the dial");
                    return;
                }
                break;  // USB: the link stays (quietly) for "Try again"
            case "refused":
            {
                int retry = Math.Clamp(msg.GetInt("retry", 60), 1, 600);
                link.RefusedUntil = now.AddSeconds(retry);
                link.PairingShown = false;
                if (_pairingLink == link) _pairingLink = null;
                _notice = new PairingNotice(PairingNoticeKind.Refused, name, link.RefusedUntil);
                _log.Info("link", $"{name} won't pair with this PC for {retry} s (cancelled there earlier)");
                break;
            }
            case "timeout":
                link.PairingShown = false;
                if (_pairingLink == link) _pairingLink = null;
                // Nobody was there: don't put the numbers straight back up
                _pairingModeUntil = DateTime.MinValue;
                _notice = new PairingNotice(PairingNoticeKind.TimedOut, name, now);
                _log.Info("link", $"Pairing with {name} timed out");
                break;
            case "none":
                link.Paired = false;
                break;
        }
        SelectActive();
    }

    void Remember(DialLink link)
    {
        if (link.DialId is null) return;
        _settings.Update(s =>
        {
            var k = s.Dials.FirstOrDefault(p => p.Id == link.DialId);
            if (k is null)
            {
                k = new KnownDial { Id = link.DialId, PairedAt = DateTimeOffset.Now };
                s.Dials.Add(k);
            }
            k.Name = link.DialName ?? k.Name;
            if (link is BleLink b) k.BleAddress = b.Address;
        });
    }

    /// <summary>
    /// Picks the link everything goes through: one dial at a time, and for that dial USB over BLE
    /// (when PreferUsb), dropping the BLE link so the dial isn't sending everything twice.
    /// </summary>
    void SelectActive()
    {
        var paired = _links.Where(l => !l.IsClosed && l.HelloReceived && l.Paired && l.DialId is not null).ToList();
        if (_activeDialId is null || !paired.Any(l => l.DialId == _activeDialId))
            _activeDialId = paired.FirstOrDefault()?.DialId;

        bool preferUsb = _settings.Current.PreferUsb;
        var mine = paired.Where(l => l.DialId == _activeDialId).ToList();
        if (preferUsb && mine.Any(l => l is SerialLink))
            foreach (var ble in mine.OfType<BleLink>().ToList())
            {
                Close(ble, "using USB instead");
                return; // Close → Removed → SelectActive again
            }
        foreach (var other in paired.OfType<BleLink>().Where(l => l.DialId != _activeDialId).ToList())
        {
            Close(other, "another dial is in use");
            return;
        }

        var best = mine.OrderByDescending(l => (l is SerialLink) == preferUsb ? 1 : 0).FirstOrDefault();
        SetActive(best);
    }

    void SetActive(DialLink? link)
    {
        var was = _active;
        if (was == link)
        {
            PublishState();
            return;
        }
        _active = link;
        RefreshInfo();
        if (link is not null)
        {
            _log.Info("link", $"Talking to {link.DialName} over {(link is SerialLink ? "USB" : "Bluetooth")}");
            SendTime();
            SendCfg(force: true);
            Raise(ConnectionChanged, true);  // services send their state from here
        }
        else
        {
            Raise(ConnectionChanged, false);
        }
        PublishState();
    }

    void RefreshInfo()
    {
        var link = _active;
        var info = link is null ? null : new DialInfo(
            link.DialId ?? "", link.DialName ?? "Gyrodial", link.Firmware ?? "?", link.Kind,
            link.Battery, link.Charging, link.UsbPowered, link.Face);
        var face = link?.Face;
        if (info != _dial || face != _face)
        {
            _dial = info;
            _face = face;
            Raise(StatusChanged);
        }
    }

    void PublishState()
    {
        var pairing = _pairingLink is { IsClosed: false, PairingShown: true, Paired: false } ? _pairingLink : null;
        if (_pairingLink is not null && pairing is null) _pairingLink = null;
        var state = _active is not null ? LinkState.Connected
            : pairing is not null ? LinkState.Pairing
            : _links.Count > 0 || _bleConnecting is not null ? LinkState.Connecting
            : LinkState.Searching;
        var name = state == LinkState.Pairing ? pairing?.DialName : null;
        int progress = pairing?.PairProgress ?? 0;

        var foreign = _foreignPorts.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        bool foreignChanged = !foreign.SequenceEqual(_foreignSnapshot, StringComparer.OrdinalIgnoreCase);
        if (foreignChanged) _foreignSnapshot = foreign;

        var usb = (_active as SerialLink)?.PortName
            ?? _links.OfType<SerialLink>().FirstOrDefault(l => !l.IsClosed && l.HelloReceived)?.PortName;

        if (state != _state || name != _pairingName || progress != _pairingProgress || foreignChanged || usb != _usbPort)
        {
            _state = state;
            _pairingName = name;
            Volatile.Write(ref _pairingProgress, progress);
            _usbPort = usb;
            Raise(StatusChanged);
        }
    }

    // ================================================================ what goes to the dial on its own

    void SendTime()
    {
        _lastTimeSent = DateTime.UtcNow;
        Send(Protocol.T.Time, new
        {
            epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            tz = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes),
        });
    }

    void SendCfg(bool force)
    {
        if (_active is null) return;
        var s = _settings.Current;
        var cfg = DialMessage.Create(Protocol.T.Cfg, new
        {
            bright = Math.Clamp(s.Brightness, 10, 100),
            sound = s.Sound,
            dim = Math.Max(0, s.DimAfterSeconds),
            off = Math.Max(0, s.OffAfterSeconds),
            sens = Math.Round(Math.Clamp(s.TurnSensitivity, 0.25, 4.0), 2),
            flip = s.FlipTurn,
        });
        var json = cfg.ToJson();
        if (!force && json == _lastCfg) return;
        _lastCfg = json;
        Send(cfg);
    }

    void OnSettingsChanged()
    {
        SendCfg(force: false);
        SelectActive();  // PreferUsb may have changed
    }

    void OnTimeChanged(object? sender, EventArgs e) => Post(() => { if (_active is not null) SendTime(); });

    // ================================================================ events

    void Dispatch(DialMessage msg)
    {
        Raise(MessageReceived, msg);
        (string Type, Action<DialMessage> Handler)[] handlers;
        lock (_handlersGate) handlers = _handlers.ToArray();
        foreach (var (type, handler) in handlers)
        {
            if (type != msg.Type) continue;
            try { handler(msg); }
            catch (Exception e) { _log.Error("link", $"handler for '{type}' failed", e); }
        }
    }

    void Raise<T>(EventHandler<T>? handler, T arg)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler<T>)d)(this, arg); }
            catch (Exception e) { _log.Error("link", "event handler failed", e); }
        }
    }

    void Raise(EventHandler? handler)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler)d)(this, EventArgs.Empty); }
            catch (Exception e) { _log.Error("link", "event handler failed", e); }
        }
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        _cts.Cancel();
        _scanner.Dispose();
        var done = new TaskCompletionSource();
        Post(() =>
        {
            foreach (var link in _links.ToList()) link.Dispose();
            _links.Clear();
            _active = null;
            done.TrySetResult();
        });
        done.Task.Wait(TimeSpan.FromSeconds(2));
        _inbox.Writer.TryComplete();
    }

    sealed class Unsubscriber(Action dispose) : IDisposable
    {
        Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
