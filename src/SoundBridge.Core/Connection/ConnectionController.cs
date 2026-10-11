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

    // Held while retired links are closed. Taken before _gate, never the other way round.
    private readonly Lock _closing = new();
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
    private bool _userDecided;
    private bool _disposed;
    private DateTimeOffset? _waitingSince;
    private DateTimeOffset _lastCheckEnded = DateTimeOffset.MinValue;
    private DateTimeOffset _connectedSince;
    private bool _connectedByRecovery;
    private int _unstableRecoveries;

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

            // The session's automatic connect is decided while the app starts, which takes a moment. If the
            // user has connected or disconnected by hand in that moment, their choice stands.
            if (origin == ConnectOrigin.SessionStart && _userDecided)
            {
                _log.Info(Category, $"Ignored the session's automatic connect to '{device.Name}': the connection was already changed by hand.");
                return false;
            }

            _userDecided |= origin == ConnectOrigin.Manual;

            var sameDevice = _device is not null && string.Equals(_device.Id, device.Id, StringComparison.Ordinal);
            if (sameDevice && _current.Phase is ConnectionPhase.Connecting or ConnectionPhase.Connected)
            {
                _log.Info(Category, $"Ignored duplicate connect request for '{device.Name}' while {_current.Phase}.");
                return false;
            }

            _log.Info(Category, $"Connect requested ({origin}) for '{device.Name}' [{device.Id}].");
            StopActivityLocked();
            _unstableRecoveries = 0;
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
            _userDecided = true;
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

    /// <summary>
    /// Replaces an established link with a fresh one. For the case where Windows still reports the link as
    /// open but has stopped playing its audio, which happens after a phone call. Does nothing unless a link
    /// is connected right now, so it can never undo a disconnect or interfere with an attempt in progress.
    /// </summary>
    public bool Reopen(string reason)
    {
        Attempt? toStart = null;
        lock (_gate)
        {
            if (_disposed || _current.Phase != ConnectionPhase.Connected || _device is null)
            {
                return false;
            }

            _log.Info(Category, $"Reopening the link to '{_device.Name}': {reason}.");
            StopActivityLocked();

            // The connection was established, so a failure here is recovered like any dropped link.
            _recoverable = true;
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
            if (policy.Enabled && !policy.KeepWaiting && _current.Phase == ConnectionPhase.WaitingForPhone)
            {
                _log.Info(Category, "Waiting for the phone was turned off; giving up.");
                var failure = _current.Failure;
                var detail = _current.FailureDetail;
                StopActivityLocked();
                FailLocked(failure, detail, exhausted: true);
            }

            if (!policy.Enabled)
            {
                _resumeOnWake = false;
                if (_current.Phase is ConnectionPhase.WaitingToRetry or ConnectionPhase.WaitingForBluetooth or ConnectionPhase.WaitingForPhone)
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
                if (_current.Phase is ConnectionPhase.Connecting or ConnectionPhase.Connected or ConnectionPhase.WaitingToRetry or ConnectionPhase.WaitingForPhone)
                {
                    // Anything that was in progress followed an established connection or a session start,
                    // or it would not be waiting; keep it recoverable.
                    var recoverable = _recoverable || _current.Phase == ConnectionPhase.WaitingForPhone;
                    StopActivityLocked();
                    _recoverable = recoverable;
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

    /// <summary>
    /// The phone showed up on Bluetooth. If the controller is waiting for it, check now rather than at the
    /// next scheduled time. Ignored in every other state.
    /// </summary>
    public void NotifyPhoneSeen()
    {
        Attempt? toStart = null;
        lock (_gate)
        {
            if (_disposed || _current.Phase != ConnectionPhase.WaitingForPhone || !_bluetoothOn)
            {
                return;
            }

            // The checks themselves make the phone appear for a moment; do not let that feed back.
            if (_time.GetLocalNow() - _lastCheckEnded < _policy.MinimumCheckGap)
            {
                return;
            }

            _log.Info(Category, $"'{_device!.Name}' was seen on Bluetooth; checking now.");
            _retryTimer?.Dispose();
            _retryTimer = null;
            toStart = BeginAttemptLocked(retry: 0, background: true);
        }

        Flush(toStart);
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

    private Attempt? BeginAttemptLocked(int retry, bool background = false)
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

        var attempt = new Attempt(++_epoch, link, retry, background);
        attempt.ClosedHandler = (_, _) => OnLinkClosed(attempt);
        link.Closed += attempt.ClosedHandler;
        _attempt = attempt;

        _log.Info(Category, background
            ? $"Checking whether '{device.Name}' is back."
            : retry == 0 ? $"Opening link to '{device.Name}'." : $"Opening link to '{device.Name}' (retry {retry} of {_policy.MaxRetries}).");
        SetLocked(new ConnectionSnapshot
        {
            Phase = ConnectionPhase.Connecting,
            Device = device,
            RetryAttempt = retry,
            MaxRetries = _policy.MaxRetries,
            BackgroundCheck = background,
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
                _waitingSince = null;
                _connectedSince = _time.GetLocalNow();
                _connectedByRecovery = attempt.Retry > 0 || attempt.Background;
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
                RetireCurrentLocked();
                if (attempt.Background && failure == ConnectionFailure.DeniedBySystem)
                {
                    // A refusal is a decision by Windows, not an absent phone; checking again will not change it.
                    _log.Warn(Category, "Windows refused the connection; no longer waiting for the phone.");
                    _waitingSince = null;
                    FailLocked(failure, result.Detail, exhausted: false);
                }
                else if (attempt.Background)
                {
                    _log.Info(Category, $"'{_device!.Name}' is not back yet ({failure}).");
                    WaitForPhoneLocked(failure, result.Detail);
                }
                else
                {
                    _log.Warn(Category, $"Open failed: {failure}{(result.Detail is null ? string.Empty : " - " + result.Detail)}");
                    HandleFailureLocked(failure, result.Detail, attempt.Retry);
                }
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

            var shortLived = _time.GetLocalNow() - _connectedSince < _policy.StableAfter;
            _unstableRecoveries = _connectedByRecovery && shortLived ? _unstableRecoveries + 1 : 0;
            if (_unstableRecoveries >= _policy.MaxUnstableRecoveries)
            {
                _log.Warn(Category, $"The link closed again after each of the last {_unstableRecoveries} reconnections; not reconnecting any more.");
                _unstableRecoveries = 0;
                FailLocked(ConnectionFailure.Unstable, null, exhausted: false);
            }
            else
            {
                HandleFailureLocked(ConnectionFailure.ConnectionLost, null, retriesDone: 0);
            }
        }

        Flush(null);
    }

    private void OnRetryTimer(object? state)
    {
        var epoch = (long)state!;
        Attempt? toStart = null;
        lock (_gate)
        {
            if (epoch != _epoch || _current.Phase is not (ConnectionPhase.WaitingToRetry or ConnectionPhase.WaitingForPhone))
            {
                return;
            }

            _retryTimer?.Dispose();
            _retryTimer = null;

            if (!_bluetoothOn)
            {
                HoldForBluetoothLocked();
            }
            else if (_current.Phase == ConnectionPhase.WaitingForPhone)
            {
                toStart = BeginAttemptLocked(retry: 0, background: true);
            }
            else
            {
                toStart = BeginAttemptLocked(_current.RetryAttempt);
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

        if (canRetry && _policy.KeepWaiting)
        {
            _log.Info(Category, $"No answer after {retriesDone} reconnection attempt(s); waiting for the phone to come back.");
            WaitForPhoneLocked(failure, detail);
            return;
        }

        if (canRetry)
        {
            _log.Warn(Category, $"Giving up after {retriesDone} reconnection attempt(s).");
        }

        FailLocked(failure, detail, exhausted: canRetry);
    }

    /// <summary>Schedules the next slow check, or gives up once the waiting limit has passed.</summary>
    private void WaitForPhoneLocked(ConnectionFailure failure, string? detail)
    {
        if (!_bluetoothOn)
        {
            _recoverable = true;
            HoldForBluetoothLocked();
            return;
        }

        var now = _time.GetLocalNow();
        _waitingSince ??= now;

        // The attempt that just failed made the phone flicker on Bluetooth by itself; that is not a sighting.
        _lastCheckEnded = now;
        if (!_policy.Enabled || !_policy.KeepWaiting || now - _waitingSince >= _policy.WaitLimit)
        {
            _log.Warn(Category, $"Giving up: the phone has not come back within {_policy.WaitLimit.TotalHours:0.#} h.");
            _waitingSince = null;
            FailLocked(failure, detail, exhausted: true);
            return;
        }

        var epoch = ++_epoch;
        SetLocked(new ConnectionSnapshot
        {
            Phase = ConnectionPhase.WaitingForPhone,
            Device = _device,
            MaxRetries = _policy.MaxRetries,
            NextRetryAt = now + _policy.WaitCheckInterval,
            Failure = failure,
            FailureDetail = detail,
        });
        _retryTimer = _time.CreateTimer(OnRetryTimer, epoch, _policy.WaitCheckInterval, Timeout.InfiniteTimeSpan);
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
        _waitingSince = null;
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
        bool changed;

        // One thread closes links at a time, and each takes whatever is retired once it is its turn. A second
        // thread arriving with a link to open therefore waits here until every older link is fully closed,
        // instead of finding nothing left to close and opening alongside a link that is still closing.
        lock (_closing)
        {
            Attempt[] retired;
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

    private sealed class Attempt(long epoch, IAudioLink link, int retry, bool background)
    {
        private readonly CancellationTokenSource _cancellation = new();

        public long Epoch { get; } = epoch;

        public IAudioLink Link { get; } = link;

        public int Retry { get; } = retry;

        /// <summary>True for a slow check made while waiting for the phone; its failure is expected.</summary>
        public bool Background { get; } = background;

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
