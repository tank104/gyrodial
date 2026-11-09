using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Puck.Integrations.Claude;
using Puck.Integrations.Common;

namespace Puck.Integrations.Tests;

/// <summary>The Messages API requests ClaudeChat makes, against a local stand-in for the API.</summary>
public class ClaudeChatTests
{
    sealed record Captured(JsonObject Body, string? Beta, string? Key);

    static async Task<(LoopbackServer Server, ConcurrentQueue<Captured> Requests)> StubAsync(int port, Func<int, string> reply)
    {
        var requests = new ConcurrentQueue<Captured>();
        var count = 0;
        var server = await LoopbackServer.StartAsync(port, app =>
            app.MapPost("/v1/messages", (RequestDelegate)(async ctx =>
            {
                var body = (await JsonNode.ParseAsync(ctx.Request.Body))!.AsObject();
                requests.Enqueue(new Captured(body, ctx.Request.Headers["anthropic-beta"].FirstOrDefault(), ctx.Request.Headers["x-api-key"].FirstOrDefault()));
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(reply(Interlocked.Increment(ref count)));
            })));
        return (server, requests);
    }

    static string Message(string text, string stop = "end_turn") => $$$"""
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5",
         "content":[{"type":"thinking","thinking":"","signature":"sig-1"},{"type":"text","text":"{{{text}}}"}],
         "stop_reason":"{{{stop}}}","stop_sequence":null,"stop_details":null,"container":null,"context_management":null,
         "usage":{"input_tokens":12,"output_tokens":7,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,
                  "server_tool_use":null,"service_tier":"standard","cache_creation":null,"inference_geo":null}}
        """;

    [Fact]
    public async Task Sends_a_short_low_effort_request_and_keeps_the_history_append_only()
    {
        var port = Net.FreePort();
        var (server, requests) = await StubAsync(port, n => Message(n == 1 ? "Paris." : "About 2.1 million."));
        await using var _ = server;
        var chat = new ClaudeChat { BaseUrl = $"http://127.0.0.1:{port}" };

        var first = await chat.AskAsync("sk-test", "claude-sonnet-5-5", "Be brief.", "Capital of France?", CancellationToken.None);
        Assert.Equal(new ClaudeChat.Reply("Paris.", false), first);
        var second = await chat.AskAsync("sk-test", "claude-sonnet-5-5", "Be brief.", "How many people live there?", CancellationToken.None);
        Assert.Equal("About 2.1 million.", second.Text);

        Assert.True(requests.TryDequeue(out var r1));
        Assert.Equal("sk-test", r1.Key);
        Assert.Equal("claude-sonnet-5-5", r1.Body["model"]!.GetValue<string>());
        Assert.Equal("Be brief.", r1.Body["system"]!.GetValue<string>());
        Assert.Equal("low", r1.Body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("default", r1.Body["fallbacks"]!.GetValue<string>());
        Assert.Contains("server-side-fallback-2026-07-01", r1.Beta);
        Assert.Single(r1.Body["messages"]!.AsArray());

        // the second request replays the first turn unchanged, thinking block included
        Assert.True(requests.TryDequeue(out var r2));
        var messages = r2.Body["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        var assistant = messages[1]!["content"]!.AsArray();
        Assert.Equal("thinking", assistant[0]!["type"]!.GetValue<string>());
        Assert.Equal("sig-1", assistant[0]!["signature"]!.GetValue<string>());
        Assert.Equal("Paris.", assistant[1]!["text"]!.GetValue<string>());
        Assert.Equal("How many people live there?", messages[2]!["content"]!.ToString());
    }

    [Fact]
    public async Task Older_models_get_no_effort_or_fallbacks_and_refusals_are_explained()
    {
        var port = Net.FreePort();
        var (server, requests) = await StubAsync(port, _ => Message("", "refusal"));
        await using var _ = server;
        var chat = new ClaudeChat { BaseUrl = $"http://127.0.0.1:{port}" };

        var reply = await chat.AskAsync("sk-test", "claude-haiku-4-5", "Be brief.", "Hi", CancellationToken.None);
        Assert.True(reply.Failed);
        Assert.Equal("Claude can't help with that one.", reply.Text);

        Assert.True(requests.TryDequeue(out var r));
        Assert.Null(r.Body["output_config"]);
        Assert.Null(r.Body["fallbacks"]);
    }

    [Fact]
    public void Truncates_on_a_word_boundary()
    {
        Assert.Equal("short", TextUtil.Truncate("  short ", 600));
        var cut = TextUtil.Truncate("one two three four five", 12);
        Assert.Equal("one two…", cut);
        Assert.True(TextUtil.Truncate(new string('x', 50), 10).Length <= 10);
    }

    [Fact]
    public void Truncates_by_utf8_bytes()
    {
        // the puck's limits are bytes: accents, curly quotes and dashes take 2-3 each
        var text = string.Join(' ', Enumerable.Repeat("“Größe” — naïve café", 60));
        var cut = TextUtil.Truncate(text, 600);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(cut) <= 600);
        Assert.EndsWith("…", cut);
        // and a message built from it stays under the puck's 1024-byte line, unescaped
        var line = Puck.Core.PuckMessage.Create(Puck.Core.Protocol.T.Claude, new { s = "reply", text = cut }).ToJson();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) < 1024, $"{System.Text.Encoding.UTF8.GetByteCount(line)} bytes");
        Assert.Contains("“Größe”", line);
    }
}
