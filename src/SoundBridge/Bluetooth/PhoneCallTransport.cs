using System.Collections.Concurrent;
using System.Runtime.Versioning;
using SoundBridge.Core.Calls;
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;
using SoundBridge.Platform;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;

namespace SoundBridge.Bluetooth;

/// <summary>
/// The Bluetooth hands-free role for one phone, through <see cref="PhoneLineTransportDevice"/>.
/// <para>
/// Registering makes the phone treat this PC as a hands-free device. Windows then carries a call's audio
/// between the phone and the PC's default communication speaker and microphone by itself; this class only
/// asks for a call to be moved here. No audio passes through this process.
/// </para>
/// <para>
/// Windows grants registration only to a packaged app whose manifest declares the
/// phoneLineTransportManagement capability and the windows.phoneCallActivation extension. Without the
/// extension, RegisterApp returns normally but nothing is registered.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.22000.0")]
internal sealed class PhoneCallTransport(AudioSource phone, ILog log) : ICallTransport
{
    private const string Category = "Calls";
    private const string AddressProperty = "System.DeviceInterface.Bluetooth.DeviceAddress";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Guid, PhoneLine> _lines = new();
    private readonly ConcurrentDictionary<string, PhoneCall> _calls = new(StringComparer.Ordinal);
    private PhoneLineTransportDevice? _device;
    private PhoneLineWatcher? _watcher;
    private bool _disposed;

    public event EventHandler? Changed;

    public bool CanRouteToPc
    {
        get
        {
            try
            {
                return _device?.AudioRoutingStatus == TransportDeviceAudioRoutingStatus.CanRouteToLocalDevice;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public async Task<CallClaimResult> ClaimAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await FindTransportAsync().ConfigureAwait(false);
            if (info is null)
            {
                return new CallClaimResult(CallClaimStatus.PhoneNotFound);
            }

            var device = PhoneLineTransportDevice.FromId(info.Id);

            // RegisterApp throws unless access has been requested first.
            var access = await device.RequestAccessAsync().AsTask(cancellationToken).WaitAsync(Patience, cancellationToken).ConfigureAwait(false);
            if (access != DeviceAccessStatus.Allowed)
            {
                return new CallClaimResult(CallClaimStatus.AccessDenied, access.ToString());
            }

            if (!device.IsRegistered())
            {
                device.RegisterApp();
            }

            if (!device.IsRegistered())
            {
                return new CallClaimResult(CallClaimStatus.NotRegistered);
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    Unregister(device);
                    return new CallClaimResult(CallClaimStatus.Failed, "Call routing was switched off while it was being set up.");
                }

                _device = device;
            }

            device.AudioRoutingStatusChanged += OnRoutingChanged;

            var store = await PhoneCallManager.RequestStoreAsync().AsTask(cancellationToken).WaitAsync(Patience, cancellationToken).ConfigureAwait(false);
            var watcher = store.RequestLineWatcher();
            watcher.LineAdded += OnLineAdded;
            watcher.LineUpdated += OnLineAdded;
            watcher.LineRemoved += OnLineRemoved;
            _watcher = watcher;
            watcher.Start();
            PhoneCallManager.CallStateChanged += OnCallStateChanged;

            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new CallClaimResult(CallClaimStatus.Claimed);
        }
        catch (Exception ex)
        {
            Dispose();
            return new CallClaimResult(CallClaimStatus.Failed, Describe(ex));
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var device = _device;
        if (device is null)
        {
            return;
        }

        try
        {
            var connected = await device.ConnectAsync().AsTask(cancellationToken).WaitAsync(Patience, cancellationToken).ConfigureAwait(false);
            log.Info(Category, connected
                ? $"Hands-free link to '{phone.Name}' is up."
                : $"Hands-free link to '{phone.Name}' is not up yet; the phone will connect when it is in range.");
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Could not bring the hands-free link up: {Describe(ex)}");
        }
    }

    public async Task<IReadOnlyList<ActiveCall>> GetActiveCallsAsync(CancellationToken cancellationToken)
    {
        var active = new List<ActiveCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in _lines.Values)
        {
            try
            {
                var result = await line.GetAllActivePhoneCallsAsync().AsTask(cancellationToken).WaitAsync(Patience, cancellationToken).ConfigureAwait(false);
                foreach (var call in result.AllActivePhoneCalls)
                {
                    var status = call.Status;
                    if (status is PhoneCallStatus.Ended or PhoneCallStatus.Lost)
                    {
                        continue;
                    }

                    Track(call);
                    seen.Add(call.CallId);
                    active.Add(new ActiveCall(call.CallId, Map(status), call.AudioDevice == PhoneCallAudioDevice.LocalDevice));
                }
            }
            catch (Exception ex)
            {
                log.Warn(Category, $"Could not read the calls on the phone's line: {Describe(ex)}");
            }
        }

        foreach (var id in _calls.Keys.Where(id => !seen.Contains(id)))
        {
            Forget(id);
        }

        return active;
    }

    public async Task<bool> MoveToPcAsync(string callId, CancellationToken cancellationToken)
    {
        if (!_calls.TryGetValue(callId, out var call))
        {
            return false;
        }

        try
        {
            var outcome = await call.ChangeAudioDeviceAsync(PhoneCallAudioDevice.LocalDevice).AsTask(cancellationToken).WaitAsync(Patience, cancellationToken).ConfigureAwait(false);
            if (outcome != PhoneCallOperationStatus.Succeeded)
            {
                log.Warn(Category, $"Windows answered the request to move the call with {outcome}.");
            }

            return outcome == PhoneCallOperationStatus.Succeeded;
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Moving the call failed: {Describe(ex)}");
            return false;
        }
    }

    /// <summary>Gives the hands-free role back and stops listening. Safe to call more than once.</summary>
    public void Dispose()
    {
        PhoneLineTransportDevice? device;
        PhoneLineWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            device = _device;
            watcher = _watcher;
            _device = null;
            _watcher = null;
        }

        PhoneCallManager.CallStateChanged -= OnCallStateChanged;
        foreach (var id in _calls.Keys)
        {
            Forget(id);
        }

        _lines.Clear();

        if (watcher is not null)
        {
            watcher.LineAdded -= OnLineAdded;
            watcher.LineUpdated -= OnLineAdded;
            watcher.LineRemoved -= OnLineRemoved;

            // Stopping the watcher can block; it must never hold up giving the role back or exiting.
            _ = Task.Run(() =>
            {
                try
                {
                    watcher.Stop();
                }
                catch (Exception)
                {
                }
            });
        }

        if (device is not null)
        {
            device.AudioRoutingStatusChanged -= OnRoutingChanged;
            Unregister(device);
        }
    }

    private async Task<DeviceInformation?> FindTransportAsync()
    {
        var candidates = await DeviceInformation
            .FindAllAsync(PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth), [AddressProperty])
            .AsTask().WaitAsync(Patience).ConfigureAwait(false);

        // The music source and the call transport are different services of the same phone; the Bluetooth
        // address is what ties them together.
        string? address = null;
        try
        {
            var source = await DeviceInformation.CreateFromIdAsync(phone.Id, [AddressProperty]).AsTask().WaitAsync(Patience).ConfigureAwait(false);
            address = source.Properties.TryGetValue(AddressProperty, out var value) ? value as string : null;
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Could not read the phone's Bluetooth address: {Describe(ex)}");
        }

        if (!string.IsNullOrEmpty(address))
        {
            return candidates.FirstOrDefault(candidate =>
                candidate.Properties.TryGetValue(AddressProperty, out var value) &&
                string.Equals(value as string, address, StringComparison.OrdinalIgnoreCase));
        }

        return candidates.FirstOrDefault(candidate => string.Equals(candidate.Name, phone.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    private async void OnLineAdded(PhoneLineWatcher sender, PhoneLineWatcherEventArgs args)
    {
        try
        {
            var device = _device;
            if (device is null)
            {
                return;
            }

            var line = await PhoneLine.FromIdAsync(args.LineId).AsTask().WaitAsync(Patience).ConfigureAwait(false);
            if (line.Transport != PhoneLineTransport.Bluetooth || !string.Equals(line.TransportDeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_lines.TryAdd(args.LineId, line))
            {
                log.Info(Category, $"Windows opened a phone line for '{phone.Name}'.");
            }
            else
            {
                _lines[args.LineId] = line;
            }

            RaiseChanged();
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Could not open the phone's line: {Describe(ex)}");
        }
    }

    private void OnLineRemoved(PhoneLineWatcher sender, PhoneLineWatcherEventArgs args)
    {
        if (_lines.TryRemove(args.LineId, out PhoneLine? removed))
        {
            log.Info(Category, $"The phone line for '{phone.Name}' went away.");
            RaiseChanged();
        }
    }

    private void OnCallStateChanged(object? sender, object args) => RaiseChanged();

    private void OnRoutingChanged(PhoneLineTransportDevice sender, object args) => RaiseChanged();

    private void OnCallChanged(PhoneCall sender, object args) => RaiseChanged();

    private void Track(PhoneCall call)
    {
        if (_calls.TryAdd(call.CallId, call))
        {
            call.StatusChanged += OnCallChanged;
            call.AudioDeviceChanged += OnCallChanged;
        }
    }

    private void Forget(string callId)
    {
        if (_calls.TryRemove(callId, out var call))
        {
            try
            {
                call.StatusChanged -= OnCallChanged;
                call.AudioDeviceChanged -= OnCallChanged;
            }
            catch (Exception)
            {
                // The call object may already be gone on the Windows side.
            }
        }
    }

    private void Unregister(PhoneLineTransportDevice device)
    {
        try
        {
            if (device.IsRegistered())
            {
                device.UnregisterApp();
            }

            log.Info(Category, $"Unregistered from '{phone.Name}'; this PC no longer acts as its hands-free device.");
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Unregistering from '{phone.Name}' failed: {Describe(ex)}");
        }
    }

    private void RaiseChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static CallPhase Map(PhoneCallStatus status) => status switch
    {
        PhoneCallStatus.Incoming => CallPhase.Incoming,
        PhoneCallStatus.Dialing => CallPhase.Dialing,
        PhoneCallStatus.Talking => CallPhase.Talking,
        PhoneCallStatus.Held => CallPhase.Held,
        _ => CallPhase.Other,
    };

    private static string Describe(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return $"{(message.Length == 0 ? ex.GetType().Name : message)} (0x{ex.HResult:X8})";
    }
}

internal sealed class PhoneCallTransportFactory(ILog log) : ICallTransportFactory
{
    private const string CallsContract = "Windows.ApplicationModel.Calls.CallsPhoneContract";

    public CallSupport Support { get; } = Detect();

    public ICallTransport Create(AudioSource phone) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? new PhoneCallTransport(phone, log)
            : throw new PlatformNotSupportedException("Call routing needs Windows 11.");

    private static CallSupport Detect()
    {
        // Contract version 6 carries PhoneCall and the request to move a call's audio.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !ApiInformation.IsApiContractPresent(CallsContract, 6))
        {
            return CallSupport.NeedsNewerWindows;
        }

        return AppIdentity.IsPackaged ? CallSupport.Available : CallSupport.NeedsInstalledApp;
    }
}
