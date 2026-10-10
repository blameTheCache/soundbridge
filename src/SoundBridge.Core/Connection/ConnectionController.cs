using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Core.Connection;

/// <summary>
/// Owns the single audio link and the reconnection policy around it.
/// <para>
/// All state lives behind one lock. Each open attempt and each retry timer belongs to an "activity"; any
/// change of intent (connect, disconnect, Bluetooth off, sleep) retires the current activity, so a late
/// callback from an abandoned attempt or timer is recognised as stale and ignored instead of rewriting
/// the state.
/// </para>
/// <para>
/// Automatic reconnection only ever follows a connection that was established, or an automatic connect at
/// session start. <see cref="Disconnect"/> ends that: nothing reconnects afterwards until
/// <see cref="Connect"/> is called again.
/// </para>
/// </summary>
public sealed class ConnectionController : IDisposable
{
    private const string Category = "Connection";

    private readonly Lock _gate = new();
    private readonly IAudioLinkFactory _factory;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly List<Attempt> _retired = [];

    private ReconnectPolicy _policy;
    private ConnectionSnapshot _current = ConnectionSnapshot.Idle;
    private ConnectionSnapshot _lastRaised = ConnectionSnapshot.Idle;
    private AudioSource? _device;
    private Attempt? _attempt;
    private ITimer? _retryTimer;
    private long _epoch;
    private bool _recoverable;
    private bool _bluetoothOn = true;
    private bool _resumeOnWake;
    private bool _disposed;

    public ConnectionController(IAudioLinkFactory factory, ReconnectPolicy policy, TimeProvider? time = null, ILog? log = null)
    {
        _factory = factory;
        _policy = policy;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>
    /// Raised after the state changes, on whichever thread caused it. Handlers should read
    /// <see cref="Current"/> rather than assume one event per transition.
    /// </summary>
    public event EventHandler? Changed;

    public ConnectionSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Starts connecting to <paramref name="device"/>, replacing any link to a different device.
    /// Returns false when the request is a duplicate of an attempt or connection already in place.
    /// </summary>
    public bool Connect(AudioSource device, ConnectOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(device);

        Attempt? toStart = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var sameDevice = _device is not null && string.Equals(_device.Id, device.Id, StringComparison.Ordinal);
            if (sameDevice && _current.Phase is ConnectionPhase.Connecting or ConnectionPhase.Connected)
            {
                _log.Info(Category, $"Ignored duplicate connect request for '{device.Name}' while {_current.Phase}.");
                return false;
            }

            _log.Info(Category, $"Connect requested ({origin}) for '{device.Name}' [{device.Id}].");
            StopActivityLocked();
            _device = device;
            _resumeOnWake = false;
            _recoverable = origin == ConnectOrigin.SessionStart;

            if (_bluetoothOn)
            {
                toStart = BeginAttemptLocked(retry: 0);
            }
            else
            {
                HoldForBluetoothLocked();
            }
        }

        Flush(toStart);
        return true;
    }

    /// <summary>
    /// Intentional disconnect. Cancels any attempt or pending retry and suppresses automatic reconnection
    /// until the next <see cref="Connect"/>.
    /// </summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _recoverable = false;
            _resumeOnWake = false;
            if (_current.Phase == ConnectionPhase.Disconnected)
            {
                return;
            }

            _log.Info(Category, $"Disconnect requested while {_current.Phase}; automatic reconnection is off until the next connect.");
            StopActivityLocked();
            SetLocked(new ConnectionSnapshot { Phase = ConnectionPhase.Disconnected, Device = _device });
        }

        Flush(null);
    }

    public void SetPolicy(ReconnectPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _policy = policy;
            if (!policy.Enabled)
            {
                _resumeOnWake = false;
                if (_current.Phase is ConnectionPhase.WaitingToRetry or ConnectionPhase.WaitingForBluetooth)
                {
                    _log.Info(Category, "Automatic reconnection was turned off; pending reconnection cancelled.");
                    var failure = _current.Failure == ConnectionFailure.None ? ConnectionFailure.ConnectionLost : _current.Failure;
                    var detail = _current.FailureDetail;
                    StopActivityLocked();
                    FailLocked(failure, detail, exhausted: false);
                }
            }
        }

        Flush(null);
    }

    /// <summary>Reports the Bluetooth radio state. Assumed on until told otherwise.</summary>
    public void SetBluetoothAvailable(bool available)
    {
        lock (_gate)
        {
            if (_disposed || _bluetoothOn == available)
            {
                return;
            }

            _bluetoothOn = available;
            _log.Info(Category, $"Bluetooth radio is {(available ? "on" : "off")}.");

            if (!available)
            {
                if (_current.Phase is ConnectionPhase.Connecting or ConnectionPhase.Connected or ConnectionPhase.WaitingToRetry)
                {
                    StopActivityLocked();
                    HoldForBluetoothLocked();
                }
            }
            else if (_current.Phase == ConnectionPhase.WaitingForBluetooth)
            {
                ScheduleRetryLocked(1, ConnectionFailure.None, null);
            }
            else if (_current is { Phase: ConnectionPhase.Failed, Failure: ConnectionFailure.BluetoothOff })
            {
                SetLocked(new ConnectionSnapshot { Phase = ConnectionPhase.Disconnected, Device = _device });
            }
        }

        Flush(null);
    }

    /// <summary>The PC is about to sleep. The link is released; it is restored by <see cref="OnResumed"/> when allowed.</summary>
    public void OnSuspending()
    {
        lock (_gate)
        {
            if (_disposed || !_current.IsActive)
            {
                return;
            }

            _resumeOnWake = _recoverable && _policy.Enabled;
            _log.Info(Category, $"Suspending while {_current.Phase}; reconnect on resume: {_resumeOnWake}.");
            StopActivityLocked();
            SetLocked(new ConnectionSnapshot { Phase = ConnectionPhase.Disconnected, Device = _device });
        }

        Flush(null);
    }

    /// <summary>The PC woke up. Resumes only what sleep interrupted; a manual disconnect before sleep stays disconnected.</summary>
    public void OnResumed()
    {
        lock (_gate)
        {
            if (_disposed || !_resumeOnWake)
            {
                return;
            }

            _resumeOnWake = false;
            if (_current.Phase != ConnectionPhase.Disconnected || _device is null || !_policy.Enabled)
            {
                return;
            }

            _log.Info(Category, "Resumed from sleep; restoring the connection that sleep interrupted.");
            _recoverable = true;
            if (_bluetoothOn)
            {
                ScheduleRetryLocked(1, ConnectionFailure.None, null);
            }
            else
            {
                HoldForBluetoothLocked();
            }
        }

        Flush(null);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Changed = null;
            StopActivityLocked();
        }

        Flush(null);
    }

    private Attempt? BeginAttemptLocked(int retry)
    {
        var device = _device!;
        IAudioLink link;
        try
        {
            link = _factory.Create(device);
        }
        catch (Exception ex)
        {
            _log.Error(Category, $"Could not create a link for '{device.Name}': {ex.Message}");
            HandleFailureLocked(ConnectionFailure.Unknown, ex.Message, retry);
            return null;
        }

        var attempt = new Attempt(++_epoch, link, retry);
        attempt.ClosedHandler = (_, _) => OnLinkClosed(attempt);
        link.Closed += attempt.ClosedHandler;
        _attempt = attempt;

        _log.Info(Category, retry == 0 ? $"Opening link to '{device.Name}'." : $"Opening link to '{device.Name}' (retry {retry} of {_policy.MaxRetries}).");
        SetLocked(new ConnectionSnapshot
        {
            Phase = ConnectionPhase.Connecting,
            Device = device,
            RetryAttempt = retry,
            MaxRetries = _policy.MaxRetries,
        });
        return attempt;
    }

    private async Task RunAttemptAsync(Attempt attempt)
    {
        LinkOpenResult result;
        try
        {
            result = await attempt.Link.OpenAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new LinkOpenResult(LinkOpenStatus.Failed, "The attempt was cancelled.");
        }
        catch (Exception ex)
        {
            result = new LinkOpenResult(LinkOpenStatus.Failed, ex.Message);
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_attempt, attempt))
            {
                // Superseded while in flight. Its link was already retired with the activity that owned it.
                _log.Info(Category, $"Ignored stale result ({result.Status}) from a superseded attempt.");
                return;
            }

            if (result.Status == LinkOpenStatus.Opened && !attempt.ClosedDuringOpen)
            {
                _recoverable = true;
                _log.Info(Category, $"Connected to '{_device!.Name}'.");
                SetLocked(new ConnectionSnapshot
                {
                    Phase = ConnectionPhase.Connected,
                    Device = _device,
                    MaxRetries = _policy.MaxRetries,
                    ConnectedAt = _time.GetLocalNow(),
                });
            }
            else
            {
                var failure = result.Status == LinkOpenStatus.Opened ? ConnectionFailure.ConnectionLost : Map(result.Status);
                _log.Warn(Category, $"Open failed: {failure}{(result.Detail is null ? string.Empty : " - " + result.Detail)}");
                RetireCurrentLocked();
                HandleFailureLocked(failure, result.Detail, attempt.Retry);
            }
        }

        Flush(null);
    }

    private void OnLinkClosed(Attempt attempt)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_attempt, attempt))
            {
                return;
            }

            if (_current.Phase == ConnectionPhase.Connecting)
            {
                // The open result has not been processed yet; remember that the link did not survive.
                attempt.ClosedDuringOpen = true;
                return;
            }

            if (_current.Phase != ConnectionPhase.Connected)
            {
                return;
            }

            _log.Warn(Category, $"Link to '{_device!.Name}' was closed by the phone or by Windows.");
            RetireCurrentLocked();
            HandleFailureLocked(ConnectionFailure.ConnectionLost, null, retriesDone: 0);
        }

        Flush(null);
    }

    private void OnRetryTimer(object? state)
    {
        var epoch = (long)state!;
        Attempt? toStart = null;
        lock (_gate)
        {
            if (epoch != _epoch || _current.Phase != ConnectionPhase.WaitingToRetry)
            {
                return;
            }

            _retryTimer?.Dispose();
            _retryTimer = null;

            if (_bluetoothOn)
            {
                toStart = BeginAttemptLocked(_current.RetryAttempt);
            }
            else
            {
                HoldForBluetoothLocked();
            }
        }

        Flush(toStart);
    }

    private void HandleFailureLocked(ConnectionFailure failure, string? detail, int retriesDone)
    {
        if (!_bluetoothOn || failure == ConnectionFailure.BluetoothOff)
        {
            HoldForBluetoothLocked();
            return;
        }

        // A denial is a decision by Windows, not a transient condition; repeating the request will not change it.
        var canRetry = _recoverable && _policy.Enabled && failure != ConnectionFailure.DeniedBySystem;
        if (canRetry && retriesDone < _policy.MaxRetries)
        {
            ScheduleRetryLocked(retriesDone + 1, failure, detail);
            return;
        }

        if (canRetry)
        {
            _log.Warn(Category, $"Giving up after {retriesDone} reconnection attempt(s).");
        }

        FailLocked(failure, detail, exhausted: canRetry);
    }

    private void ScheduleRetryLocked(int retry, ConnectionFailure failure, string? detail)
    {
        var delay = _policy.DelayBefore(retry);
        var epoch = ++_epoch;

        _log.Info(Category, $"Retry {retry} of {_policy.MaxRetries} in {delay.TotalSeconds:0.#} s.");
        SetLocked(new ConnectionSnapshot
        {
            Phase = ConnectionPhase.WaitingToRetry,
            Device = _device,
            RetryAttempt = retry,
            MaxRetries = _policy.MaxRetries,
            NextRetryAt = _time.GetLocalNow() + delay,
            Failure = failure,
            FailureDetail = detail,
        });
        _retryTimer = _time.CreateTimer(OnRetryTimer, epoch, delay, Timeout.InfiniteTimeSpan);
    }

    private void HoldForBluetoothLocked()
    {
        if (_recoverable && _policy.Enabled)
        {
            _log.Info(Category, "Bluetooth is off; waiting for it before reconnecting.");
            SetLocked(new ConnectionSnapshot
            {
                Phase = ConnectionPhase.WaitingForBluetooth,
                Device = _device,
                MaxRetries = _policy.MaxRetries,
                Failure = ConnectionFailure.BluetoothOff,
            });
        }
        else
        {
            FailLocked(ConnectionFailure.BluetoothOff, null, exhausted: false);
        }
    }

    private void FailLocked(ConnectionFailure failure, string? detail, bool exhausted)
    {
        _recoverable = false;
        SetLocked(new ConnectionSnapshot
        {
            Phase = ConnectionPhase.Failed,
            Device = _device,
            MaxRetries = _policy.MaxRetries,
            Failure = failure,
            FailureDetail = detail,
            RetriesExhausted = exhausted,
        });
    }

    /// <summary>Ends the current activity: invalidates its callbacks, cancels its timer and queues its link for disposal.</summary>
    private void StopActivityLocked()
    {
        _epoch++;
        _retryTimer?.Dispose();
        _retryTimer = null;
        RetireCurrentLocked();
    }

    private void RetireCurrentLocked()
    {
        if (_attempt is { } attempt)
        {
            _attempt = null;
            _retired.Add(attempt);
        }
    }

    private void SetLocked(ConnectionSnapshot snapshot) => _current = snapshot;

    /// <summary>
    /// Runs everything that must happen outside the lock, in order: dispose retired links (so an old link is
    /// closed before a new one opens), notify listeners, then start the next attempt.
    /// </summary>
    private void Flush(Attempt? toStart)
    {
        Attempt[] retired;
        bool changed;
        lock (_gate)
        {
            retired = [.. _retired];
            _retired.Clear();
            changed = !ReferenceEquals(_current, _lastRaised);
            _lastRaised = _current;
        }

        foreach (var attempt in retired)
        {
            attempt.Retire(_log);
        }

        if (changed)
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _log.Error(Category, $"A state listener threw: {ex}");
            }
        }

        if (toStart is not null)
        {
            _ = RunAttemptAsync(toStart);
        }
    }

    private static ConnectionFailure Map(LinkOpenStatus status) => status switch
    {
        LinkOpenStatus.TimedOut => ConnectionFailure.TimedOut,
        LinkOpenStatus.DeniedBySystem => ConnectionFailure.DeniedBySystem,
        LinkOpenStatus.DeviceUnavailable => ConnectionFailure.DeviceUnavailable,
        _ => ConnectionFailure.Unknown,
    };

    private sealed class Attempt(long epoch, IAudioLink link, int retry)
    {
        private readonly CancellationTokenSource _cancellation = new();

        public long Epoch { get; } = epoch;

        public IAudioLink Link { get; } = link;

        public int Retry { get; } = retry;

        public CancellationToken Token => _cancellation.Token;

        public EventHandler? ClosedHandler { get; set; }

        public bool ClosedDuringOpen { get; set; }

        public void Retire(ILog log)
        {
            try
            {
                Link.Closed -= ClosedHandler;
                _cancellation.Cancel();
                Link.Dispose();
            }
            catch (Exception ex)
            {
                log.Warn(Category, $"Releasing a link failed: {ex.Message}");
            }
        }
    }
}
