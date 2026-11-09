using System.IO.Ports;
using Microsoft.Win32;
using Puck.Core;

namespace Puck.App.Transport;

/// <summary>A serial port that is present right now, and whether it is the ESP32-S3's own USB.</summary>
public sealed record SerialPortInfo(string Port, bool IsEspressif)
{
    public override string ToString() => IsEspressif ? $"{Port} (ESP32-S3 USB)" : Port;
}

/// <summary>
/// Finds the puck's COM port: the ESP32-S3 USB-Serial-JTAG device, VID 303A / PID 1001.
/// Uses the registry instead of WMI: Enum\USB keeps the port name of every such device ever plugged
/// in, and SERIALCOMM (via GetPortNames) says which ports exist right now. Cheap enough for every 2 s.
/// </summary>
public static class UsbPorts
{
    static readonly string DeviceKeyPrefix = $"VID_{Protocol.UsbVid:X4}&PID_{Protocol.UsbPid:X4}";

    public static IReadOnlyList<string> FindPuckPorts()
    {
        var present = new HashSet<string>(SafePortNames(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var port in EspressifPortNames())
            if (present.Contains(port) && !result.Contains(port, StringComparer.OrdinalIgnoreCase))
                result.Add(port);
        result.Sort(ComparePorts);
        return result;
    }

    /// <summary>Every present port, Espressif ones first (for the firmware installer's picker).</summary>
    public static IReadOnlyList<SerialPortInfo> AllPorts()
    {
        var esp = new HashSet<string>(FindPuckPorts(), StringComparer.OrdinalIgnoreCase);
        return SafePortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new SerialPortInfo(p, esp.Contains(p)))
            .OrderByDescending(p => p.IsEspressif)
            .ThenBy(p => p.Port, Comparer<string>.Create(ComparePorts))
            .ToList();
    }

    static string[] SafePortNames()
    {
        try { return SerialPort.GetPortNames(); }
        catch (Exception) { return []; }
    }

    static IEnumerable<string> EspressifPortNames()
    {
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) yield break;
        foreach (var device in usb.GetSubKeyNames())
        {
            // "VID_303A&PID_1001" and its interfaces, e.g. "VID_303A&PID_1001&MI_00"
            if (!device.StartsWith(DeviceKeyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            using var deviceKey = usb.OpenSubKey(device);
            if (deviceKey is null) continue;
            foreach (var instance in deviceKey.GetSubKeyNames())
            {
                using var parameters = deviceKey.OpenSubKey($@"{instance}\Device Parameters");
                if (parameters?.GetValue("PortName") is string port && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    yield return port;
            }
        }
    }

    // COM2 before COM10
    static int ComparePorts(string? a, string? b)
    {
        static int Num(string? s) => s is not null && s.Length > 3 && int.TryParse(s.AsSpan(3), out var n) ? n : int.MaxValue;
        var c = Num(a).CompareTo(Num(b));
        return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
