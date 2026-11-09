using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gyrodial.Core;
using Gyrodial.Integrations.Common;

namespace Gyrodial.Integrations.Teams;

/// <summary>
/// Microsoft Teams' local "third-party app API" (new Teams: Settings → Privacy → Manage API → Enable
/// API). Teams listens on ws://127.0.0.1:8124 and sends meetingUpdate messages; we send actions such
/// as toggle-mute. The first action from an unpaired app makes Teams show an "Allow" prompt in the
/// meeting, after which it sends {"tokenRefresh": "…"}; we keep that token and connect with it.
/// </summary>
public sealed class TeamsIntegration : IntegrationBase
{
    const string Endpoint = "ws://127.0.0.1:8124";
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    readonly object _gate = new();
    CancellationTokenSource? _run;
    ClientWebSocket? _socket;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    int _requestId;
    MeetingState _meeting = MeetingState.None;
    bool _callShown;

    public override string Id => "teams";
    public override string DisplayName => "Microsoft Teams";

    public override IReadOnlyList<IntegrationAction> Actions =>
    [
        new("Reset Teams pairing", () =>
        {
            Host.Settings.Update(s => s.TeamsToken = null);
            Reconnect();
            return Task.FromResult<string?>("Done. Next time you press mute on the dial in a meeting, click Allow in Teams.");
        }),
    ];

    protected override bool IsEnabled(GyrodialSettings s) => s.TeamsEnabled;

    protected override void Subscribe()
    {
        On(Protocol.T.Call, m =>
        {
            var action = m.GetString("a") switch
            {
                "mute" => "toggle-mute",
                "video" => "toggle-video",
                "leave" => "leave-call",
                _ => null,
            };
            if (action is not null) _ = SendActionAsync(action);
        });
        OnConnection(connected =>
        {
            if (!connected) return;
            lock (_gate)
            {
                // A quick reconnect may find the dial still on the call face: say "off" too
                _callShown = true;
                PushToDial();
            }
        });
    }

    protected override void Enable()
    {
        var run = new CancellationTokenSource();
        _run = run;
        SetStatus("Looking for Teams…");
        _ = Task.Run(() => RunAsync(run.Token));
    }

    protected override void Disable()
    {
        _run?.Cancel();
        _run = null;
        lock (_gate)
        {
            _meeting = MeetingState.None;
            PushToDial();
        }
    }

    void Reconnect()
    {
        // closing the socket makes the loop connect again, with the current token
        try { _socket?.Abort(); } catch (ObjectDisposedException) { }
    }

    async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(BuildUri(Token()), cancel).ConfigureAwait(false);
                _socket = socket;
                SetStatus(string.IsNullOrEmpty(Settings.TeamsToken)
                    ? "Connected. In a meeting, press mute on the dial once, then click Allow in Teams"
                    : "Connected");
                Log("Connected to Teams");
                await ReceiveLoopAsync(socket, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException)
            {
                SetStatus("Teams isn't running, or its third-party app API is off (Teams → Settings → Privacy → Manage API)");
            }
            catch (Exception e)
            {
                // Anything else (a bad message, a Teams update): keep trying rather than stop for good
                Log($"Teams connection failed: {e.GetType().Name}: {e.Message}");
                SetStatus("Couldn't talk to Teams. Retrying…");
            }
            finally
            {
                _socket = null;
                lock (_gate)
                {
                    _meeting = MeetingState.None;
                    PushToDial();
                }
            }
            try
            {
                await Task.Delay(RetryDelay, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>The pairing token. Stored encrypted (DPAPI); a plain one from an older build is
    /// encrypted the first time it's read.</summary>
    string? Token()
    {
        var stored = Settings.TeamsToken;
        if (string.IsNullOrEmpty(stored)) return null;
        if (Secrets.Unprotect(stored) is { Length: > 0 } token) return token;
        Host.Settings.Update(s => s.TeamsToken = Secrets.Protect(stored));
        return stored;
    }

    static Uri BuildUri(string? token)
    {
        var q = new StringBuilder();
        q.Append("?token=").Append(Uri.EscapeDataString(token ?? ""));
        q.Append("&protocol-version=2.0.0");
        q.Append("&manufacturer=Gyrodial");
        q.Append("&device=").Append(Uri.EscapeDataString(Environment.MachineName));
        q.Append("&app=Gyrodial");
        q.Append("&app-version=").Append(Uri.EscapeDataString(typeof(TeamsIntegration).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"));
        return new Uri(Endpoint + q);
    }

    async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try
            {
                if (JsonNode.Parse(text) is JsonObject o) OnTeamsMessage(o);
            }
            catch (JsonException) { }
        }
    }

    void OnTeamsMessage(JsonObject o)
    {
        if (o["tokenRefresh"] is JsonValue tv && tv.TryGetValue(out string? token) && !string.IsNullOrEmpty(token))
        {
            Host.Settings.Update(s => s.TeamsToken = Secrets.Protect(token));
            Log("Paired with Teams");
        }
        if (o["meetingUpdate"] is JsonObject update)
        {
            var state = update["meetingState"] as JsonObject;
            var permissions = update["meetingPermissions"] as JsonObject;
            lock (_gate)
            {
                _meeting = _meeting with
                {
                    InMeeting = Bool(state?["isInMeeting"]) ?? _meeting.InMeeting,
                    Muted = Bool(state?["isMuted"]) ?? _meeting.Muted,
                    Video = Bool(state?["isVideoOn"]) ?? _meeting.Video,
                    CanPair = Bool(permissions?["canPair"]) ?? _meeting.CanPair,
                };
                PushToDial();
            }
            SetStatus(_meeting.InMeeting
                ? (_meeting.CanPair && string.IsNullOrEmpty(Settings.TeamsToken)
                    ? "In a meeting: press mute on the dial, then click Allow in Teams"
                    : "Connected · In a meeting")
                : "Connected");
        }
    }

    /// <summary>Sends the meeting state to the dial (call with the gate held).</summary>
    void PushToDial()
    {
        if (_meeting.InMeeting)
        {
            Send(Protocol.T.Call, new { on = true, muted = _meeting.Muted, video = _meeting.Video, app = "Teams" });
            _callShown = true;
        }
        else if (_callShown)
        {
            Send(Protocol.T.Call, new { on = false, muted = false, video = false, app = "Teams" });
            _callShown = false;
        }
    }

    async Task SendActionAsync(string action)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            Log($"Can't {action}: not connected to Teams");
            return;
        }
        var json = new JsonObject
        {
            ["action"] = action,
            ["parameters"] = new JsonObject(),
            ["requestId"] = Interlocked.Increment(ref _requestId),
        }.ToJsonString();
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            Log($"Couldn't {action}: {e.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    sealed record MeetingState(bool InMeeting, bool Muted, bool Video, bool CanPair)
    {
        public static readonly MeetingState None = new(false, false, false, false);
    }
}
