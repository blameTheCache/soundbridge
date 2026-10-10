using SoundBridge.Core.Diagnostics;
using Windows.Devices.Radios;

namespace SoundBridge.Bluetooth;

internal enum BluetoothRadioState
{
    /// <summary>Not determined yet, or Windows would not say. Treated as available.</summary>
    Unknown,
    On,
    Off,
    NoAdapter,
}

/// <summary>Observes whether this PC's Bluetooth radio is switched on. Read-only: it never changes the radio.</summary>
internal sealed class BluetoothRadioMonitor(ILog log) : IDisposable
{
    private const string Category = "Bluetooth";

    private readonly Lock _gate = new();
    private Radio? _radio;
    private BluetoothRadioState _state = BluetoothRadioState.Unknown;

    /// <summary>Raised on a background thread when <see cref="State"/> changes.</summary>
    public event EventHandler? Changed;

    public BluetoothRadioState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Finds the Bluetooth radio (again). Safe to call repeatedly, for example after a refresh.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
            var bluetooth = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);

            Radio? previous;
            lock (_gate)
            {
                previous = _radio;
                _radio = bluetooth;
            }

            if (previous is not null)
            {
                previous.StateChanged -= OnRadioStateChanged;
            }

            if (bluetooth is not null)
            {
                bluetooth.StateChanged += OnRadioStateChanged;
            }

            Update(bluetooth is null ? BluetoothRadioState.NoAdapter : Map(bluetooth.State));
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Could not read the Bluetooth radio state: {ex.Message} (0x{ex.HResult:X8})");
            Update(BluetoothRadioState.Unknown);
        }
    }

    public void Dispose()
    {
        Radio? radio;
        lock (_gate)
        {
            radio = _radio;
            _radio = null;
        }

        if (radio is not null)
        {
            radio.StateChanged -= OnRadioStateChanged;
        }
    }

    private void OnRadioStateChanged(Radio sender, object args) => Update(Map(sender.State));

    private void Update(BluetoothRadioState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        log.Info(Category, $"Bluetooth radio state: {state}.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static BluetoothRadioState Map(RadioState state) => state switch
    {
        RadioState.On => BluetoothRadioState.On,
        RadioState.Off or RadioState.Disabled => BluetoothRadioState.Off,
        _ => BluetoothRadioState.Unknown,
    };
}
