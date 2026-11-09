using System.Collections.ObjectModel;
using Puck.Core;
using Puck.Integrations.Common;

namespace Puck.Integrations.Claude;

/// <summary>
/// Talk to Claude from the puck: tap the Claude face to listen (the PC's microphone, Windows speech
/// recognition), tap again to send. Only the text goes to Claude; the answer comes back to the puck.
/// </summary>
public sealed class ClaudeIntegration : IntegrationBase
{
    const int MaxReply = 600;  // UTF-8 bytes: the puck keeps 640
    static readonly TimeSpan TranscriptInterval = TimeSpan.FromMilliseconds(200);

    readonly SemaphoreSlim _turn = new(1, 1);  // one listen/send at a time
    readonly ClaudeChat _chat = new();
    readonly object _gate = new();
    IDictation? _dictation;
    MicLevelMeter? _meter;
    Timer? _transcriptTimer;
    string _transcript = "";
    string _sentTranscript = "";
    float _sentLevel = -1;
    CancellationTokenSource? _request;
    State _state = State.Idle;
    ConversationWindow? _window;

    enum State { Idle, Listening, Thinking }

    /// <summary>Every exchange this session, newest last (for the conversation window). UI thread only.</summary>
    public ObservableCollection<Exchange> Exchanges { get; } = [];

    public override string Id => "claude";
    public override string DisplayName => "Claude";

    public override IReadOnlyList<IntegrationAction> Actions =>
    [
        new("Test API key", TestKeyAsync),
        new("Open conversation", () =>
        {
            Host.RunOnUi(ShowWindow);
            return Task.FromResult<string?>(null);
        }),
    ];

    protected override bool IsEnabled(PuckSettings s) => s.ClaudeEnabled;

    protected override void Subscribe()
    {
        On(Protocol.T.Claude, m =>
        {
            switch (m.GetString("a"))
            {
                case "listen": _ = ListenAsync(); break;
                case "send": _ = SendAsync(); break;
                case "cancel": _ = CancelAsync(); break;
            }
        });
        OnConnection(connected =>
        {
            if (!connected)
            {
                _ = CancelAsync(sendIdle: false);
                return;
            }
            // A quick reconnect may find the puck still showing "listening" or "thinking" from
            // before: everything was cancelled when it went away, so tell it so.
            bool idle;
            lock (_gate) idle = _state == State.Idle;
            if (idle) SendState("idle");
        });
    }

    protected override void Enable() => UpdateStatus();

    protected override void Disable() => _ = CancelAsync();

    protected override void OnSettingsChanged() => UpdateStatus();

    void UpdateStatus()
    {
        if (!Enabled) return;
        lock (_gate)
        {
            SetStatus(_state switch
            {
                State.Listening => "Listening…",
                State.Thinking => "Asking Claude…",
                _ => Secrets.ClaudeApiKey(Settings) is null ? "No API key: add one in the Claude settings" : $"Ready ({Settings.ClaudeModel})",
            });
        }
    }

    void SendState(string s, string? text = null, float? level = null)
    {
        if (text is null && level is null) Send(Protocol.T.Claude, new { s });
        else if (level is null) Send(Protocol.T.Claude, new { s, text });
        else Send(Protocol.T.Claude, new { s, text = text ?? "", lvl = Math.Round(level.Value, 2) });
    }

    // ---------------------------------------------------------------- listen

    async Task ListenAsync()
    {
        if (Secrets.ClaudeApiKey(Settings) is null)
        {
            SendState("error", "Add your Claude API key in Puck settings");
            return;
        }
        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListeningAsync().ConfigureAwait(false);
            CancelRequest();

            IDictation dictation;
            try
            {
                dictation = await Dictation.StartAsync(Settings.SpeechLanguage, Log).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log($"Can't listen: {e.Message}");
                SendState("error", "Can't hear you: check the PC's microphone");
                return;
            }

            lock (_gate)
            {
                _dictation = dictation;
                _transcript = _sentTranscript = "";
                _sentLevel = -1;
                _state = State.Listening;
            }
            dictation.Transcript += t =>
            {
                lock (_gate) _transcript = t;
            };
            _meter = MicLevelMeter.TryStart();
            _transcriptTimer = new Timer(_ => PushTranscript(), null, TranscriptInterval, TranscriptInterval);
            SendState("listening", "", 0);
            UpdateStatus();
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>The live transcript and mic level, at most 5 times a second, only when they changed.</summary>
    void PushTranscript()
    {
        string text;
        float level;
        lock (_gate)
        {
            if (_state != State.Listening) return;
            text = Common.TextUtil.Truncate(_transcript, MaxReply);
            level = _meter?.Level ?? 0;
            if (text == _sentTranscript && Math.Abs(level - _sentLevel) < 0.05f) return;
            _sentTranscript = text;
            _sentLevel = level;
        }
        SendState("listening", text, level);
    }

    /// <summary>Stops the mic, returning what was said (empty if it wasn't listening).</summary>
    async Task<string> StopListeningAsync()
    {
        IDictation? dictation;
        lock (_gate)
        {
            dictation = _dictation;
            _dictation = null;
        }
        _transcriptTimer?.Dispose();
        _transcriptTimer = null;
        _meter?.Dispose();
        _meter = null;
        if (dictation is null) return "";
        try
        {
            return (await dictation.StopAsync().ConfigureAwait(false)).Trim();
        }
        finally
        {
            await dictation.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- send

    async Task SendAsync()
    {
        CancellationTokenSource request;
        string question;
        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            question = await StopListeningAsync().ConfigureAwait(false);
            if (question.Length == 0)
            {
                lock (_gate) question = _transcript.Trim();  // the recognizer never finalized it
            }
            if (question.Length == 0)
            {
                lock (_gate) _state = State.Idle;
                SendState("error", "Didn't catch that");
                UpdateStatus();
                return;
            }
            request = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var previous = Interlocked.Exchange(ref _request, request);
            try { previous?.Cancel(); } catch (ObjectDisposedException) { }
            lock (_gate) _state = State.Thinking;
            SendState("thinking", Common.TextUtil.Truncate(question, MaxReply));
            UpdateStatus();
        }
        finally
        {
            _turn.Release();
        }

        try
        {
            var key = Secrets.ClaudeApiKey(Settings);
            if (key is null)
            {
                Finish(question, "Add your Claude API key in Puck settings", failed: true);
                return;
            }
            var reply = await _chat.AskAsync(key, Settings.ClaudeModel, Settings.ClaudeSystemPrompt, question, request.Token).ConfigureAwait(false);
            Finish(question, reply.Text, reply.Failed);  // no-op if it was cancelled meanwhile
        }
        catch (OperationCanceledException)
        {
            // cancelled from the puck (it already shows idle), or the 2-minute timeout
            if (Volatile.Read(ref _request) == request) Finish(question, "Claude took too long. Try again.", failed: true);
        }
        catch (Exception e)
        {
            // The exception's type and message only: no request content in the log
            Log($"Asking Claude failed: {e.GetType().Name}: {e.Message}");
            Finish(question, "Something went wrong asking Claude.", failed: true);
        }
        finally
        {
            Interlocked.CompareExchange(ref _request, null, request);
            request.Dispose();
        }
    }

    /// <summary>Cancels the request in flight, if any (its owner disposes it).</summary>
    void CancelRequest()
    {
        var request = Interlocked.Exchange(ref _request, null);
        try { request?.Cancel(); } catch (ObjectDisposedException) { }
    }

    void Finish(string question, string answer, bool failed)
    {
        lock (_gate)
        {
            if (_state != State.Thinking) return;
            _state = State.Idle;
        }
        SendState(failed ? "error" : "reply", Common.TextUtil.Truncate(answer, MaxReply));
        UpdateStatus();
        // What was said stays out of the log file (it's kept for days); the conversation window has it
        Log(failed ? $"No answer ({question.Length}-character question)" : $"Answered a {question.Length}-character question ({answer.Length} characters)");
        Host.RunOnUi(() => Exchanges.Add(new Exchange(DateTimeOffset.Now, question, answer, failed)));
        if (!failed && Settings.SpeakReplies) Speaker.Speak(answer, Settings.SpeechLanguage, Log);
    }

    async Task CancelAsync(bool sendIdle = true)
    {
        CancelRequest();
        Speaker.Stop();
        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListeningAsync().ConfigureAwait(false);
            lock (_gate) _state = State.Idle;
        }
        finally
        {
            _turn.Release();
        }
        if (sendIdle) SendState("idle");
        UpdateStatus();
    }

    // ---------------------------------------------------------------- actions

    async Task<string?> TestKeyAsync()
    {
        var key = Secrets.ClaudeApiKey(Settings);
        if (key is null) return "Add your Claude API key first.";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var reply = await ClaudeChat.TestAsync(key, Settings.ClaudeModel, cts.Token).ConfigureAwait(false);
            return $"It works. {Settings.ClaudeModel} says: {Common.TextUtil.Truncate(reply, 80)}";
        }
        catch (Anthropic.Exceptions.AnthropicUnauthorizedException)
        {
            return "That API key was rejected.";
        }
        catch (Anthropic.Exceptions.AnthropicNotFoundException)
        {
            return $"The model \"{Settings.ClaudeModel}\" wasn't found.";
        }
        catch (Anthropic.Exceptions.AnthropicApiException e)
        {
            return $"Claude answered with an error: {e.Message}";
        }
        catch (Exception e) when (e is Anthropic.Exceptions.AnthropicIOException or OperationCanceledException)
        {
            return "Couldn't reach Claude. Check your internet connection.";
        }
    }

    void ShowWindow()
    {
        if (_window is { IsLoaded: true })
        {
            _window.Activate();
            return;
        }
        _window = new ConversationWindow(Exchanges);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }
}
