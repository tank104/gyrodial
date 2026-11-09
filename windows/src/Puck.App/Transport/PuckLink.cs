using System.Text;
using Puck.Core;

namespace Puck.App.Transport;

/// <summary>
/// One open connection to a puck (a COM port or a BLE GATT link) carrying NDJSON lines.
/// The transport fields are the link's own; the handshake fields are kept by
/// <see cref="ConnectionManager"/> (only touched from its loop).
/// </summary>
public abstract class PuckLink : IDisposable
{
    readonly LineSplitter _splitter = new();
    int _closed;

    /// <summary>"usb" or "ble".</summary>
    public abstract string Kind { get; }

    /// <summary>For the log: "COM5" or "BLE C4:5B:...".</summary>
    public abstract string Description { get; }

    /// <summary>A complete line arrived (raised on the transport's thread).</summary>
    public event Action<PuckLink, string>? LineReceived;

    /// <summary>The link closed (error, unplugged, disconnected, or Dispose). Raised once.</summary>
    public event Action<PuckLink, string>? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    // ---- handshake state (ConnectionManager's loop only)
    public DateTime OpenedAt { get; } = DateTime.UtcNow;
    public DateTime LastRx { get; set; } = DateTime.UtcNow;
    public DateTime LastHelloSent { get; set; } = DateTime.MinValue;
    public DateTime LastPingSent { get; set; } = DateTime.MinValue;
    public bool HelloReceived { get; set; }
    public bool Paired { get; set; }
    public string? PuckId { get; set; }
    public string? PuckName { get; set; }
    public string? Firmware { get; set; }
    public int Battery { get; set; } = -1;
    public bool Charging { get; set; }
    public bool UsbPowered { get; set; }
    public string Face { get; set; } = "home";
    /// <summary>Why this link was opened (BLE): a known puck, or open for pairing.</summary>
    public bool OpenedForPairing { get; set; }
    /// <summary>When we last answered a hello the puck sent on its own (rate limit).</summary>
    public DateTime LastHelloAnswered { get; set; } = DateTime.MinValue;
    /// <summary>When we last sent <c>pair a:start</c>.</summary>
    public DateTime LastPairStartSent { get; set; } = DateTime.MinValue;
    /// <summary>The puck said <c>pair s:start</c>: it is showing its numbers for this PC.</summary>
    public bool PairingShown { get; set; }
    /// <summary>Numbers dialled in so far (0..3), from <c>pair s:progress</c>.</summary>
    public int PairProgress { get; set; }
    /// <summary>The puck refuses to pair with this PC until then (UTC), from <c>pair s:refused</c>.</summary>
    public DateTime RefusedUntil { get; set; } = DateTime.MinValue;
    /// <summary>A hello came back as the answer to ours (not one the puck sent on its own).</summary>
    public bool HelloReplied { get; set; }
    /// <summary>The puck restarted while paired: once it knows us again, everything is sent afresh.</summary>
    public bool NeedsRelink { get; set; }

    /// <summary>Queues one message line (adds the newline). Never throws; a broken link closes itself.</summary>
    public void Send(string json) => SendBytes(Encoding.UTF8.GetBytes(json + "\n"));

    protected abstract void SendBytes(byte[] data);

    /// <summary>Feed raw bytes from the transport; raises <see cref="LineReceived"/> per line.</summary>
    protected void OnBytes(ReadOnlySpan<byte> data)
    {
        foreach (var line in _splitter.Push(data))
            LineReceived?.Invoke(this, line);
    }

    /// <summary>Marks the link closed and raises <see cref="Closed"/> (once). Call from the transport on failure.</summary>
    protected void OnClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { Closed?.Invoke(this, reason); }
        catch (Exception e) { AppLog.Instance.Error("link", "Closed handler failed", e); }
    }

    public void Dispose()
    {
        CloseTransport();
        OnClosed("closed");
        GC.SuppressFinalize(this);
    }

    protected abstract void CloseTransport();

    public override string ToString() => Description;
}

/// <summary>Splits a byte stream into UTF-8 lines on '\n', dropping runaway lines over the protocol limit.</summary>
public sealed class LineSplitter
{
    readonly List<byte> _buf = new(256);
    bool _overflow;

    public IEnumerable<string> Push(ReadOnlySpan<byte> data)
    {
        List<string>? lines = null;
        foreach (var b in data)
        {
            if (b == (byte)'\n')
            {
                if (!_overflow && _buf.Count > 0)
                {
                    var s = Encoding.UTF8.GetString(_buf.ToArray()).TrimEnd('\r');
                    if (s.Length > 0) (lines ??= []).Add(s);
                }
                _buf.Clear();
                _overflow = false;
            }
            else if (_buf.Count < Protocol.MaxLine * 4)
            {
                _buf.Add(b);
            }
            else
            {
                _overflow = true;
            }
        }
        return lines ?? (IEnumerable<string>)[];
    }
}
