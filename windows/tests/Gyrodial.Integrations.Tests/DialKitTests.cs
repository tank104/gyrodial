using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Gyrodial.Core;
using Gyrodial.Integrations.DialKit;

namespace Gyrodial.Integrations.Tests;

public class DialKitTests
{
    // What dialkit-watt sends: DialStore.getPanels("panel") → {id, name, controls, values}
    const string Panels = """
        [{
          "id": "card", "name": "Card",
          "controls": [
            { "type": "slider", "path": "blur", "label": "Blur", "min": 0, "max": 40, "step": 0.5 },
            { "type": "toggle", "path": "shadow", "label": "Shadow" },
            { "type": "folder", "path": "motion", "label": "Motion", "children": [
                { "type": "transition", "path": "motion.spring", "label": "Spring" },
                { "type": "action", "path": "motion.replay", "label": "Replay" }
            ]},
            { "type": "select", "path": "size", "label": "Size", "options": ["sm", { "value": "lg", "label": "Large" }] },
            { "type": "color", "path": "tint", "label": "Tint" },
            { "type": "pad", "path": "offset", "label": "Offset", "pad": { "type": "pad", "x": [0, -10, 10, 1] } }
          ],
          "values": {
            "blur": 12, "shadow": true,
            "motion.spring": { "type": "spring", "visualDuration": 0.3, "bounce": 0.2 },
            "motion.spring.__mode": "simple",
            "size": "sm", "tint": "#ff8800", "offset": { "x": 0, "y": 0 }
          }
        }]
        """;

    static DialKitKnobs Load()
    {
        var k = new DialKitKnobs();
        k.Load(JsonNode.Parse(Panels)!.AsArray());
        return k;
    }

    [Fact]
    public void Flattens_controls_into_knobs()
    {
        var k = Load();
        Assert.Equal(
            ["Blur", "Shadow", "Motion · Spring · Duration", "Motion · Spring · Bounce", "Motion · Replay", "Size", "Tint", "Offset · X", "Offset · Y"],
            k.Knobs.Select(x => x.Label));
        Assert.Equal("12.0", k.DisplayText(k.Knobs[0]));
        Assert.Equal("On", k.DisplayText(k.Knobs[1]));
        Assert.Equal("0.30s", k.DisplayText(k.Knobs[2]));
        Assert.Equal("sm", k.DisplayText(k.Knobs[5]));
        Assert.Equal("#ff8800", k.DisplayText(k.Knobs[6]));
    }

    [Fact]
    public void Turning_snaps_to_steps_and_clamps()
    {
        var k = Load();
        var blur = k.Knobs[0];
        var set = k.Turn(blur, 3)!.Message;
        Assert.Equal("set", set["type"]!.GetValue<string>());
        Assert.Equal("card", set["panel"]!.GetValue<string>());
        Assert.Equal("blur", set["path"]!.GetValue<string>());
        Assert.Equal(13.5, set["value"]!.GetValue<double>());
        Assert.Equal(13.5, k.NumberValue(blur));  // kept locally until the page echoes it

        k.Turn(blur, 1000);
        Assert.Equal(40, k.NumberValue(blur));
        Assert.Null(k.Turn(blur, 1));  // already at the end: nothing to send
    }

    [Fact]
    public void Springs_bools_selects_actions_and_pads()
    {
        var k = Load();

        var bounce = k.Knobs[3];
        var spring = k.Turn(bounce, 2)!.Message["value"]!.AsObject();
        Assert.Equal("spring", spring["type"]!.GetValue<string>());
        Assert.Equal(0.3, spring["bounce"]!.GetValue<double>(), 6);
        Assert.Equal(0.3, spring["visualDuration"]!.GetValue<double>(), 6);

        var shadow = k.Knobs[1];
        Assert.False(k.Tap(shadow)!.Message["value"]!.GetValue<bool>());
        Assert.Null(k.Turn(shadow, -1));  // already off

        var size = k.Knobs[5];
        Assert.Equal("lg", k.Tap(size)!.Message["value"]!.GetValue<string>());
        Assert.Equal("Large", k.DisplayText(size));

        var replay = k.Knobs[4];
        var action = k.Tap(replay)!.Message;
        Assert.Equal("action", action["type"]!.GetValue<string>());
        Assert.Equal("motion.replay", action["path"]!.GetValue<string>());

        var x = k.Knobs[7];
        var point = k.Turn(x, -4)!.Message["value"]!.AsObject();
        Assert.Equal(-4, point["x"]!.GetValue<double>());
        Assert.Equal(0, point["y"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:3000", true)]
    [InlineData("https://localhost", true)]
    [InlineData(null, true)]
    [InlineData("https://example.com", false)]
    [InlineData("http://localhost.evil.com", false)]
    [InlineData("file://", false)]
    public void Only_local_pages(string? origin, bool allowed) =>
        Assert.Equal(allowed, DialKitIntegration.IsAllowedOrigin(origin));

    static async Task<ClientWebSocket> ConnectAsync(int port, string origin)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", origin);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/bridge"), CancellationToken.None);
        return ws;
    }

    static Task SendAsync(ClientWebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<JsonObject> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(5000);
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count))!.AsObject();
    }

    [Fact]
    public async Task A_dialkit_page_gets_the_dial()
    {
        var port = Net.FreePort();
        using var host = new FakeHost();
        var hub = new FakeHub();
        using var integration = new DialKitIntegration(port);
        integration.Start(hub, host);
        await Net.WaitForPortAsync(port);

        using var page = await ConnectAsync(port, "http://localhost:5173");
        await SendAsync(page, """{"type":"hello","kind":"dialkit"}""");
        await SendAsync(page, """{"type":"presence","visible":true,"focused":true,"title":"My app"}""");
        await SendAsync(page, $$"""{"type":"state","panels":{{Panels}}}""");

        DialMessage dial;
        do dial = hub.Next(Protocol.T.Dial); while (dial.GetString("name") is null);
        Assert.True(dial.GetBool("on"));
        Assert.Equal("Card", dial.GetString("panel"));
        Assert.Equal("Blur", dial.GetString("name"));
        Assert.Equal("num", dial.GetString("kind"));
        Assert.Equal(12, dial.GetDouble("val"));
        Assert.Equal(9, dial.GetInt("cnt"));
        Assert.Equal("DialKit: My app", integration.Status);

        hub.Receive(Protocol.T.Dial, new { a = "turn", d = 2 });
        var set = await ReceiveAsync(page);
        Assert.Equal("set", set["type"]!.GetValue<string>());
        Assert.Equal("blur", set["path"]!.GetValue<string>());
        Assert.Equal(13, set["value"]!.GetValue<double>());

        hub.Receive(Protocol.T.Dial, new { a = "next" });
        DialMessage next;
        do next = hub.Next(Protocol.T.Dial); while (next.GetString("name") != "Shadow");
        Assert.Equal("bool", next.GetString("kind"));
        Assert.Equal(1, next.GetInt("idx"));

        // the page goes to the background: the dial is let go
        await SendAsync(page, """{"type":"presence","visible":false,"focused":false,"title":"My app"}""");
        DialMessage off;
        do off = hub.Next(Protocol.T.Dial); while (off.GetBool("on") != false);
        Assert.Equal("1 page connected, none in front", integration.Status);
    }

    [Fact]
    public async Task A_game_page_gets_inputs()
    {
        var port = Net.FreePort();
        using var host = new FakeHost();
        var hub = new FakeHub();
        using var integration = new DialKitIntegration(port);
        integration.Start(hub, host);
        await Net.WaitForPortAsync(port);

        using var page = await ConnectAsync(port, "http://127.0.0.1:8080");
        await SendAsync(page, """{"type":"hello","kind":"game"}""");
        await SendAsync(page, """{"type":"presence","visible":true,"focused":true,"title":"Pong"}""");
        await SendAsync(page, """{"type":"state","label":"PLAYER 1"}""");
        DialMessage game;
        do game = hub.Next(Protocol.T.Game); while (game.GetString("label") != "PLAYER 1");
        Assert.True(game.GetBool("on"));

        hub.Receive(Protocol.T.Game, new { @in = "turn", v = 12.5 });
        var input = await ReceiveAsync(page);
        Assert.Equal("input", input["type"]!.GetValue<string>());
        Assert.Equal("turn", input["input"]!.GetValue<string>());
        Assert.Equal(12.5, input["value"]!.GetValue<double>());

        hub.Receive(Protocol.T.Game, new { @in = "swipe", v = "l" });
        var swipe = await ReceiveAsync(page);
        Assert.Equal("l", swipe["value"]!.GetValue<string>());
    }

    [Fact]
    public void A_pad_without_a_step_moves_like_dialkit()
    {
        // DialKit's pad moves in 1/200ths of the range when the axis has no step (dial-pad.ts)
        var k = new DialKitKnobs();
        k.Load(JsonNode.Parse("""
            [{ "id": "p", "name": "P",
               "controls": [{ "type": "pad", "path": "pos", "label": "Pos", "pad": { "type": "pad", "x": [0, -100, 100] } }],
               "values": { "pos": { "x": 0, "y": 0 } } }]
            """)!.AsArray());
        var x = k.Knobs.First(n => n.Label == "Pos · X");
        Assert.Equal(1.0, x.Step, 6);
        Assert.Equal(3, k.Turn(x, 3)!.Message["value"]!["x"]!.GetValue<double>(), 6);
    }

    [Fact]
    public async Task Reconnecting_says_off_for_a_dial_that_may_still_show_a_page()
    {
        var port = Net.FreePort();
        using var host = new FakeHost();
        var hub = new FakeHub();
        using var integration = new DialKitIntegration(port);
        integration.Start(hub, host);
        await Net.WaitForPortAsync(port);
        while (hub.Sent.TryTake(out _)) { }

        hub.SetConnected(false);
        hub.SetConnected(true);
        Assert.False(hub.Next(Protocol.T.Dial).GetBool("on"));
        Assert.False(hub.Next(Protocol.T.Game).GetBool("on"));
    }

    [Fact]
    public async Task Switching_it_off_during_page_traffic_does_not_hang()
    {
        var port = Net.FreePort();
        using var host = new FakeHost();
        var hub = new FakeHub();
        using var integration = new DialKitIntegration(port);
        integration.Start(hub, host);
        await Net.WaitForPortAsync(port);

        using var page = await ConnectAsync(port, "http://localhost:5173");
        await SendAsync(page, """{"type":"hello","kind":"dialkit"}""");
        using var stop = new CancellationTokenSource();
        var traffic = Task.Run(async () =>
        {
            var focused = false;
            while (!stop.IsCancellationRequested && page.State == WebSocketState.Open)
            {
                focused = !focused;
                try
                {
                    await SendAsync(page, $$"""{"type":"presence","visible":true,"focused":{{(focused ? "true" : "false")}},"title":"T"}""");
                    await SendAsync(page, $$"""{"type":"state","panels":{{Panels}}}""");
                }
                catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        });

        // the settings thread takes the integration's lock, then DialKit's; the page's handler held
        // DialKit's and then wanted the integration's: this used to freeze the settings thread
        var toggles = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                host.Settings.Update(s => s.DialKitEnabled = !s.DialKitEnabled);
                Thread.Sleep(5);
            }
        });
        await toggles.WaitAsync(TimeSpan.FromSeconds(20));
        stop.Cancel();
        await traffic.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Pages_from_the_internet_are_refused()
    {
        var port = Net.FreePort();
        using var host = new FakeHost();
        using var integration = new DialKitIntegration(port);
        integration.Start(new FakeHub(), host);
        await Net.WaitForPortAsync(port);
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(port, "https://example.com"));
    }
}
