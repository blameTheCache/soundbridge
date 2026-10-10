using System.Globalization;
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace SoundBridge.Bluetooth;

/// <summary>
/// Reports when the selected phone establishes a Bluetooth connection to this PC, for any purpose. It is a
/// hint that the phone is back in range; it is read-only and never connects or disconnects anything.
/// </summary>
internal sealed class PhonePresenceMonitor(ILog log) : IDisposable
{
    private const string Category = "Bluetooth";
    private const string AddressProperty = "System.DeviceInterface.Bluetooth.DeviceAddress";

    private readonly Lock _gate = new();
    private BluetoothDevice? _device;
    private string? _watchedId;
    private int _generation;

    /// <summary>Raised on a background thread each time the phone's Bluetooth connection comes up.</summary>
    public event EventHandler? Seen;

    public async Task WatchAsync(AudioSource? phone)
    {
        BluetoothDevice? previous;
        int generation;
        lock (_gate)
        {
            if (phone?.Id == _watchedId)
            {
                return;
            }

            _watchedId = phone?.Id;
            generation = ++_generation;
            previous = _device;
            _device = null;
        }

        Release(previous);
        if (phone is null)
        {
            return;
        }

        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(phone.Id, [AddressProperty]).AsTask().ConfigureAwait(false);
            if (!info.Properties.TryGetValue(AddressProperty, out var value) ||
                value is not string hex ||
                !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                log.Warn(Category, $"Windows gave no Bluetooth address for '{phone.Name}'; its return will only be noticed by the periodic checks.");
                return;
            }

            var device = await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask().ConfigureAwait(false);
            if (device is null)
            {
                return;
            }

            lock (_gate)
            {
                if (generation != _generation)
                {
                    device.Dispose();
                    return;
                }

                _device = device;
            }

            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            log.Info(Category, $"Watching for '{phone.Name}' on Bluetooth (currently {device.ConnectionStatus}).");
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Could not watch for '{phone.Name}': {ex.Message} (0x{ex.HResult:X8})");
        }
    }

    public void Dispose()
    {
        BluetoothDevice? device;
        lock (_gate)
        {
            _generation++;
            _watchedId = null;
            device = _device;
            _device = null;
        }

        Release(device);
    }

    private void OnConnectionStatusChanged(BluetoothDevice sender, object args)
    {
        try
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                Seen?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Reading the phone's Bluetooth status failed: {ex.Message}");
        }
    }

    private void Release(BluetoothDevice? device)
    {
        if (device is null)
        {
            return;
        }

        try
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            device.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
