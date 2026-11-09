using Gyrodial.Core;
using Windows.Media.Control;
using static Gyrodial.App.Services.NativeMethods;

namespace Gyrodial.App.Services;

public sealed record NowPlaying(string Title, string Artist, string Source, bool Playing)
{
    public static readonly NowPlaying None = new("", "", "", false);
}

/// <summary>
/// What's playing, from Windows' System Media Transport Controls (the same source as the media
/// flyout over the volume OSD), and play/pause/next/previous for the dial.
///
/// SMTC only sees apps that publish a media session (Spotify, browsers, Media Player...). When no
/// session exists, or the app ignores the command, the media keys are sent instead, which reach
/// whichever player Windows routes them to.
/// </summary>
public sealed class MediaService : IDisposable
{
    readonly IDialHub _hub;
    readonly AppLog _log = AppLog.Instance;
    readonly List<IDisposable> _subs = [];
    readonly object _gate = new();
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    Timer? _debounce;
    NowPlaying? _lastSent;
    int _readSeq;
    bool _disposed;

    public NowPlaying Current { get; private set; } = NowPlaying.None;
    public event Action<NowPlaying>? Changed;

    public MediaService(IDialHub hub)
    {
        _hub = hub;
        _subs.Add(hub.On(Protocol.T.Media, m => _ = CommandAsync(m.GetString("a", "toggle"))));
        hub.ConnectionChanged += OnConnectionChanged;
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate)
            {
                if (_disposed) return;
                _manager = manager;
            }
            manager.CurrentSessionChanged += OnSessionsChanged;
            manager.SessionsChanged += OnSessionsChanged;
            AttachCurrent();
        }
        catch (Exception e)
        {
            _log.Warn("media", $"Media sessions unavailable: {e.Message}");
        }
    }

    void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected) return;
        lock (_gate) _lastSent = null;
        Refresh();
    }

    void AttachCurrent()
    {
        var current = _manager?.GetCurrentSession();
        lock (_gate)
        {
            if (!ReferenceEquals(current, _session))
            {
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged -= OnSessionChanged;
                    _session.PlaybackInfoChanged -= OnSessionChanged;
                }
                _session = current;
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnSessionChanged;
                    _session.PlaybackInfoChanged += OnSessionChanged;
                }
            }
        }
        Refresh();
    }

    void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Refresh();

    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) => AttachCurrent();

    /// <summary>Players fire several events per track change: coalesce into one read.</summary>
    void Refresh()
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => _ = ReadAsync());
            _debounce.Change(120, Timeout.Infinite);
        }
    }

    async Task ReadAsync()
    {
        GlobalSystemMediaTransportControlsSession? session;
        int seq;
        lock (_gate)
        {
            session = _session;
            seq = ++_readSeq;
        }
        var now = NowPlaying.None;
        if (session is not null)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                now = new NowPlaying(
                    props?.Title ?? "",
                    props?.Artist is { Length: > 0 } a ? a : props?.AlbumArtist ?? "",
                    FriendlyAppName(session.SourceAppUserModelId),
                    playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }
            catch (Exception e)
            {
                // the session can vanish between the event and the read
                _log.Debug("media", $"read: {e.Message}");
            }
        }

        bool send;
        lock (_gate)
        {
            // Two reads can be in flight (a track change fires several events); only the newest counts
            if (seq != _readSeq) return;
            Current = now;
            send = now != _lastSent;
            if (send) _lastSent = now;
        }
        if (!send) return;
        Changed?.Invoke(now);
        // The dial stores these in 96/64/32-byte fields
        _hub.Send(Protocol.T.State, new
        {
            play = now.Playing,
            title = DialMessage.FitUtf8(now.Title, 95),
            artist = DialMessage.FitUtf8(now.Artist, 63),
            src = DialMessage.FitUtf8(now.Source, 31),
        });
    }

    async Task CommandAsync(string action)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;
        bool done = false;
        if (session is not null)
        {
            try
            {
                done = action switch
                {
                    "next" => await session.TrySkipNextAsync(),
                    "prev" => await session.TrySkipPreviousAsync(),
                    _ => await session.TryTogglePlayPauseAsync(),
                };
            }
            catch (Exception e)
            {
                _log.Debug("media", $"{action}: {e.Message}");
            }
        }
        if (!done)
        {
            var vk = action switch { "next" => VK_MEDIA_NEXT_TRACK, "prev" => VK_MEDIA_PREV_TRACK, _ => VK_MEDIA_PLAY_PAUSE };
            Send(Key(vk, up: false), Key(vk, up: true));
        }
        Refresh();
    }

    /// <summary>"Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "MSEdge" → a name to show.</summary>
    public static string FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        try
        {
            // Packaged apps (and many Win32 apps with an AUMID) know their display name
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid);
            if (info?.DisplayInfo?.DisplayName is { Length: > 0 } name) return name;
        }
        catch (Exception) { }

        var id = aumid.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("firefox") || id == "308046b0af4a39cb") return "Firefox";
        if (id.Contains("zunemusic")) return "Media Player";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("teams")) return "Teams";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("applemusic") || id.Contains("apple music")) return "Apple Music";

        var s = aumid;
        int bang = s.IndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        s = s.Split('\\', '/').Last();
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : "";
    }

    public void Dispose()
    {
        _hub.ConnectionChanged -= OnConnectionChanged;
        foreach (var s in _subs) s.Dispose();
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
        {
            _disposed = true;
            _debounce?.Dispose();
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session = null;
            }
            manager = _manager;
            _manager = null;
        }
        if (manager is not null)
        {
            manager.CurrentSessionChanged -= OnSessionsChanged;
            manager.SessionsChanged -= OnSessionsChanged;
        }
    }
}
