using System.Globalization;
using System.Runtime.InteropServices;
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace SoundBridge.Bluetooth;

/// <summary>
/// Tells whether a phone is still within Bluetooth reach by asking it for its list of services afresh.
/// <para>
/// The uncached query makes Windows page the phone over the air, which only a phone that is in range with
/// Bluetooth on can answer. It opens no profile: no audio moves, and nothing changes on the phone. A phone that
/// has just disconnected this PC on purpose still answers; one carried out of range does not.
/// </para>
/// </summary>
internal sealed class BluetoothPhoneReachability(ILog log) : IPhoneReachability
{
    private const string Category = "Bluetooth";

    // HRESULTs Windows uses when the remote device does not answer a page.
    private const int DeviceNotConnected = unchecked((int)0x8007048F);
    private const int SemaphoreTimeout = unchecked((int)0x80070079);
    private const int TimedOut = unchecked((int)0x800705B4);

    public async Task<PhoneReachability> ProbeAsync(AudioSource device, CancellationToken cancellationToken)
    {
        try
        {
            var address = await BluetoothAddresses.TryGetAsync(device.Id, cancellationToken).ConfigureAwait(false);
            if (address is null)
            {
                log.Warn(Category, $"Windows gave no Bluetooth address for '{device.Name}'; cannot check whether it is in range.");
                return PhoneReachability.Unknown;
            }

            using var phone = await BluetoothDevice.FromBluetoothAddressAsync(address.Value).AsTask(cancellationToken).ConfigureAwait(false);
            if (phone is null)
            {
                return PhoneReachability.Unknown;
            }

            var result = await phone.GetRfcommServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            log.Info(Category, $"Reachability check for '{device.Name}': {result.Error}, {result.Services.Count} service(s).");
            return result.Error switch
            {
                BluetoothError.Success => PhoneReachability.Reachable,
                BluetoothError.DeviceNotConnected => PhoneReachability.Unreachable,
                _ => PhoneReachability.Unknown,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.HResult is DeviceNotConnected or SemaphoreTimeout or TimedOut)
        {
            log.Info(Category, $"Reachability check for '{device.Name}': no answer (0x{ex.HResult:X8}).");
            return PhoneReachability.Unreachable;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            log.Warn(Category, $"Reachability check for '{device.Name}' failed: {ex.Message.ReplaceLineEndings(" ").Trim()} (0x{ex.HResult:X8})");
            return PhoneReachability.Unknown;
        }
    }
}

/// <summary>Finds the Bluetooth address behind an audio source's device id.</summary>
internal static class BluetoothAddresses
{
    private const string AddressProperty = "System.DeviceInterface.Bluetooth.DeviceAddress";

    public static async Task<ulong?> TryGetAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var info = await DeviceInformation.CreateFromIdAsync(deviceId, [AddressProperty]).AsTask(cancellationToken).ConfigureAwait(false);
        return info.Properties.TryGetValue(AddressProperty, out var value) &&
            value is string hex &&
            ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
                ? address
                : null;
    }
}
