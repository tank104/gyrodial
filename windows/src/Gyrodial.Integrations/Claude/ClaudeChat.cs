using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Gyrodial.Integrations.Claude;

/// <summary>One exchange, for the conversation window.</summary>
public sealed record Exchange(DateTimeOffset At, string You, string Claude, bool Failed = false);

/// <summary>
/// The conversation with Claude behind the dial's Claude face: an append-only history (so earlier
/// thinking blocks stay valid and the prompt cache stays warm), started over after 10 quiet
/// minutes, when it gets long, or when the model or system prompt changes.
/// </summary>
sealed class ClaudeChat
{
    static readonly TimeSpan IdleReset = TimeSpan.FromMinutes(10);
    const int MaxTurns = 10;

    // Models that take the server-side refusal fallback ("default" routing): a declined request is
    // re-served by a fallback model inside the same call instead of coming back empty.
    static readonly HashSet<string> FallbackModels = ["claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"];
    // Models that take output_config.effort (the 4.6+ family); others reject it.
    static readonly string[] EffortPrefixes = ["claude-fable", "claude-mythos", "claude-opus-5", "claude-sonnet-5", "claude-haiku-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6"];

    readonly List<BetaMessageParam> _history = [];
    readonly object _gate = new();
    AnthropicClient? _client;
    string? _clientKey;
    string? _model, _system;
    DateTimeOffset _lastAt;

    /// <summary>
    /// Always set explicitly: the SDK otherwise honors ANTHROPIC_BASE_URL, which Claude Code setups
    /// often point at a gateway, and the key pasted into Gyrodial shouldn't follow it there.
    /// </summary>
    const string ProductionUrl = "https://api.anthropic.com";

    /// <summary>Another API endpoint (tests point this at a local stub).</summary>
    internal string? BaseUrl { get; init; }

    public sealed record Reply(string Text, bool Failed);

    public async Task<Reply> AskAsync(string apiKey, string model, string system, string question, CancellationToken cancel)
    {
        List<BetaMessageParam> messages;
        lock (_gate)
        {
            if (_client is null || _clientKey != apiKey)
            {
                _client = new AnthropicClient { ApiKey = apiKey, BaseUrl = BaseUrl ?? ProductionUrl };
                _clientKey = apiKey;
            }
            var now = DateTimeOffset.Now;
            if (now - _lastAt > IdleReset || model != _model || system != _system || _history.Count >= MaxTurns * 2)
            {
                // A fresh conversation rather than trimming the front: dropping earlier turns would
                // invalidate every later thinking block in the history.
                _history.Clear();
                _model = model;
                _system = system;
            }
            _lastAt = now;
            _history.Add(new BetaMessageParam { Role = Role.User, Content = question });
            messages = [.. _history];
        }

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 8000,
            System = system,
            Messages = messages,
        };
        if (Array.Exists(EffortPrefixes, p => model.StartsWith(p, StringComparison.Ordinal)))
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = Effort.Low } };  // quick spoken answers
        if (FallbackModels.Contains(model))
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };

        try
        {
            var response = await _client.Beta.Messages.Create(parameters, cancel).ConfigureAwait(false);
            if (response.StopReason == "refusal")
            {
                Undo(question);
                return new Reply("Claude can't help with that one.", true);
            }

            // Keep the whole assistant turn (thinking blocks included, unchanged) for the next request.
            var blocks = new List<BetaContentBlockParam>();
            var text = new List<string>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t))
                {
                    blocks.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Add(t.Text);
                }
                else if (block.TryPickThinking(out var th))
                {
                    blocks.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                }
                else if (block.TryPickRedactedThinking(out var rt))
                {
                    blocks.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                }
            }
            var answer = string.Join("\n", text).Trim();
            lock (_gate)
            {
                if (blocks.Count > 0) _history.Add(new BetaMessageParam { Role = Role.Assistant, Content = blocks });
                else Undo(question);
            }
            return answer.Length > 0 ? new Reply(answer, false) : new Reply("Claude had nothing to say.", true);
        }
        catch (OperationCanceledException)
        {
            Undo(question);
            throw;
        }
        catch (AnthropicUnauthorizedException)
        {
            Undo(question);
            return new Reply("Your Claude API key was rejected. Check it in Gyrodial settings.", true);
        }
        catch (AnthropicNotFoundException)
        {
            Undo(question);
            return new Reply($"Claude doesn't know the model \"{model}\". Check Gyrodial settings.", true);
        }
        catch (AnthropicRateLimitException)
        {
            Undo(question);
            return new Reply("Claude is busy right now. Try again in a moment.", true);
        }
        catch (AnthropicApiException e)
        {
            Undo(question);
            return new Reply($"Claude couldn't answer ({e.Message}).", true);
        }
        catch (AnthropicIOException)
        {
            Undo(question);
            return new Reply("Couldn't reach Claude. Check your internet connection.", true);
        }
    }

    /// <summary>Takes back a question that got no answer, so the history stays user/assistant pairs.</summary>
    void Undo(string question)
    {
        lock (_gate)
        {
            if (_history.Count > 0 && _history[^1].Role == Role.User) _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>A tiny request to check the key and model.</summary>
    public static async Task<string> TestAsync(string apiKey, string model, CancellationToken cancel)
    {
        var client = new AnthropicClient { ApiKey = apiKey, BaseUrl = ProductionUrl };
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 1000,
            Messages = [new() { Role = Role.User, Content = "Reply with just the word: ready" }],
        };
        if (Array.Exists(EffortPrefixes, p => model.StartsWith(p, StringComparison.Ordinal)))
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = Effort.Low } };
        var response = await client.Beta.Messages.Create(parameters, cancel).ConfigureAwait(false);
        return string.Join("", response.Content.Select(b => b.TryPickText(out var t) ? t.Text : "")).Trim();
    }
}
