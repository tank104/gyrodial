namespace Gyrodial.Core;

/// <summary>What the app knows about the dial it's talking to.</summary>
public sealed record DialInfo(
    string Id,
    string Name,
    string Firmware,
    string Transport,   // "usb" or "ble"
    int Battery,        // percent, -1 when there's no battery
    bool Charging,
    bool UsbPowered,
    string Face);

/// <summary>
/// The connection to the dial, as the integrations see it. Implemented by the app.
/// Messages arrive on a background thread; use <see cref="IIntegrationHost.RunOnUi"/> for UI work.
/// </summary>
public interface IDialHub
{
    /// <summary>A paired dial is connected and talking.</summary>
    bool IsConnected { get; }

    /// <summary>The connected dial, or null.</summary>
    DialInfo? Dial { get; }

    /// <summary>The face the dial is showing ("home", "claude", ...), or null when disconnected.</summary>
    string? CurrentFace { get; }

    /// <summary>Sends a message (dropped while disconnected).</summary>
    void Send(DialMessage message);

    /// <summary>Sends <c>DialMessage.Create(type, fields)</c>.</summary>
    void Send(string type, object? fields = null);

    /// <summary>Every message from the paired dial.</summary>
    event EventHandler<DialMessage>? MessageReceived;

    /// <summary>Raised with the new <see cref="IsConnected"/>.</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Raised when <see cref="Dial"/> or <see cref="CurrentFace"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Calls <paramref name="handler"/> for messages of one type. Dispose to stop.</summary>
    IDisposable On(string type, Action<DialMessage> handler);
}

/// <summary>Services the app gives every integration.</summary>
public interface IIntegrationHost
{
    SettingsStore Settings { get; }

    /// <summary>Writes a line to the app's log (Settings → Log, and %AppData%\Gyrodial\logs).</summary>
    void Log(string source, string message);

    /// <summary>A Windows notification from the tray icon.</summary>
    void ShowNotification(string title, string message);

    /// <summary>Runs on the app's UI (WPF dispatcher) thread.</summary>
    void RunOnUi(Action action);
}

/// <summary>A button an integration shows in Settings (e.g. "Install Claude Code hook").</summary>
/// <param name="Run">Returns a message to show the user when it's done, or null.</param>
public sealed record IntegrationAction(string Label, Func<Task<string?>> Run);

/// <summary>
/// One feature that connects the dial to something on the PC (Claude, Claude Code, DialKit, Teams).
/// Created by <c>Gyrodial.Integrations.IntegrationCatalog.CreateAll()</c>, started once by the app, and
/// disposed on exit. It must cope with the dial connecting and disconnecting at any time, and with
/// its own setting being switched off and on (watch <see cref="SettingsStore.Changed"/>).
/// </summary>
public interface IDialIntegration : IDisposable
{
    /// <summary>Stable id: "claude", "claude-code", "dialkit", "teams".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>One line for Settings, e.g. "Listening on 127.0.0.1:47825" or "Not configured".</summary>
    string Status { get; }

    event EventHandler? StatusChanged;

    /// <summary>Buttons for Settings.</summary>
    IReadOnlyList<IntegrationAction> Actions { get; }

    void Start(IDialHub hub, IIntegrationHost host);
}
