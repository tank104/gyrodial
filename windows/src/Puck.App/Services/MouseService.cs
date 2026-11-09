using Puck.Core;
using static Puck.App.Services.NativeMethods;

namespace Puck.App.Services;

/// <summary>
/// The puck as a mouse: "mv", "scroll", "click", "btn" become SendInput calls. The puck sends raw
/// motion (touch pixels on the trackpad, degrees of hand rotation as a pointer); speed and
/// acceleration are applied here, so they can be tuned in Settings without new firmware.
///
/// Moves are sent as absolute positions (cursor + delta). A relative SendInput move goes through
/// Windows' own pointer speed and "Enhance pointer precision" curve, which would stack a second
/// acceleration on top of ours and make the puck feel different on every PC. The cost: programs
/// that read raw mouse deltas (games, some 3D viewports) don't see these moves.
///
/// SendInput can't reach elevated windows from a non-elevated app (UIPI): moves and clicks over an
/// admin window are silently dropped by Windows.
/// </summary>
public sealed class MouseService : IDisposable
{
    // The puck flushes coalesced motion every 10 ms; speed is judged per message against that,
    // not against arrival times (Bluetooth delivers in bursts, which would make speed spike).
    const double MessagePeriod = 0.01;
    // A press with no puck input for this long is released: a lost "up" must not hold the
    // button down system-wide.
    static readonly TimeSpan StuckButton = TimeSpan.FromSeconds(5);

    readonly IPuckHub _hub;
    readonly SettingsStore _settings;
    readonly List<IDisposable> _subs = [];
    readonly object _gate = new();
    readonly Timer _watchdog;
    double _fx, _fy;           // sub-pixel remainders
    double _wheel, _hwheel;    // fractional wheel units
    double _speed;             // smoothed, source units per second
    DateTime _lastMove = DateTime.MinValue;
    char _lastSource;
    uint _held;                // MOUSEEVENTF_*UP flags of buttons pressed by "btn down"
    DateTime _lastInput = DateTime.MinValue;

    public MouseService(IPuckHub hub, SettingsStore settings)
    {
        _hub = hub;
        _settings = settings;
        _subs.Add(hub.On(Protocol.T.Move, OnMove));
        _subs.Add(hub.On(Protocol.T.Scroll, OnScroll));
        _subs.Add(hub.On(Protocol.T.Click, OnClick));
        _subs.Add(hub.On(Protocol.T.Btn, OnButton));
        hub.ConnectionChanged += OnConnectionChanged;
        _watchdog = new Timer(_ =>
        {
            lock (_gate)
                if (_held != 0 && DateTime.UtcNow - _lastInput > StuckButton) ReleaseAll("no puck input for 5 s");
        }, null, 1000, 1000);
    }

    void OnConnectionChanged(object? sender, bool connected)
    {
        if (connected) return;
        lock (_gate) ReleaseAll("puck disconnected");
    }

    void ReleaseAll(string why)
    {
        if (_held == 0) return;
        foreach (var up in new[] { MOUSEEVENTF_LEFTUP, MOUSEEVENTF_RIGHTUP, MOUSEEVENTF_MIDDLEUP })
            if ((_held & up) != 0) Send(Mouse(up));
        _held = 0;
        AppLog.Instance.Info("mouse", $"Released a held button ({why})");
    }

    void OnMove(PuckMessage m)
    {
        double x = m.GetDouble("x", 0), y = m.GetDouble("y", 0);
        char src = (m.GetString("s") ?? "t") is { Length: > 0 } s ? s[0] : 't';
        var settings = _settings.Current;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _lastInput = now;
            // A pause or a switch between trackpad and pointer starts a new stroke
            if (src != _lastSource || now - _lastMove > TimeSpan.FromMilliseconds(100))
            {
                _fx = _fy = 0;
                _speed = 0;
            }
            _lastMove = now;
            _lastSource = src;
            double instant = Math.Sqrt(x * x + y * y) / MessagePeriod;
            _speed += 0.5 * (instant - _speed);

            double gain, accel;
            if (src == 'p')
            {
                // degrees of hand rotation → pixels. Slow aiming stays precise, a flick crosses the screen.
                gain = settings.PointerSpeed;
                accel = settings.MouseAcceleration ? Math.Clamp(0.45 + _speed / 90.0, 0.45, 3.0) : 1.0;
            }
            else
            {
                // touch pixels (466 across the glass) → screen pixels
                gain = settings.TrackpadSpeed;
                accel = settings.MouseAcceleration ? Math.Clamp(0.6 + _speed / 700.0, 0.6, 2.6) : 1.0;
            }

            _fx += x * gain * accel;
            _fy += y * gain * accel;
            int dx = (int)Math.Truncate(_fx), dy = (int)Math.Truncate(_fy);
            _fx -= dx;
            _fy -= dy;
            if (dx != 0 || dy != 0) MoveBy(dx, dy);
        }
    }

    /// <summary>Moves the cursor by whole pixels, as an absolute move over the virtual desktop.</summary>
    static void MoveBy(int dx, int dy)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 1 || height <= 1 || !GetCursorPos(out var p))
        {
            Send(Mouse(MOUSEEVENTF_MOVE, dx, dy));  // can't place it: a plain relative move
            return;
        }
        // Windows keeps the cursor on a monitor (and inside any ClipCursor) by itself
        int x = Math.Clamp(p.X + dx, left, left + width - 1);
        int y = Math.Clamp(p.Y + dy, top, top + height - 1);
        // 0..65535 spans the virtual desktop; aim at the pixel's centre
        int nx = (int)Math.Round((x - left + 0.5) * 65536.0 / width);
        int ny = (int)Math.Round((y - top + 0.5) * 65536.0 / height);
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    void OnScroll(PuckMessage m)
    {
        lock (_gate)
        {
            _lastInput = DateTime.UtcNow;
            var k = 120.0 * _settings.Current.ScrollSpeed;
            _wheel += m.GetDouble("y", 0) * k;
            _hwheel += m.GetDouble("x", 0) * k;
            int w = (int)Math.Truncate(_wheel), h = (int)Math.Truncate(_hwheel);
            _wheel -= w;
            _hwheel -= h;
            // Sub-notch wheel deltas scroll smoothly in apps that support high-resolution wheels
            if (w != 0) Send(Mouse(MOUSEEVENTF_WHEEL, data: w));
            if (h != 0) Send(Mouse(MOUSEEVENTF_HWHEEL, data: h));
        }
    }

    void OnClick(PuckMessage m)
    {
        var (down, up) = Buttons(m.GetString("b", "l"));
        int n = Math.Clamp(m.GetInt("n", 1), 1, 3);
        lock (_gate)
        {
            _lastInput = DateTime.UtcNow;
            for (int i = 0; i < n; i++) Send(Mouse(down), Mouse(up));
        }
    }

    void OnButton(PuckMessage m)
    {
        var (down, up) = Buttons(m.GetString("b", "l"));
        bool press = m.GetString("s") == "down";
        lock (_gate)
        {
            _lastInput = DateTime.UtcNow;
            Send(Mouse(press ? down : up));
            if (press) _held |= up;
            else _held &= ~up;
        }
    }

    static (uint Down, uint Up) Buttons(string b) => b switch
    {
        "r" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        "m" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    public void Dispose()
    {
        _hub.ConnectionChanged -= OnConnectionChanged;
        foreach (var s in _subs) s.Dispose();
        _watchdog.Dispose();
        lock (_gate) ReleaseAll("quitting");
    }
}
