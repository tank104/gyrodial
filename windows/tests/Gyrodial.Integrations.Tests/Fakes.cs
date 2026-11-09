using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Gyrodial.Core;

namespace Gyrodial.Integrations.Tests;

/// <summary>A dial that records what it's sent and can answer.</summary>
sealed class FakeHub : IDialHub
{
    readonly ConcurrentDictionary<int, (string Type, Action<DialMessage> Handler)> _handlers = new();
    int _next;
    bool _connected;

    public BlockingCollection<DialMessage> Sent { get; } = [];

    public FakeHub(bool connected = true) => _connected = connected;

    public bool IsConnected => _connected;
    public DialInfo? Dial => _connected ? new DialInfo("test", "Gyrodial-TEST", "0.1.0", "usb", 80, false, true, "home") : null;
    public string? CurrentFace => _connected ? "home" : null;

    public event EventHandler<DialMessage>? MessageReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler? StatusChanged;

    public void Send(DialMessage message)
    {
        if (_connected) Sent.Add(message);
    }

    public void Send(string type, object? fields = null) => Send(DialMessage.Create(type, fields));

    public IDisposable On(string type, Action<DialMessage> handler)
    {
        var id = Interlocked.Increment(ref _next);
        _handlers[id] = (type, handler);
        return new Off(() => _handlers.TryRemove(id, out _));
    }

    /// <summary>The dial says something.</summary>
    public void Receive(string type, object? fields = null)
    {
        var m = DialMessage.Create(type, fields);
        MessageReceived?.Invoke(this, m);
        foreach (var (t, h) in _handlers.Values)
            if (t == m.Type) h(m);
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        ConnectionChanged?.Invoke(this, connected);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The next message of this type the dial is sent.</summary>
    public DialMessage Next(string type, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !Sent.TryTake(out var m, left))
                throw new TimeoutException($"The dial wasn't sent a '{type}'");
            if (m.Type == type) return m;
        }
    }

    sealed class Off(Action a) : IDisposable
    {
        public void Dispose() => a();
    }
}

sealed class FakeHost : IIntegrationHost, IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gyrodial-tests-" + Guid.NewGuid().ToString("N"));

    public FakeHost(Action<GyrodialSettings>? configure = null)
    {
        Directory.CreateDirectory(_dir);
        Settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        if (configure is not null) Settings.Update(configure);
    }

    public string Dir => _dir;
    public SettingsStore Settings { get; }
    public ConcurrentQueue<string> Logs { get; } = new();

    public void Log(string source, string message) => Logs.Enqueue($"{source}: {message}");
    public void ShowNotification(string title, string message) => Logs.Enqueue($"notification: {title}: {message}");
    public void RunOnUi(Action action) => action();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

static class Net
{
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Waits until something answers on the port (the integrations start their servers in the background).</summary>
    public static async Task WaitForPortAsync(int port, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }
        throw new TimeoutException($"Nothing listening on {port}");
    }
}
