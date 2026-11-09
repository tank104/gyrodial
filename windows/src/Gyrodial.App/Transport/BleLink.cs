using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Gyrodial.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation.Metadata;

namespace Gyrodial.App.Transport;

/// <summary>
/// The dial over Bluetooth LE: the Gyrodial GATT service, writing to RX and listening to TX notifications.
/// No OS pairing: Windows connects to the unbonded peripheral directly, and the app-level pairing in
/// the protocol is what makes the dial trust this PC.
/// </summary>
public sealed class BleLink : DialLink
{
    readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    BluetoothLEDevice? _device;
    GattSession? _session;
    GattDeviceService? _service;
    GattCharacteristic? _rx;
    GattCharacteristic? _tx;
    object? _connectionRequest;  // BluetoothLEPreferredConnectionParametersRequest: dropping it cancels the request
    int _maxPdu = 23;

    public ulong Address { get; }
    public string AdvertisedName { get; }
    public override string Kind => "ble";
    public override string Description => $"BLE {FormatAddress(Address)}";

    BleLink(ulong address, string advertisedName)
    {
        Address = address;
        AdvertisedName = advertisedName;
    }

    public static string FormatAddress(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    /// <summary>Connects and subscribes. Throws (and cleans up) if the dial isn't reachable.</summary>
    public static async Task<BleLink> ConnectAsync(ulong address, string advertisedName, CancellationToken ct)
    {
        var link = new BleLink(address, advertisedName);
        try
        {
            await link.ConnectCoreAsync(ct);
            _ = link.WriteLoopAsync();
            return link;
        }
        catch
        {
            link.CloseTransport();
            throw;
        }
    }

    async Task ConnectCoreAsync(CancellationToken ct)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(Address).AsTask(ct)
            ?? throw new InvalidOperationException("device not found");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Keep the link up while we're idle; Windows otherwise drops unbonded GATT links it thinks unused.
        // Then wait for the session to be Active: discovery on a link that isn't up yet fails.
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct);
        _session.MaxPduSizeChanged += (s, _) => Volatile.Write(ref _maxPdu, s.MaxPduSize);
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _session.SessionStatusChanged += (_, e) => { if (e.Status == GattSessionStatus.Active) active.TrySetResult(); };
        _session.MaintainConnection = true;
        if (_session.SessionStatus == GattSessionStatus.Active) active.TrySetResult();
        if (await Task.WhenAny(active.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)) != active.Task)
            throw new TimeoutException("the dial didn't accept the connection");

        // All services, uncached, then pick ours. Asking for one UUID uncached
        // (GetGattServicesForUuidAsync) throws 0x80070016 "device does not recognize the command" for
        // an unbonded dial on Windows 11 (seen on a real Gyrodial), while full discovery works. Uncached
        // because Windows caches GATT tables per address, and a reflashed dial may have changed.
        _service = null;
        for (int attempt = 0; attempt < 3 && _service is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(400, ct);
            var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (services.Status != GattCommunicationStatus.Success) continue;
            foreach (var s in services.Services)
            {
                if (_service is null && s.Uuid == Protocol.ServiceUuid) _service = s;
                else s.Dispose();
            }
        }
        if (_service is null) throw new InvalidOperationException("Gyrodial service not found");

        var chars = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (chars.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"characteristics: {chars.Status}");
        _rx = chars.Characteristics.FirstOrDefault(c => c.Uuid == Protocol.RxUuid) ?? throw new InvalidOperationException("RX characteristic missing");
        _tx = chars.Characteristics.FirstOrDefault(c => c.Uuid == Protocol.TxUuid) ?? throw new InvalidOperationException("TX characteristic missing");

        _tx.ValueChanged += OnValueChanged;
        var cccd = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        if (cccd != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"subscribe: {cccd}");

        _maxPdu = _session.MaxPduSize;
        RequestFastConnection();
    }

    /// <summary>
    /// Windows 11 22H2+ lets an app ask for a short connection interval; without it Windows keeps
    /// ~30–50 ms, which makes the pointer feel sticky. Not available on Windows 10: skip quietly.
    /// </summary>
    void RequestFastConnection()
    {
        try
        {
            if (_device is null) return;
            if (!ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "RequestPreferredConnectionParameters")) return;
            _connectionRequest = _device.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
        }
        catch (Exception e)
        {
            AppLog.Instance.Debug("ble", $"connection parameters not set: {e.Message}");
        }
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            CloseTransport();
            OnClosed("disconnected");
        }
    }

    void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (IsClosed) return;
        var data = args.CharacteristicValue.ToArray();
        OnBytes(data);
    }

    protected override void SendBytes(byte[] data)
    {
        if (!IsClosed) _outbox.Writer.TryWrite(data);
    }

    async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var data in _outbox.Reader.ReadAllAsync())
            {
                var rx = _rx;
                if (rx is null || IsClosed) break;
                // ATT payload = MTU - 3, and never over 512 (the longest attribute value BLE allows;
                // NimBLE rejects longer writes). The dial reassembles lines across writes.
                int chunk = Math.Clamp(Volatile.Read(ref _maxPdu) - 3, 20, 512);
                for (int off = 0; off < data.Length; off += chunk)
                {
                    int n = Math.Min(chunk, data.Length - off);
                    var result = await rx.WriteValueWithResultAsync(data.AsBuffer(off, n), GattWriteOption.WriteWithoutResponse);
                    if (result.Status != GattCommunicationStatus.Success)
                        throw new InvalidOperationException($"write: {result.Status}");
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            CloseTransport();
            OnClosed(e.Message);
        }
    }

    protected override void CloseTransport()
    {
        _outbox.Writer.TryComplete();
        var tx = Interlocked.Exchange(ref _tx, null);
        if (tx is not null) tx.ValueChanged -= OnValueChanged;
        _rx = null;
        (_connectionRequest as IDisposable)?.Dispose();
        _connectionRequest = null;
        Interlocked.Exchange(ref _service, null)?.Dispose();
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            try { session.MaintainConnection = false; } catch (Exception) { }
            session.Dispose();
        }
        var device = Interlocked.Exchange(ref _device, null);
        if (device is not null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            device.Dispose();  // the last reference going away is what makes Windows drop the link
        }
    }
}
