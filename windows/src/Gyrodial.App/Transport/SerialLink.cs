using System.IO;
using System.IO.Ports;
using System.Threading.Channels;

namespace Gyrodial.App.Transport;

/// <summary>
/// The dial on its USB cable: the ESP32-S3's built-in USB-Serial-JTAG port. Native USB, so the baud
/// rate means nothing, but DTR/RTS do: esptool drives them to reset the chip into its bootloader,
/// so they must stay de-asserted or opening the port could reboot the dial.
///
/// Writes go through a queue drained by a thread of its own: a dial that stops reading (rebooting,
/// or the port taken over) must stall that thread, not the connection loop that queued the line.
/// </summary>
public sealed class SerialLink : DialLink
{
    // A few seconds of the busiest traffic (volume, now playing, an ask). A queue that fills up
    // means nobody is reading on the other end: the link is dropped.
    const int OutboxDepth = 256;

    readonly SerialPort _port;
    readonly Thread _reader;
    readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboxDepth)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,  // TryWrite fails instead of waiting: see SendBytes
    });

    public string PortName { get; }
    public override string Kind => "usb";
    public override string Description => PortName;

    SerialLink(string portName, SerialPort port)
    {
        PortName = portName;
        _port = port;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"Gyrodial {portName} reader" };
    }

    /// <summary>Opens the port. Throws if it can't (busy, gone).</summary>
    public static SerialLink Open(string portName)
    {
        var port = new SerialPort(portName, 115200)
        {
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 1000,
        };
        port.Open();
        var link = new SerialLink(portName, port);
        link._reader.Start();
        new Thread(link.WriteLoop) { IsBackground = true, Name = $"Gyrodial {portName} writer" }.Start();
        return link;
    }

    void ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            var stream = _port.BaseStream;
            while (!IsClosed)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                OnBytes(buf.AsSpan(0, n));
            }
            OnClosed("port closed");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            OnClosed(e.Message);
        }
        finally
        {
            CloseTransport();
        }
    }

    void WriteLoop()
    {
        try
        {
            var reader = _outbox.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var data))
                {
                    if (IsClosed) return;
                    _port.BaseStream.Write(data, 0, data.Length);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A write that times out means nobody on the other end is reading (the dial is
            // rebooting, or the port belongs to something else now): give the port up.
            CloseTransport();
            OnClosed(e.Message);
        }
    }

    protected override void SendBytes(byte[] data)
    {
        if (IsClosed) return;
        if (!_outbox.Writer.TryWrite(data))
        {
            CloseTransport();
            OnClosed("the dial stopped reading");
        }
    }

    protected override void CloseTransport()
    {
        _outbox.Writer.TryComplete();
        try { if (_port.IsOpen) _port.Close(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        _port.Dispose();
    }
}
