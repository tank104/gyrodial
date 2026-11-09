using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Puck.Core;
using Puck.Integrations.Common;

namespace Puck.Integrations.DialKit;

/// <summary>
/// The local WebSocket that dialkit-watt pages connect to (ws://127.0.0.1:47824/bridge), speaking
/// that package's protocol unchanged:
///   page → us: {"type":"hello","kind":"dialkit"|"game"|…}, {"type":"presence",visible,focused,title},
///              {"type":"state","panels":[…]} (dialkit) or {"type":"state","label","angle"} (game)
///   us → page: {"type":"set"|"setSub"|"action",panel,path,…} (dialkit), {"type":"input",input,value} (game)
/// Only pages served from localhost may connect. The page you're looking at gets the puck.
/// </summary>
public sealed class DialKitIntegration : IntegrationBase
{
    static readonly TimeSpan StickyFocus = TimeSpan.FromMinutes(30);

    readonly object _gate = new();
    readonly List<BridgeClient> _clients = [];
    readonly int _port;
    LoopbackServer? _server;
    BridgeClient? _active;
    string? _selectedKnob;   // Knob.Key of the knob the puck is on
    bool _dialShown, _gameShown;

    public DialKitIntegration(int port = Protocol.DialKitPort)
    {
        _port = port;
    }

    public override string Id => "dialkit";
    public override string DisplayName => "DialKit & games";

    protected override bool IsEnabled(PuckSettings s) => s.DialKitEnabled;

    protected override void Subscribe()
    {
        On(Protocol.T.Dial, OnPuckDial);
        On(Protocol.T.Game, OnPuckGame);
        OnConnection(connected =>
        {
            if (!connected) return;
            lock (_gate)
            {
                // The puck may or may not remember the last page (a quick reconnect finds it still
                // showing): treat both as shown, so it hears "on" again or an explicit "off".
                _dialShown = _gameShown = true;
                PushToPuck();
            }
        });
    }

    protected override void Enable()
    {
        SetStatus("Starting…");
        _ = StartServerAsync();
    }

    protected override void Disable()
    {
        List<BridgeClient> clients;
        lock (_gate)
        {
            clients = [.. _clients];
            _clients.Clear();
            _active = null;
            PushToPuck();
        }
        foreach (var c in clients) c.Abort();
        var server = _server;
        _server = null;
        if (server is not null) _ = server.DisposeAsync().AsTask();
    }

    async Task StartServerAsync()
    {
        try
        {
            var server = await LoopbackServer.StartAsync(_port, app =>
            {
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
                app.Map(Protocol.DialKitPath, (RequestDelegate)AcceptAsync);
            }).ConfigureAwait(false);
            if (!Enabled)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _server = server;
            Log($"Listening on ws://127.0.0.1:{_port}{Protocol.DialKitPath}");
            lock (_gate) UpdateStatus();
        }
        catch (Exception e)
        {
            Log($"Couldn't listen on 127.0.0.1:{_port}: {e.Message}");
            SetStatus($"Port {_port} is in use (is another DialKit bridge running?)");
        }
    }

    /// <summary>Pages on http(s)://localhost or 127.0.0.1 only, so a site on the internet can't drive or read the puck.</summary>
    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;  // not a browser: a local process
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        return uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    async Task AcceptAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!IsAllowedOrigin(ctx.Request.Headers.Origin.ToString()))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var client = new BridgeClient(socket);
        lock (_gate) _clients.Add(client);
        try
        {
            await client.ReceiveLoopAsync(m => OnPageMessage(client, m), ctx.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client);
                Reselect();
            }
        }
    }

    // ---------------------------------------------------------------- pages

    void OnPageMessage(BridgeClient client, JsonObject m)
    {
        var type = Str(m["type"]);
        lock (_gate)
        {
            switch (type)
            {
                case "hello":
                    client.Kind = Str(m["kind"]) ?? "";
                    Log($"A {client.Kind} page connected");
                    break;
                case "presence":
                    client.Visible = m["visible"] is JsonValue v && v.TryGetValue(out bool vis) && vis;
                    var focused = m["focused"] is JsonValue f && f.TryGetValue(out bool foc) && foc;
                    if (focused) client.FocusedAt = DateTimeOffset.UtcNow;
                    client.Focused = focused;
                    client.Title = Str(m["title"]) ?? "";
                    break;
                case "state" when client.Kind == "dialkit":
                    if (m["panels"] is JsonArray panels) client.Knobs.Load(panels);
                    break;
                case "state" when client.Kind == "game":
                    if (m.ContainsKey("label")) client.GameLabel = Str(m["label"]) ?? "";
                    if (m["angle"] is JsonValue a && a.TryGetValue(out double angle)) client.GameAngle = angle;
                    break;
                default:
                    return;
            }
            Reselect();
        }
    }

    /// <summary>Picks the page that gets the puck: visible, and the one most recently focused (a
    /// page keeps the puck while you're in your editor beside it, until another page is focused).</summary>
    void Reselect()
    {
        var now = DateTimeOffset.UtcNow;
        var next = _clients
            .Where(c => c.Kind is "dialkit" or "game" && c.Visible && (c.Focused || now - c.FocusedAt < StickyFocus))
            .OrderByDescending(c => c.Focused)
            .ThenByDescending(c => c.FocusedAt)
            .FirstOrDefault();
        _active = next;
        PushToPuck();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (!Enabled || _server is null) return;
        var a = _active;
        if (a is null)
            SetStatus(_clients.Count == 0 ? "No pages" : $"{_clients.Count} page{(_clients.Count == 1 ? "" : "s")} connected, none in front");
        else if (a.Kind == "game")
            SetStatus($"Game: {(a.GameLabel.Length > 0 ? a.GameLabel : a.Title)}");
        else
            SetStatus($"DialKit: {a.Title}");
    }

    /// <summary>Tells the puck what the active page wants on it (call with the gate held).</summary>
    void PushToPuck()
    {
        var a = _active;
        if (a?.Kind == "dialkit" && a.Knobs.Knobs.Count > 0)
        {
            var knobs = a.Knobs.Knobs;
            var idx = Math.Max(0, knobs.ToList().FindIndex(k => k.Key == _selectedKnob));
            var k = knobs[idx];
            _selectedKnob = k.Key;
            Send(Protocol.T.Dial, new
            {
                on = true,
                // byte budgets: the puck keeps 40 / 48 / 48 bytes (with the terminator)
                panel = TextUtil.Truncate(k.PanelName, 38),
                name = TextUtil.Truncate(k.Label, 46),
                kind = KindName(k.Kind),
                val = a.Knobs.NumberValue(k),
                on2 = k.Kind == KnobKind.Bool && a.Knobs.BoolValue(k),
                txt = TextUtil.Truncate(a.Knobs.DisplayText(k), 46),
                min = k.Min,
                max = k.Max,
                step = k.Step,
                idx,
                cnt = knobs.Count,
            });
            _dialShown = true;
        }
        else if (_dialShown)
        {
            Send(Protocol.T.Dial, new { on = false });
            _dialShown = false;
        }

        if (a?.Kind == "game")
        {
            Send(Protocol.T.Game, new { on = true, label = TextUtil.Truncate(a.GameLabel, 30), angle = a.GameAngle });
            _gameShown = true;
        }
        else if (_gameShown)
        {
            Send(Protocol.T.Game, new { on = false });
            _gameShown = false;
        }
    }

    static string KindName(KnobKind k) => k switch
    {
        KnobKind.Num => "num",
        KnobKind.Bool => "bool",
        KnobKind.Action => "action",
        KnobKind.Select => "select",
        KnobKind.Color => "color",
        _ => "text",
    };

    // ---------------------------------------------------------------- the puck

    void OnPuckDial(PuckMessage m)
    {
        BridgeClient? target;
        PageCommand? command = null;
        lock (_gate)
        {
            target = _active;
            if (target?.Kind != "dialkit") return;
            var knobs = target.Knobs.Knobs;
            if (knobs.Count == 0) return;
            var idx = Math.Max(0, knobs.ToList().FindIndex(k => k.Key == _selectedKnob));
            switch (m.GetString("a"))
            {
                case "turn":
                    command = target.Knobs.Turn(knobs[idx], m.GetInt("d", 0));
                    break;
                case "next":
                    _selectedKnob = knobs[(idx + 1) % knobs.Count].Key;
                    break;
                case "prev":
                    _selectedKnob = knobs[(idx - 1 + knobs.Count) % knobs.Count].Key;
                    break;
                case "tap":
                    command = target.Knobs.Tap(knobs[idx]);
                    break;
                default:
                    return;
            }
            PushToPuck();
        }
        if (command is not null) target.Send(command.Message);
    }

    void OnPuckGame(PuckMessage m)
    {
        BridgeClient? target;
        lock (_gate) target = _active;
        if (target?.Kind != "game") return;
        var input = m.GetString("in");
        var message = new JsonObject { ["type"] = "input", ["input"] = input };
        switch (input)
        {
            case "turn":
                message["value"] = m.GetDouble("v", 0);
                break;
            case "swipe":
                message["value"] = m.GetString("v") ?? "";
                break;
            case "tap" or "hold" or "press":
                break;
            default:
                return;
        }
        target.Send(message);
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>One connected page.</summary>
    sealed class BridgeClient(WebSocket socket)
    {
        readonly SemaphoreSlim _sendLock = new(1, 1);

        public string Kind { get; set; } = "";
        public bool Visible { get; set; }
        public bool Focused { get; set; }
        public DateTimeOffset FocusedAt { get; set; } = DateTimeOffset.MinValue;
        public string Title { get; set; } = "";
        public DialKitKnobs Knobs { get; } = new();
        public string GameLabel { get; set; } = "";
        public double GameAngle { get; set; }

        public async Task ReceiveLoopAsync(Action<JsonObject> onMessage, CancellationToken cancel)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException)
                {
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 4 * 1024 * 1024) return;  // not a DialKit state
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    try
                    {
                        if (JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)) is JsonObject o)
                            onMessage(o);
                    }
                    catch (JsonException) { }
                }
                message.SetLength(0);
            }
        }

        public void Send(JsonObject message) => _ = SendAsync(message);

        async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Abort()
        {
            try { socket.Abort(); } catch (ObjectDisposedException) { }
        }
    }
}
