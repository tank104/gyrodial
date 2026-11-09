using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Gyrodial.Integrations.Claude;

/// <summary>Speech to text from the PC's microphone, while the dial's Claude face is listening.</summary>
interface IDictation : IAsyncDisposable
{
    /// <summary>The whole transcript so far, including the words not yet final. Raised on a background thread.</summary>
    event Action<string>? Transcript;

    Task StartAsync();

    /// <summary>Stops listening and returns the final transcript.</summary>
    Task<string> StopAsync();
}

static class Dictation
{
    /// <summary>The "online speech recognition" privacy setting is off (Settings → Privacy → Speech).</summary>
    const int PrivacyPolicyNotAccepted = unchecked((int)0x80045509);

    /// <summary>
    /// A dictation that is already listening: Windows' dictation (WinRT, the good one: needs
    /// Settings → Privacy &amp; security → Speech → Online speech recognition), falling back to the
    /// offline desktop recognizer (System.Speech). Starting is part of the try, because the privacy
    /// setting's error (0x80045509) usually surfaces from the session's StartAsync, not from compiling.
    /// </summary>
    public static async Task<IDictation> StartAsync(string language, Action<string> log)
    {
        IDictation? winrt = null;
        try
        {
            winrt = await WinRtDictation.CreateAsync(language).ConfigureAwait(false);
            await winrt.StartAsync().ConfigureAwait(false);
            return winrt;
        }
        catch (Exception e)
        {
            if (winrt is not null) await winrt.DisposeAsync().ConfigureAwait(false);
            if (e.HResult == PrivacyPolicyNotAccepted)
                log("Windows online speech recognition is off, so Gyrodial is using the offline recognizer. For better accuracy, turn on Settings → Privacy & security → Speech → Online speech recognition.");
            else
                log($"Windows dictation isn't available ({e.Message}); using the offline recognizer.");
        }

        var offline = SystemSpeechDictation.Create(language);
        try
        {
            await offline.StartAsync().ConfigureAwait(false);
            return offline;
        }
        catch
        {
            await offline.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Windows.Media.SpeechRecognition with the dictation grammar, as a continuous session.</summary>
sealed class WinRtDictation : IDictation
{
    readonly SpeechRecognizer _recognizer;
    readonly object _gate = new();
    readonly List<string> _final = [];
    string _hypothesis = "";
    TaskCompletionSource? _completed;

    public event Action<string>? Transcript;

    WinRtDictation(SpeechRecognizer recognizer)
    {
        _recognizer = recognizer;
        _recognizer.HypothesisGenerated += (_, e) =>
        {
            lock (_gate) _hypothesis = e.Hypothesis.Text;
            Transcript?.Invoke(Current());
        };
        _recognizer.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
        {
            if (e.Result.Status == SpeechRecognitionResultStatus.Success && e.Result.Confidence != SpeechRecognitionConfidence.Rejected
                && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                lock (_gate)
                {
                    _final.Add(e.Result.Text.Trim());
                    _hypothesis = "";
                }
                Transcript?.Invoke(Current());
            }
        };
        _recognizer.ContinuousRecognitionSession.Completed += (_, _) => _completed?.TrySetResult();
    }

    public static async Task<IDictation> CreateAsync(string language)
    {
        var recognizer = string.IsNullOrWhiteSpace(language) ? new SpeechRecognizer() : new SpeechRecognizer(new Language(language));
        try
        {
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
                throw new InvalidOperationException($"Dictation grammar: {compiled.Status}");
            // Long pauses while thinking of what to say shouldn't end the session; the dial's tap does.
            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(2);
            return new WinRtDictation(recognizer);
        }
        catch
        {
            recognizer.Dispose();
            throw;
        }
    }

    string Current()
    {
        lock (_gate)
            return string.Join(' ', _final.Append(_hypothesis).Where(s => s.Length > 0));
    }

    public async Task StartAsync()
    {
        _completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _recognizer.ContinuousRecognitionSession.StartAsync();
    }

    public async Task<string> StopAsync()
    {
        try
        {
            if (_recognizer.State != SpeechRecognizerState.Idle)
                await _recognizer.ContinuousRecognitionSession.StopAsync();
            // the last phrase is delivered before Completed; don't wait forever for it
            if (_completed is { } done) await Task.WhenAny(done.Task, Task.Delay(1500)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
        lock (_gate)
        {
            var text = string.Join(' ', _final);
            return text.Length > 0 ? text : _hypothesis;
        }
    }

    public ValueTask DisposeAsync()
    {
        _recognizer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The offline desktop recognizer (SAPI), for when Windows' online dictation is off.</summary>
sealed class SystemSpeechDictation : IDictation
{
    readonly System.Speech.Recognition.SpeechRecognitionEngine _engine;
    readonly object _gate = new();
    readonly List<string> _final = [];
    string _hypothesis = "";
    TaskCompletionSource? _completed;

    public event Action<string>? Transcript;

    SystemSpeechDictation(System.Speech.Recognition.SpeechRecognitionEngine engine)
    {
        _engine = engine;
        _engine.SpeechHypothesized += (_, e) =>
        {
            lock (_gate) _hypothesis = e.Result.Text;
            Transcript?.Invoke(Current());
        };
        _engine.SpeechRecognized += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Result.Text)) return;
            lock (_gate)
            {
                _final.Add(e.Result.Text.Trim());
                _hypothesis = "";
            }
            Transcript?.Invoke(Current());
        };
        _engine.RecognizeCompleted += (_, _) => _completed?.TrySetResult();
    }

    public static IDictation Create(string language)
    {
        var installed = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count == 0)
            throw new InvalidOperationException("No speech recognizer is installed. Add a speech language in Windows Settings → Time & language → Speech.");
        var want = string.IsNullOrWhiteSpace(language) ? CultureInfo.CurrentUICulture : new CultureInfo(language);
        var info = installed.FirstOrDefault(r => r.Culture.Name.Equals(want.Name, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == want.TwoLetterISOLanguageName)
            ?? installed[0];
        var engine = new System.Speech.Recognition.SpeechRecognitionEngine(info);
        try
        {
            engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            return new SystemSpeechDictation(engine);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    string Current()
    {
        lock (_gate)
            return string.Join(' ', _final.Append(_hypothesis).Where(s => s.Length > 0));
    }

    public Task StartAsync()
    {
        _completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.RecognizeAsync(System.Speech.Recognition.RecognizeMode.Multiple);
        return Task.CompletedTask;
    }

    public async Task<string> StopAsync()
    {
        try
        {
            _engine.RecognizeAsyncStop();
            if (_completed is { } done) await Task.WhenAny(done.Task, Task.Delay(1500)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        lock (_gate)
        {
            var text = string.Join(' ', _final);
            return text.Length > 0 ? text : _hypothesis;
        }
    }

    public ValueTask DisposeAsync()
    {
        _engine.Dispose();
        return ValueTask.CompletedTask;
    }
}
