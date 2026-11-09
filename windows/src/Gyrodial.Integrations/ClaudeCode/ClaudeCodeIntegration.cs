using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Gyrodial.Core;
using Gyrodial.Integrations.Common;

namespace Gyrodial.Integrations.ClaudeCode;

/// <summary>
/// Answers Claude Code's permission prompts from the dial. Claude Code runs gyrodial-hook.exe for each
/// PermissionRequest; the hook POSTs the request here and waits; we show it on the dial (one at a
/// time) and reply with the dial's answer. No answer (dial away, timeout, hook killed) means "no
/// decision", and Claude Code falls back to its own prompt in the terminal.
///
/// Only gyrodial-hook.exe gets an answer: requests must carry a MAC made with the shared
/// <see cref="HookKey"/>, and may not come from a browser (Origin / Sec-Fetch-* headers, a Host
/// other than this loopback port, or a non-JSON body are refused).
/// </summary>
public sealed class ClaudeCodeIntegration : IntegrationBase
{
    const int MaxQueued = 16;
    const int MaxRemembered = 8;

    readonly object _gate = new();
    readonly LinkedList<PendingAsk> _queue = new();
    readonly List<string> _endedWhileAway = [];  // asks the dial may still show: ask_end once it's back
    readonly HookInstaller _installer;
    readonly HookKey _hookKey;
    // Ids from an earlier run must never collide: the dial won't reshow the id it last answered
    readonly string _idPrefix = "cc" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant() + "-";
    byte[]? _key;
    PendingAsk? _shown;
    LoopbackServer? _server;
    int _serverPort;
    int _nextId;
    CancellationTokenSource? _lifetime;

    public ClaudeCodeIntegration(HookInstaller? installer = null, HookKey? hookKey = null)
    {
        _installer = installer ?? new HookInstaller();
        _hookKey = hookKey ?? new HookKey();
    }

    public override string Id => "claude-code";
    public override string DisplayName => "Claude Code approvals";

    public override IReadOnlyList<IntegrationAction> Actions =>
    [
        new("Install Claude Code hook", InstallHookAsync),
        new("Remove Claude Code hook", RemoveHookAsync),
        new("Send a test request", TestRequestAsync),
    ];

    /// <summary>The port the server is listening on, or 0.</summary>
    public int ListeningPort => _server?.Port ?? 0;

    /// <summary>The timeout written into the hook entry: the dial's wait plus slack for the round trip.</summary>
    int HookTimeoutSeconds => Math.Max(5, Settings.ClaudeCodeTimeoutSeconds) + 10;

    protected override bool IsEnabled(GyrodialSettings s) => s.ClaudeCodeEnabled;

    protected override void Subscribe()
    {
        On(Protocol.T.Ans, OnAnswer);
        OnConnection(OnConnectionChanged);
    }

    protected override void Enable()
    {
        try
        {
            _key = _hookKey.Ensure();
        }
        catch (Exception e)
        {
            // Without the key no request can be trusted: the server still runs, and refuses them all
            Log($"Couldn't create {_hookKey.FilePath}: {e.Message}");
        }
        _lifetime = new CancellationTokenSource();
        _ = StartServerAsync(Settings.ClaudeCodePort);
    }

    protected override void Disable()
    {
        _lifetime?.Cancel();
        _lifetime = null;
        ResolveAll(null);
        var server = _server;
        _server = null;
        _serverPort = 0;
        if (server is not null) _ = server.DisposeAsync().AsTask();
    }

    protected override void OnSettingsChanged()
    {
        var port = Settings.ClaudeCodePort;
        if (port != _serverPort) _ = RestartServerAsync(port);
        else UpdateStatus();
    }

    async Task RestartServerAsync(int port)
    {
        var old = _server;
        _server = null;
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        await StartServerAsync(port).ConfigureAwait(false);
    }

    async Task StartServerAsync(int port)
    {
        _serverPort = port;
        LoopbackServer server;
        try
        {
            server = await LoopbackServer.StartAsync(port, Map).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log($"Couldn't listen on 127.0.0.1:{port}: {e.Message}");
            SetStatus($"Port {port} is in use. Pick another in Settings.");
            return;
        }
        if (!Enabled || _serverPort != port)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            return;
        }
        _server = server;
        Log($"Listening on 127.0.0.1:{port}");
        UpdateStatus();
    }

    void Map(WebApplication app)
    {
        app.MapGet("/health", (RequestDelegate)(ctx => FromBrowser(ctx) ? Refuse(ctx, StatusCodes.Status403Forbidden) : ctx.Response.WriteAsync("ok")));
        app.MapPost("/permission", (RequestDelegate)HandlePermissionAsync);
        app.MapPost("/notify", (RequestDelegate)HandleNotifyAsync);
    }

    void UpdateStatus()
    {
        if (!Enabled) return;
        string hook;
        try
        {
            hook = _installer.GetState(HookTimeoutSeconds) switch
            {
                HookInstaller.HookState.Installed => "Hook installed",
                HookInstaller.HookState.Stale => "Hook points at an old copy (or at Puck, the old name): install it again",
                HookInstaller.HookState.OldTimeout => "Reinstall the hook to apply the new timeout",
                _ => "Hook not installed",
            };
        }
        catch (Exception e)
        {
            hook = "Couldn't read Claude Code's settings";
            Log($"Reading {_installer.SettingsPath}: {e.Message}");
        }
        if (_key is null) hook += " · no hook key (see the log)";
        SetStatus(_server is null ? hook : $"{hook} · listening on 127.0.0.1:{_server.Port}");
    }

    // ---------------------------------------------------------------- who may ask

    /// <summary>A browser page (or DNS rebinding to it): browsers always send one of these, the hook never does.</summary>
    static bool FromBrowser(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        if (h.ContainsKey("Origin") || h.ContainsKey("Sec-Fetch-Site") || h.ContainsKey("Sec-Fetch-Mode")) return true;
        var host = ctx.Request.Host.Value ?? "";
        var port = ctx.Connection.LocalPort;
        return !host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            && !host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks a hook request; on success returns its nonce, else writes the refusal and returns null.</summary>
    async Task<string?> AuthorizeAsync(HttpContext ctx)
    {
        if (FromBrowser(ctx))
        {
            await Refuse(ctx, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return null;
        }
        if (ctx.Request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(ctx, StatusCodes.Status415UnsupportedMediaType).ConfigureAwait(false);
            return null;
        }
        var nonce = ctx.Request.Headers[HookKey.NonceHeader].ToString();
        var auth = ctx.Request.Headers[HookKey.AuthHeader].ToString();
        var key = _key;
        if (key is null || nonce.Length is < 16 or > 128 || !HookKey.Verify(key, HookKey.RequestMessage(nonce), auth))
        {
            await Refuse(ctx, StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return null;
        }
        return nonce;
    }

    static Task Refuse(HttpContext ctx, int status)
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- the hook's requests

    async Task HandlePermissionAsync(HttpContext ctx)
    {
        if (await AuthorizeAsync(ctx).ConfigureAwait(false) is not { } nonce) return;

        JsonObject? request = null;
        try
        {
            request = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException) { }

        string? decision = null;
        if (request is not null && Enabled && Hub.IsConnected)
        {
            var summary = PermissionSummary.From(request);
            var ok = await AskAsync(summary, TimeSpan.FromSeconds(Math.Max(5, Settings.ClaudeCodeTimeoutSeconds)), ctx.RequestAborted).ConfigureAwait(false);
            decision = ok switch { true => "allow", false => "deny", null => null };
            Log($"{summary.Tool} in {summary.Project}: {decision ?? "no answer from the dial"}");
        }
        var mac = HookKey.Mac(_key!, HookKey.ResponseMessage(nonce, decision));
        await WriteJsonAsync(ctx, new JsonObject { ["decision"] = decision, ["mac"] = mac }).ConfigureAwait(false);
    }

    async Task HandleNotifyAsync(HttpContext ctx)
    {
        if (await AuthorizeAsync(ctx).ConfigureAwait(false) is null) return;
        try
        {
            if (await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false) is JsonObject n
                && Settings.ClaudeCodeNotifications)
            {
                var type = n["notification_type"] is JsonValue tv && tv.TryGetValue(out string? t) ? t : null;
                var message = n["message"] is JsonValue mv && mv.TryGetValue(out string? m) ? m : null;
                // A permission prompt is already on the dial as an ask; the rest become a banner.
                if (type != "permission_prompt" && !string.IsNullOrWhiteSpace(message))
                    Send(Protocol.T.Notify, new { text = TextUtil.Truncate(message, 120), kind = "claude" });
            }
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException) { }
        await WriteJsonAsync(ctx, new JsonObject()).ConfigureAwait(false);
    }

    static Task WriteJsonAsync(HttpContext ctx, JsonObject body)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(body.ToJsonString(), ctx.RequestAborted);
    }

    // ---------------------------------------------------------------- asking the dial

    /// <summary>Queues an ask and waits for the dial's answer: true = allow, false = deny, null = no answer.</summary>
    public async Task<bool?> AskAsync(PermissionSummary summary, TimeSpan timeout, CancellationToken cancel)
    {
        var ask = new PendingAsk(_idPrefix + Interlocked.Increment(ref _nextId), summary);
        lock (_gate)
        {
            // More waiting than anyone would work through on the dial: let the terminal ask
            if (_queue.Count >= MaxQueued) return null;
            _queue.AddLast(ask);
        }
        ShowNext();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel, _lifetime?.Token ?? CancellationToken.None);
        cts.CancelAfter(timeout);
        await using (cts.Token.Register(() => ask.Answer.TrySetResult(null)).ConfigureAwait(false))
        {
            var answer = await ask.Answer.Task.ConfigureAwait(false);
            Finish(ask, answered: answer is not null);
            return answer;
        }
    }

    void ShowNext()
    {
        PendingAsk? next;
        lock (_gate)
        {
            if (_shown is not null || _queue.First is null) return;
            next = _queue.First.Value;
            _shown = next;
        }
        SendAsk(next);
    }

    void SendAsk(PendingAsk ask) =>
        Send(Protocol.T.Ask, new { id = ask.Id, tool = ask.Summary.Tool, text = ask.Summary.Text, proj = ask.Summary.Project });

    void Finish(PendingAsk ask, bool answered)
    {
        bool wasShown;
        lock (_gate)
        {
            _queue.Remove(ask);
            wasShown = _shown == ask;
            if (wasShown) _shown = null;
            // The dial leaves the allow face by itself once it has answered; otherwise tell it to,
            // now or (if it's away, and may come back still showing it) when it returns.
            if (wasShown && !answered && !Hub.IsConnected)
            {
                _endedWhileAway.Add(ask.Id);
                if (_endedWhileAway.Count > MaxRemembered) _endedWhileAway.RemoveAt(0);
            }
        }
        if (wasShown && !answered) Send(Protocol.T.AskEnd, new { id = ask.Id });
        if (wasShown) ShowNext();
    }

    void OnAnswer(DialMessage m)
    {
        var id = m.GetString("id");
        var ok = m.GetBool("ok");
        if (id is null || ok is null) return;
        PendingAsk? ask;
        lock (_gate)
        {
            ask = _queue.FirstOrDefault(a => a.Id == id);
        }
        ask?.Answer.TrySetResult(ok);
    }

    void OnConnectionChanged(bool connected)
    {
        if (!connected)
        {
            // Nobody can answer now: let Claude Code ask in the terminal instead of waiting.
            ResolveAll(null);
            return;
        }
        PendingAsk? shown;
        List<string> ended;
        lock (_gate)
        {
            shown = _shown;
            ended = [.. _endedWhileAway];
            _endedWhileAway.Clear();
        }
        foreach (var id in ended) Send(Protocol.T.AskEnd, new { id });
        if (shown is not null) SendAsk(shown);
    }

    void ResolveAll(bool? answer)
    {
        List<PendingAsk> all;
        lock (_gate) all = [.. _queue];
        foreach (var a in all) a.Answer.TrySetResult(answer);
    }

    // ---------------------------------------------------------------- actions

    Task<string?> InstallHookAsync()
    {
        try
        {
            if (!File.Exists(_installer.HookExePath))
                return Task.FromResult<string?>($"gyrodial-hook.exe isn't next to Gyrodial ({_installer.HookExePath}). Reinstall Gyrodial.");
            var backup = _installer.Install(HookTimeoutSeconds);
            UpdateStatus();
            return Task.FromResult<string?>(
                "Claude Code will now ask the dial before it asks you. Restart open Claude Code sessions to pick it up." +
                (backup is null ? "" : $"\nYour previous settings were saved to {backup}."));
        }
        catch (HookSettingsException e)
        {
            return Task.FromResult<string?>(e.Message);
        }
        catch (Exception e)
        {
            return Task.FromResult<string?>($"Couldn't update {_installer.SettingsPath}: {e.Message}");
        }
    }

    Task<string?> RemoveHookAsync()
    {
        try
        {
            var backup = _installer.Remove();
            UpdateStatus();
            return Task.FromResult<string?>(backup is null ? "The hook wasn't installed." : "Removed. Claude Code will ask in the terminal again.");
        }
        catch (HookSettingsException e)
        {
            return Task.FromResult<string?>(e.Message);
        }
        catch (Exception e)
        {
            return Task.FromResult<string?>($"Couldn't update {_installer.SettingsPath}: {e.Message}");
        }
    }

    async Task<string?> TestRequestAsync()
    {
        if (!Enabled) return "Turn on Claude Code approvals first.";
        if (!Hub.IsConnected) return "Connect the dial first.";
        var answer = await AskAsync(new PermissionSummary("Bash", "echo \"Hello from Gyrodial\"", "test"), TimeSpan.FromSeconds(60), CancellationToken.None);
        return answer switch
        {
            true => "You allowed it on the dial.",
            false => "You denied it on the dial.",
            null => "No answer from the dial.",
        };
    }

    sealed class PendingAsk(string id, PermissionSummary summary)
    {
        public string Id { get; } = id;
        public PermissionSummary Summary { get; } = summary;
        public TaskCompletionSource<bool?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
