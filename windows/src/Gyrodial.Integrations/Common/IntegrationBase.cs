using Gyrodial.Core;

namespace Gyrodial.Integrations.Common;

/// <summary>
/// The plumbing every integration shares: status, logging, dial subscriptions, and switching itself
/// on and off as its setting changes. Subclasses implement <see cref="Enable"/> / <see cref="Disable"/>.
/// </summary>
public abstract class IntegrationBase : IDialIntegration
{
    readonly List<IDisposable> _subscriptions = [];
    readonly object _gate = new();
    string _status = "Starting…";
    // Written under _gate, read without it: subclasses check Enabled while holding their own locks,
    // and Enable/Disable run under _gate and take those locks, so reading it under _gate deadlocked.
    volatile bool _enabled;
    bool _disposed;

    protected IDialHub Hub { get; private set; } = null!;
    protected IIntegrationHost Host { get; private set; } = null!;
    protected GyrodialSettings Settings => Host.Settings.Current;

    /// <summary>True between Enable and Disable.</summary>
    protected bool Enabled => _enabled;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public string Status => _status;
    public event EventHandler? StatusChanged;
    public virtual IReadOnlyList<IntegrationAction> Actions => [];

    /// <summary>Whether the user has this integration switched on.</summary>
    protected abstract bool IsEnabled(GyrodialSettings settings);

    /// <summary>Start listeners. Called with the gate held, so keep it quick (start tasks, don't await them).</summary>
    protected abstract void Enable();

    /// <summary>Stop listeners and drop any state shown on the dial.</summary>
    protected abstract void Disable();

    /// <summary>Called on every settings change while enabled (e.g. a port changed).</summary>
    protected virtual void OnSettingsChanged() { }

    /// <summary>Subscribe to dial messages here; handlers run on background threads.</summary>
    protected virtual void Subscribe() { }

    public void Start(IDialHub hub, IIntegrationHost host)
    {
        Hub = hub;
        Host = host;
        try
        {
            Subscribe();
            host.Settings.Changed += OnSettingsStoreChanged;
            ApplyEnabled();
        }
        catch (Exception e)
        {
            Log($"Couldn't start: {e.Message}");
            SetStatus("Error: " + e.Message);
        }
    }

    void OnSettingsStoreChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!ApplyEnabled() && Enabled) OnSettingsChanged();
        }
        catch (Exception ex)
        {
            Log($"Settings change failed: {ex.Message}");
        }
    }

    /// <summary>Enables or disables to match the setting. True if that changed anything.</summary>
    bool ApplyEnabled()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            var want = IsEnabled(Settings);
            if (want == _enabled) return false;
            _enabled = want;
            if (want)
            {
                Enable();
            }
            else
            {
                Disable();
                SetStatus("Off");
            }
            return true;
        }
    }

    /// <summary>Calls <paramref name="handler"/> for dial messages of this type while enabled.</summary>
    protected void On(string type, Action<DialMessage> handler)
    {
        _subscriptions.Add(Hub.On(type, m =>
        {
            if (!Enabled) return;
            try
            {
                handler(m);
            }
            catch (Exception e)
            {
                Log($"Handling '{type}' failed: {e.Message}");
            }
        }));
    }

    /// <summary>Calls <paramref name="handler"/> with the dial's connection state while enabled.</summary>
    protected void OnConnection(Action<bool> handler)
    {
        void Handler(object? s, bool connected)
        {
            if (!Enabled) return;
            try
            {
                handler(connected);
            }
            catch (Exception e)
            {
                Log($"Connection change failed: {e.Message}");
            }
        }
        Hub.ConnectionChanged += Handler;
        _subscriptions.Add(new Unsubscriber(() => Hub.ConnectionChanged -= Handler));
    }

    protected void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    protected void Log(string message) => Host?.Log(DisplayName, message);

    /// <summary>Sends to the dial if it's connected.</summary>
    protected void Send(string type, object? fields = null)
    {
        if (Hub.IsConnected) Hub.Send(type, fields);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_enabled)
            {
                _enabled = false;
                try { Disable(); } catch (Exception e) { Log($"Stopping failed: {e.Message}"); }
            }
        }
        if (Host is not null) Host.Settings.Changed -= OnSettingsStoreChanged;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        GC.SuppressFinalize(this);
    }

    sealed class Unsubscriber(Action action) : IDisposable
    {
        Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
