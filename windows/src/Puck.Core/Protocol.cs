namespace Puck.Core;

/// <summary>Constants shared with the firmware (firmware/src/config.h, docs/PROTOCOL.md).</summary>
public static class Protocol
{
    public const int Version = 1;

    public static readonly Guid ServiceUuid = Guid.Parse("60bd0001-b6ce-41b2-9911-a4a7e64e01d9");
    public static readonly Guid RxUuid = Guid.Parse("60bd0002-b6ce-41b2-9911-a4a7e64e01d9");  // app → puck (write)
    public static readonly Guid TxUuid = Guid.Parse("60bd0003-b6ce-41b2-9911-a4a7e64e01d9");  // puck → app (notify)

    public const string BleNamePrefix = "Puck-";

    /// <summary>Espressif's USB-Serial-JTAG port (the ESP32-S3's own USB).</summary>
    public const int UsbVid = 0x303A;
    public const int UsbPid = 0x1001;

    public const int MaxLine = 1024;
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The local WebSocket that dialkit-watt pages connect to (fixed by that npm package).</summary>
    public const int DialKitPort = 47824;
    public const string DialKitPath = "/bridge";

    /// <summary>Message types, as they appear in "t".</summary>
    public static class T
    {
        // both ways
        public const string Hello = "hello", Ping = "ping", Pong = "pong", Pair = "pair", Claude = "claude",
            Dial = "dial", Game = "game", Call = "call", Face = "face", Focus = "focus";
        // app → puck
        public const string Unpair = "unpair", State = "state", Time = "time", Cfg = "cfg", Ask = "ask",
            AskEnd = "ask_end", Notify = "notify";
        // puck → app
        public const string Bat = "bat", Vol = "vol", Mute = "mute", Media = "media", Move = "mv", Scroll = "scroll",
            Click = "click", Btn = "btn", Ans = "ans", Log = "log";
    }

    /// <summary>Face ids, as in "face" messages.</summary>
    public static class Faces
    {
        public const string Home = "home", Trackpad = "trackpad", Pointer = "pointer", Claude = "claude", Allow = "allow",
            DialKit = "dialkit", Game = "game", Call = "call", Focus = "focus", Spin = "spin", Dice = "dice",
            Pong = "pong", Pair = "pair", Picker = "picker", Offline = "offline";
    }
}
