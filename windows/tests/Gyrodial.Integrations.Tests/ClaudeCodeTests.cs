using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Gyrodial.Core;
using Gyrodial.Integrations.ClaudeCode;

namespace Gyrodial.Integrations.Tests;

public class ClaudeCodeTests
{
    const string BashRequest = """
        {"session_id":"abc","transcript_path":"C:\\t.jsonl","cwd":"D:\\git\\my-app","permission_mode":"default",
         "hook_event_name":"PermissionRequest","tool_name":"Bash",
         "tool_input":{"command":"npm   run build","description":"Build"}}
        """;

    /// <summary>The integration on a free port, with its own hook key, talking to a fake dial.</summary>
    sealed class Fixture : IDisposable
    {
        public readonly int Port = Net.FreePort();
        public readonly FakeHost Host;
        public readonly FakeHub Hub;
        public readonly HookKey Key;
        public readonly ClaudeCodeIntegration Integration;

        public Fixture(bool connected = true)
        {
            Host = new FakeHost(s => s.ClaudeCodePort = Port);
            Hub = new FakeHub(connected);
            Key = new HookKey(Path.Combine(Host.Dir, "hook.key"));
            Integration = new ClaudeCodeIntegration(new HookInstaller(Path.Combine(Host.Dir, "claude.json"), "gyrodial-hook.exe"), Key);
            Integration.Start(Hub, Host);
        }

        public Task ReadyAsync() => Net.WaitForPortAsync(Port);

        /// <summary>A request as gyrodial-hook.exe makes it.</summary>
        public HttpRequestMessage Signed(string path, string body, out string nonce)
        {
            nonce = Guid.NewGuid().ToString("N");
            var r = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/{path}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            r.Headers.Add(HookKey.NonceHeader, nonce);
            r.Headers.Add(HookKey.AuthHeader, HookKey.Mac(Key.Ensure(), HookKey.RequestMessage(nonce)));
            return r;
        }

        /// <summary>Posts a permission request and returns the decision, checking the answer's MAC.</summary>
        public async Task<string?> DecideAsync(HttpClient http, string body)
        {
            using var request = Signed("permission", body, out var nonce);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var decision = json["decision"]?.GetValue<string>();
            Assert.True(HookKey.Verify(Key.Ensure(), HookKey.ResponseMessage(nonce, decision), json["mac"]?.GetValue<string>()));
            return decision;
        }

        public void Dispose()
        {
            Integration.Dispose();
            Host.Dispose();
        }
    }

    [Fact]
    public void Summarizes_tools()
    {
        var bash = PermissionSummary.From((JsonObject)JsonNode.Parse(BashRequest)!);
        Assert.Equal(new PermissionSummary("Bash", "npm run build", "my-app"), bash);

        var edit = PermissionSummary.From((JsonObject)JsonNode.Parse("""{"tool_name":"Edit","cwd":"D:\\git\\my-app","tool_input":{"file_path":"D:\\git\\my-app\\src\\a.ts"}}""")!);
        Assert.Equal(Path.Combine("src", "a.ts"), edit.Text);

        var mcp = PermissionSummary.From((JsonObject)JsonNode.Parse("""{"tool_name":"mcp__github__create_issue","cwd":"/x/y","tool_input":{"title":"Hi"}}""")!);
        Assert.Equal("github: create_issue", mcp.Tool);
        Assert.Equal("""{"title":"Hi"}""", mcp.Text);
        Assert.Equal("y", mcp.Project);

        var longCommand = PermissionSummary.From((JsonObject)JsonNode.Parse($$$"""{"tool_name":"Bash","tool_input":{"command":"{{{string.Join(' ', Enumerable.Repeat("word", 100))}}}"}}""")!);
        Assert.True(Encoding.UTF8.GetByteCount(longCommand.Text) <= PermissionSummary.MaxText);
        Assert.EndsWith("…", longCommand.Text);

        // limits are bytes: a long tool name and non-ASCII text still fit the dial's buffers
        var wide = PermissionSummary.From((JsonObject)JsonNode.Parse($$$"""{"tool_name":"mcp__{{{new string('g', 60)}}}__create_pull_request","cwd":"C:\\{{{new string('é', 60)}}}","tool_input":{"command":"{{{string.Join(' ', Enumerable.Repeat("“quoted” — ünïcode", 30))}}}"}}""")!);
        Assert.True(Encoding.UTF8.GetByteCount(wide.Tool) <= PermissionSummary.MaxTool);
        Assert.True(Encoding.UTF8.GetByteCount(wide.Project) <= PermissionSummary.MaxProject);
        Assert.True(Encoding.UTF8.GetByteCount(wide.Text) <= PermissionSummary.MaxText);
    }

    [Fact]
    public async Task Asks_the_dial_and_returns_its_answer()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        using var http = new HttpClient();
        var decision = f.DecideAsync(http, BashRequest);

        var ask = f.Hub.Next(Protocol.T.Ask);
        Assert.Equal("Bash", ask.GetString("tool"));
        Assert.Equal("npm run build", ask.GetString("text"));
        Assert.Equal("my-app", ask.GetString("proj"));
        Assert.True(ask.GetString("id")!.Length <= 31);
        f.Hub.Receive(Protocol.T.Ans, new { id = ask.GetString("id"), ok = true });

        Assert.Equal("allow", await decision);
    }

    [Fact]
    public async Task Shows_one_ask_at_a_time()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        using var http = new HttpClient();
        var first = f.DecideAsync(http, BashRequest);
        var ask1 = f.Hub.Next(Protocol.T.Ask);
        var second = f.DecideAsync(http, BashRequest.Replace("npm   run build", "rm -rf dist"));
        await Task.Delay(300);
        Assert.DoesNotContain(f.Hub.Sent, m => m.Type == Protocol.T.Ask);  // the second waits

        f.Hub.Receive(Protocol.T.Ans, new { id = ask1.GetString("id"), ok = false });
        Assert.Equal("deny", await first);

        var ask2 = f.Hub.Next(Protocol.T.Ask);
        Assert.Equal("rm -rf dist", ask2.GetString("text"));
        Assert.NotEqual(ask1.GetString("id"), ask2.GetString("id"));
        f.Hub.Receive(Protocol.T.Ans, new { id = ask2.GetString("id"), ok = true });
        Assert.Equal("allow", await second);
    }

    [Fact]
    public void Ask_ids_differ_between_runs()
    {
        // The dial won't reshow the id it last answered, so a restarted app must not reuse ids
        static string FirstId()
        {
            using var f = new Fixture();
            _ = f.Integration.AskAsync(new PermissionSummary("Bash", "x", "p"), TimeSpan.FromSeconds(5), CancellationToken.None);
            return f.Hub.Next(Protocol.T.Ask).GetString("id")!;
        }
        Assert.NotEqual(FirstId(), FirstId());
    }

    [Fact]
    public async Task No_decision_when_the_dial_is_away_or_leaves()
    {
        using var f = new Fixture(connected: false);
        await f.ReadyAsync();
        using var http = new HttpClient();

        Assert.Null(await f.DecideAsync(http, BashRequest));

        f.Hub.SetConnected(true);
        var pending = f.DecideAsync(http, BashRequest);
        var ask = f.Hub.Next(Protocol.T.Ask);
        f.Hub.SetConnected(false);
        Assert.Null(await pending);

        // back again: the dial may still show the old ask, so it's told to drop it
        f.Hub.SetConnected(true);
        var end = f.Hub.Next(Protocol.T.AskEnd);
        Assert.Equal(ask.GetString("id"), end.GetString("id"));
    }

    [Fact]
    public async Task Caps_the_queue()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        var waiting = Enumerable.Range(0, 16)
            .Select(_ => f.Integration.AskAsync(new PermissionSummary("Bash", "x", "p"), TimeSpan.FromSeconds(10), CancellationToken.None))
            .ToList();
        // the 17th gets "no decision" straight away (the terminal asks instead)
        var extra = f.Integration.AskAsync(new PermissionSummary("Bash", "y", "p"), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(extra.IsCompleted);
        Assert.Null(await extra);
        f.Hub.SetConnected(false);  // resolves the rest
        await Task.WhenAll(waiting);
    }

    [Fact]
    public async Task Refuses_browsers_and_unsigned_requests()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        using var http = new HttpClient();

        async Task<HttpStatusCode> Post(Action<HttpRequestMessage> tweak, string contentType = "application/json")
        {
            using var r = f.Signed("permission", BashRequest, out _);
            r.Content = new StringContent(BashRequest, Encoding.UTF8, contentType);
            tweak(r);
            using var response = await http.SendAsync(r);
            return response.StatusCode;
        }

        // a page in the browser (fetch / form post), or DNS rebinding to this port
        Assert.Equal(HttpStatusCode.Forbidden, await Post(r => r.Headers.Add("Origin", "https://evil.example")));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(r => r.Headers.Add("Sec-Fetch-Site", "cross-site")));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(r => r.Headers.Host = "evil.example"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await Post(_ => { }, "text/plain"));
        // not gyrodial-hook (no key)
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(r => r.Headers.Remove(HookKey.AuthHeader)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Post(r =>
        {
            r.Headers.Remove(HookKey.AuthHeader);
            r.Headers.Add(HookKey.AuthHeader, new string('0', 64));
        }));
        using (var unsignedNotify = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{f.Port}/notify")
        {
            Content = new StringContent("""{"message":"hi","notification_type":"idle_prompt"}""", Encoding.UTF8, "application/json"),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(unsignedNotify)).StatusCode);
        }
        Assert.DoesNotContain(f.Hub.Sent, m => m.Type is Protocol.T.Ask or Protocol.T.Notify);
    }

    [Fact]
    public async Task Forwards_notifications_but_not_permission_prompts()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        using var http = new HttpClient();

        using (var r = f.Signed("notify", """{"hook_event_name":"Notification","message":"Claude needs your permission","notification_type":"permission_prompt"}""", out _))
            (await http.SendAsync(r)).EnsureSuccessStatusCode();
        using (var r = f.Signed("notify", """{"hook_event_name":"Notification","message":"Claude is waiting for your input","notification_type":"idle_prompt"}""", out _))
            (await http.SendAsync(r)).EnsureSuccessStatusCode();
        var n = f.Hub.Next(Protocol.T.Notify);
        Assert.Equal("Claude is waiting for your input", n.GetString("text"));
        Assert.Equal("claude", n.GetString("kind"));
    }

    [Fact]
    public async Task Turning_it_off_stops_the_server()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        f.Host.Settings.Update(s => s.ClaudeCodeEnabled = false);
        Assert.Equal("Off", f.Integration.Status);
        await Task.Delay(500);
        using var http = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{f.Port}/health"));
    }

    [Fact]
    public void The_key_is_made_once_and_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gyrodial-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "hook.key");
            var key = new HookKey(path).Ensure();
            Assert.Equal(32, key.Length);
            Assert.Equal(key, new HookKey(path).Ensure());  // read back, not remade
            var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            Assert.Single(rules);  // this user only, nothing inherited
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- gyrodial-hook.exe, end to end

    static string HookExe()
    {
        // next to the tests (it travels with Gyrodial.Integrations), else in the GyrodialHook project's output
        var local = Path.Combine(AppContext.BaseDirectory, "gyrodial-hook.exe");
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gyrodial.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var exe = Path.Combine(dir!.FullName, "src", "GyrodialHook", "bin", config, "net10.0", "gyrodial-hook.exe");
        Assert.True(File.Exists(exe), $"build GyrodialHook first ({exe})");
        return exe;
    }

    static async Task<(string Stdout, string Stderr, int Exit)> RunHookAsync(int port, string? keyFile, string mode, string stdin, string? proxy = null)
    {
        var psi = new ProcessStartInfo(HookExe(), mode)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["GYRODIAL_HOOK_PORT"] = port.ToString();
        psi.Environment["GYRODIAL_HOOK_KEY_FILE"] = keyFile ?? Path.Combine(Path.GetTempPath(), "no-such-gyrodial-key-" + Guid.NewGuid().ToString("N"));
        if (proxy is not null)
        {
            psi.Environment["HTTP_PROXY"] = proxy;
            psi.Environment["HTTPS_PROXY"] = proxy;
            psi.Environment["ALL_PROXY"] = proxy;
        }
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (await stdout, await stderr, p.ExitCode);
    }

    /// <summary>Something else on the port, answering every request with "allow" (and no MAC).</summary>
    static async Task SquatAsync(TcpListener listener, CancellationToken cancel)
    {
        const string body = """{"decision":"allow"}""";
        var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        while (!cancel.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancel);
            var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            var text = "";
            while (!text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buffer, cancel);
                if (n == 0) break;
                text += Encoding.ASCII.GetString(buffer, 0, n);
            }
            await Task.Delay(100, cancel);  // let the body arrive; it's ignored
            await stream.WriteAsync(reply, cancel);
        }
    }

    [Fact]
    public async Task Hook_prints_the_dial_decision()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        f.Key.Ensure();

        var allowRun = RunHookAsync(f.Port, f.Key.FilePath, "permission", BashRequest);
        var ask = f.Hub.Next(Protocol.T.Ask, 15000);
        f.Hub.Receive(Protocol.T.Ans, new { id = ask.GetString("id"), ok = true });
        var allowed = await allowRun;
        Assert.Equal(0, allowed.Exit);
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""", allowed.Stdout.Trim());
        Assert.Equal("", allowed.Stderr);

        // a proxy in the environment is ignored: the hook still talks to Gyrodial directly
        var denyRun = RunHookAsync(f.Port, f.Key.FilePath, "permission", BashRequest, proxy: "http://127.0.0.1:9");
        ask = f.Hub.Next(Protocol.T.Ask, 15000);
        f.Hub.Receive(Protocol.T.Ans, new { id = ask.GetString("id"), ok = false });
        var denied = await denyRun;
        var decision = JsonNode.Parse(denied.Stdout)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("deny", decision["behavior"]!.GetValue<string>());
        Assert.Equal("Denied from Gyrodial", decision["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Hook_takes_its_mode_from_the_event()
    {
        using var f = new Fixture();
        await f.ReadyAsync();
        f.Key.Ensure();
        // registered as "permission", but Claude Code says it's a notification
        var run = await RunHookAsync(f.Port, f.Key.FilePath, "permission", """{"hook_event_name":"Notification","message":"Done thinking","notification_type":"idle_prompt"}""");
        Assert.Equal("", run.Stdout);
        Assert.Equal("Done thinking", f.Hub.Next(Protocol.T.Notify).GetString("text"));
    }

    [Fact]
    public async Task Hook_ignores_an_impostor_on_the_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var squatter = SquatAsync(listener, cts.Token);
        var keyDir = Path.Combine(Path.GetTempPath(), "gyrodial-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = new HookKey(Path.Combine(keyDir, "hook.key"));
            key.Ensure();
            var result = await RunHookAsync(port, key.FilePath, "permission", BashRequest);
            Assert.Equal(0, result.Exit);
            Assert.Equal("", result.Stdout);  // "allow" without Gyrodial's MAC isn't believed
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await squatter; } catch (Exception) { }
            try { Directory.Delete(keyDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Hook_is_silent_when_dial_is_not_running()
    {
        var keyDir = Path.Combine(Path.GetTempPath(), "gyrodial-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = new HookKey(Path.Combine(keyDir, "hook.key"));
            key.Ensure();
            var result = await RunHookAsync(Net.FreePort(), key.FilePath, "permission", BashRequest);
            Assert.Equal(0, result.Exit);
            Assert.Equal("", result.Stdout);
            Assert.Equal("", result.Stderr);

            var notify = await RunHookAsync(Net.FreePort(), key.FilePath, "notify", "not json at all");
            Assert.Equal(0, notify.Exit);
            Assert.Equal("", notify.Stdout);

            // never ran Gyrodial (no key): silent without even trying
            var noKey = await RunHookAsync(Net.FreePort(), null, "permission", BashRequest);
            Assert.Equal(0, noKey.Exit);
            Assert.Equal("", noKey.Stdout);
        }
        finally
        {
            try { Directory.Delete(keyDir, true); } catch (IOException) { }
        }
    }
}
