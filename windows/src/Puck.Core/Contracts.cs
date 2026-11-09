namespace Puck.Core;

/// <summary>What the app knows about the puck it's talking to.</summary>
public sealed record PuckInfo(
    string Id,
    string Name,
    string Firmware,
    string Transport,   // "usb" or "ble"
    int Battery,        // percent, -1 when there's no battery
    bool Charging,
    bool UsbPowered,
    string Face);

/// <summary>
/// The connection to the puck, as the integrations see it. Implemented by the app.
/// Messages arrive on a background thread; use <see cref="IIntegrationHost.RunOnUi"/> for UI work.
/// </summary>
public interface IPuckHub
{
    /// <summary>A paired puck is connected and talking.</summary>
    bool IsConnected { get; }

    /// <summary>The connected puck, or null.</summary>
    PuckInfo? Puck { get; }

    /// <summary>The face the puck is showing ("home", "claude", ...), or null when disconnected.</summary>
    string? CurrentFace { get; }

    /// <summary>Sends a message (dropped while disconnected).</summary>
    void Send(PuckMessage message);

    /// <summary>Sends <c>PuckMessage.Create(type, fields)</c>.</summary>
    void Send(string type, object? fields = null);

    /// <summary>Every message from the paired puck.</summary>
    event EventHandler<PuckMessage>? MessageReceived;

    /// <summary>Raised with the new <see cref="IsConnected"/>.</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Raised when <see cref="Puck"/> or <see cref="CurrentFace"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Calls <paramref name="handler"/> for messages of one type. Dispose to stop.</summary>
    IDisposable On(string type, Action<PuckMessage> handler);
}

/// <summary>Services the app gives every integration.</summary>
public interface IIntegrationHost
{
    SettingsStore Settings { get; }

    /// <summary>Writes a line to the app's log (Settings → Log, and %AppData%\Puck\logs).</summary>
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
/// One feature that connects the puck to something on the PC (Claude, Claude Code, DialKit, Teams).
/// Created by <c>Puck.Integrations.IntegrationCatalog.CreateAll()</c>, started once by the app, and
/// disposed on exit. It must cope with the puck connecting and disconnecting at any time, and with
/// its own setting being switched off and on (watch <see cref="SettingsStore.Changed"/>).
/// </summary>
public interface IPuckIntegration : IDisposable
{
    /// <summary>Stable id: "claude", "claude-code", "dialkit", "teams".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>One line for Settings, e.g. "Listening on 127.0.0.1:47825" or "Not configured".</summary>
    string Status { get; }

    event EventHandler? StatusChanged;

    /// <summary>Buttons for Settings.</summary>
    IReadOnlyList<IntegrationAction> Actions { get; }

    void Start(IPuckHub hub, IIntegrationHost host);
}
