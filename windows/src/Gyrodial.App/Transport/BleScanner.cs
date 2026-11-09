using Gyrodial.Core;
using Windows.Devices.Bluetooth.Advertisement;

namespace Gyrodial.App.Transport;

/// <summary>A dial seen advertising.</summary>
public sealed record BleSighting(ulong Address, string Name, short Rssi);

/// <summary>
/// Listens for dials advertising. Active scanning, because the dial puts its 128-bit service UUID in
/// the advertisement and its "Gyrodial-XXXX" name in the scan response (both don't fit in 31 bytes);
/// Windows reports them as two separate Received events, so names are remembered per address.
/// </summary>
public sealed class BleScanner : IDisposable
{
    readonly BluetoothLEAdvertisementWatcher _watcher;
    readonly Dictionary<ulong, string> _names = [];
    readonly HashSet<ulong> _dials = [];
    readonly object _gate = new();
    DateTime _retryAt = DateTime.MinValue;

    /// <summary>Raised (on a Bluetooth thread) for each advertisement from a dial.</summary>
    public event Action<BleSighting>? Seen;

    public bool Running => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    /// <summary>Why scanning isn't happening (radio off, no adapter), or null.</summary>
    public string? Problem { get; private set; }

    public BleScanner()
    {
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) =>
        {
            if (e.Error != Windows.Devices.Bluetooth.BluetoothError.Success)
            {
                Problem = e.Error switch
                {
                    Windows.Devices.Bluetooth.BluetoothError.RadioNotAvailable => "Bluetooth is off",
                    Windows.Devices.Bluetooth.BluetoothError.DisabledByPolicy or Windows.Devices.Bluetooth.BluetoothError.DisabledByUser => "Bluetooth is disabled",
                    _ => $"Bluetooth scan stopped ({e.Error})",
                };
                _retryAt = DateTime.UtcNow.AddSeconds(10);
            }
        };
    }

    /// <summary>Starts or stops scanning; call periodically (it retries after errors).</summary>
    public void SetActive(bool active)
    {
        try
        {
            var status = _watcher.Status;
            if (active && status != BluetoothLEAdvertisementWatcherStatus.Started && status != BluetoothLEAdvertisementWatcherStatus.Stopping)
            {
                if (DateTime.UtcNow < _retryAt) return;
                _watcher.Start();
                if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) Problem = null;
            }
            else if (!active && status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Stop();
            }
        }
        catch (Exception e)
        {
            // No Bluetooth adapter at all, or the radio is in a bad state
            Problem = "Bluetooth unavailable";
            _retryAt = DateTime.UtcNow.AddSeconds(30);
            AppLog.Instance.Debug("ble", $"scan: {e.Message}");
        }
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var adv = args.Advertisement;
        bool hasService = adv.ServiceUuids.Contains(Protocol.ServiceUuid);
        string name = adv.LocalName ?? "";
        bool isDial;
        lock (_gate)
        {
            // Only dials' names are kept: every phone, watch and TV in range advertises one
            if (Protocol.IsDialName(name)) _names[args.BluetoothAddress] = name;
            else if (name.Length == 0) _names.TryGetValue(args.BluetoothAddress, out name!);
            name ??= "";
            if (hasService || Protocol.IsDialName(name)) _dials.Add(args.BluetoothAddress);
            isDial = _dials.Contains(args.BluetoothAddress);
        }
        if (isDial) Seen?.Invoke(new BleSighting(args.BluetoothAddress, name, args.RawSignalStrengthInDBm));
    }

    public void Dispose()
    {
        try { _watcher.Stop(); } catch (Exception) { }
        _watcher.Received -= OnReceived;
    }
}
