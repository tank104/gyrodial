namespace Gyrodial.Core;

/// <summary>A dial this PC has paired with.</summary>
public sealed class KnownDial
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Bluetooth LE address (48-bit, as Windows gives it), 0 if never seen over BLE.</summary>
    public ulong BleAddress { get; set; }
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// Everything the user can set. Saved as JSON in %AppData%\Gyrodial\settings.json.
/// Only add properties (with defaults); never rename, so old files keep loading.
/// </summary>
public sealed class GyrodialSettings
{
    // ---- identity
    /// <summary>This PC's random id in the pairing handshake (16 hex chars). Made on first run.</summary>
    public string HostId { get; set; } = "";
    public List<KnownDial> Dials { get; set; } = [];

    // ---- app
    /// <summary>Off until the user turns it on: the app only writes the Run key when this is toggled.</summary>
    public bool StartWithWindows { get; set; }
    public bool PreferUsb { get; set; } = true;
    public bool ShowVolumeOverlay { get; set; } = true;

    // ---- the dial itself (sent as "cfg")
    public int Brightness { get; set; } = 80;          // 10..100
    public bool Sound { get; set; } = true;
    public int DimAfterSeconds { get; set; } = 30;     // 0 = never
    public int OffAfterSeconds { get; set; } = 300;    // 0 = never
    public double TurnSensitivity { get; set; } = 1.0; // 0.25..4
    public bool FlipTurn { get; set; }

    // ---- mouse
    public double TrackpadSpeed { get; set; } = 2.5;   // screen px per touch px, before acceleration
    public double PointerSpeed { get; set; } = 30;     // screen px per degree, before acceleration
    public bool MouseAcceleration { get; set; } = true;
    public double ScrollSpeed { get; set; } = 1.0;

    // ---- Claude (voice chat on the dial)
    public bool ClaudeEnabled { get; set; } = true;
    /// <summary>The Anthropic API key, encrypted with DPAPI for this Windows user (see <see cref="Secrets"/>).</summary>
    public string? ClaudeApiKeyProtected { get; set; }
    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    public string ClaudeSystemPrompt { get; set; } =
        "You are Claude, answering through Gyrodial: a small round screen on the user's desk. They speak to you and " +
        "read your reply on a 1.75-inch round display, so keep answers short: one to three plain sentences, no " +
        "markdown, no lists, no code blocks unless they ask for code.";
    public bool SpeakReplies { get; set; }
    /// <summary>Speech recognition language, e.g. "en-US". Empty = Windows display language.</summary>
    public string SpeechLanguage { get; set; } = "";

    // ---- Claude Code (approve permission prompts from the dial)
    public bool ClaudeCodeEnabled { get; set; } = true;
    public int ClaudeCodePort { get; set; } = 47825;
    /// <summary>Seconds the dial waits for an answer before Claude Code falls back to its own prompt.</summary>
    public int ClaudeCodeTimeoutSeconds { get; set; } = 110;
    public bool ClaudeCodeNotifications { get; set; } = true;

    // ---- DialKit and games (dialkit-watt pages on localhost)
    public bool DialKitEnabled { get; set; } = true;

    // ---- Microsoft Teams (meeting mute from the dial)
    public bool TeamsEnabled { get; set; }
    /// <summary>The pairing token Teams hands out the first time (Teams → Settings → Privacy → Third-party
    /// app API), encrypted with <see cref="Secrets.Protect"/>. A plain token from older builds is
    /// re-saved encrypted on first use.</summary>
    public string? TeamsToken { get; set; }
}
