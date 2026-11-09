using System.Globalization;
using System.Speech.Synthesis;

namespace Gyrodial.Integrations.Claude;

/// <summary>Reads Claude's replies aloud (when Settings → Claude → Speak replies is on).</summary>
static class Speaker
{
    static readonly object Gate = new();
    static SpeechSynthesizer? _synth;

    public static void Speak(string text, string language, Action<string> log)
    {
        try
        {
            lock (Gate)
            {
                _synth ??= CreateSynth();
                _synth.SpeakAsyncCancelAll();
                var voice = PickVoice(_synth, language);
                if (voice is not null) _synth.SelectVoice(voice);
                _synth.SpeakAsync(text);
            }
        }
        catch (Exception e)
        {
            log($"Couldn't speak the reply: {e.Message}");
        }
    }

    public static void Stop()
    {
        lock (Gate) _synth?.SpeakAsyncCancelAll();
    }

    static SpeechSynthesizer CreateSynth()
    {
        var synth = new SpeechSynthesizer();
        synth.SetOutputToDefaultAudioDevice();
        return synth;
    }

    static string? PickVoice(SpeechSynthesizer synth, string language)
    {
        var culture = string.IsNullOrWhiteSpace(language) ? CultureInfo.CurrentUICulture : new CultureInfo(language);
        var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
        return (voices.FirstOrDefault(v => v.Culture.Name == culture.Name)
            ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName))?.Name;
    }
}
