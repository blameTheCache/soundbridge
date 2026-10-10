using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Core.Calls;

/// <summary>
/// Holds the hands-free role for the chosen phone while call routing is switched on, and moves each call to
/// this PC once it is being talked on at the phone.
/// <para>
/// Callers only state what they want (<see cref="Configure"/>). A single worker then brings reality in line
/// with the latest wish, one step at a time, so overlapping requests cannot interleave and a wish that changed
/// mid-step is simply acted on next.
/// </para>
/// <para>
/// A call is moved at most once. If the user sends it back to the phone, it stays there.
/// </para>
/// </summary>
public sealed class CallsController : IDisposable
{
    private const string Category = "Calls";

    private readonly Lock _gate = new();
    private readonly ICallTransportFactory _factory;
    private readonly ILog _log;
    private readonly HashSet<string> _handledCalls = new(StringComparer.Ordinal);

    // Wishes, written by callers under the lock.
    private bool _enabled;
    private AudioSource? _phone;
    private bool _connectRequested;
    private bool _disposed;

    // Worker bookkeeping.
    private bool _running;
    private bool _pending;

    // Owned by the worker, except that Dispose may release the transport.
    private ICallTransport? _transport;
    private AudioSource? _claimedPhone;
    private CallsSnapshot _current = CallsSnapshot.Off;

    public CallsController(ICallTransportFactory factory, ILog? log = null)
    {
        _factory = factory;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>Raised after the state changes, on whichever thread caused it. Read <see cref="Current"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once each time the phone's last active call finishes, on a background thread.</summary>
    public event EventHandler? CallEnded;

    public CallsSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public CallSupport Support => _factory.Support;

    /// <summary>States whether calls should be routed here and for which phone. Safe to call repeatedly.</summary>
    public void Configure(bool enabled, AudioSource? phone)
    {
        lock (_gate)
        {
            if (_disposed || (_enabled == enabled && _phone == phone))
            {
                return;
            }

            _enabled = enabled;
            _phone = phone;
        }

        Kick();
    }

    /// <summary>
    /// A hint that the phone is in range right now (for example, its music link just opened). Retries a
    /// claim that failed and brings the hands-free link up if it is not.
    /// </summary>
    public void NotifyPhoneReachable()
    {
        lock (_gate)
        {
            if (_disposed || !_enabled)
            {
                return;
            }

            _connectRequested = true;
        }

        Kick();
    }

    public void Dispose()
    {
        ICallTransport? transport;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Changed = null;
            CallEnded = null;
            transport = _transport;
            _transport = null;
        }

        // Released here, synchronously, so the role is given back even when the process is about to end.
        ReleaseTransport(transport, "shutting down");
    }

    private void Kick()
    {
        lock (_gate)
        {
            _pending = true;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_pending || _disposed)
                {
                    _running = false;
                    return;
                }

                _pending = false;
            }

            try
            {
                await StepAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(Category, $"Call routing step failed: {ex.Message}");
            }
        }
    }

    private async Task StepAsync()
    {
        bool enabled;
        AudioSource? phone;
        bool connect;
        lock (_gate)
        {
            enabled = _enabled;
            phone = _phone;
            connect = _connectRequested;
            _connectRequested = false;
        }

        var support = _factory.Support;
        var wanted = enabled && phone is not null && support == CallSupport.Available;

        if (_transport is not null && (!wanted || _claimedPhone!.Id != phone!.Id))
        {
            var old = Detach();
            ReleaseTransport(old, wanted ? "the selected phone changed" : "call routing was turned off");
        }

        if (!wanted)
        {
            Publish(new CallsSnapshot
            {
                State = !enabled ? CallsState.Off : support != CallSupport.Available ? CallsState.Unavailable : CallsState.NoPhone,
                Support = support,
            });
            return;
        }

        if (_transport is null)
        {
            // A step only runs when something changed or the phone was reported reachable, so a claim that
            // failed is retried exactly when there is a new reason to, never in a loop. Claiming connects.
            connect = false;
            if (!await ClaimAsync(phone!).ConfigureAwait(false))
            {
                return;
            }
        }

        var transport = _transport;
        if (transport is null)
        {
            return;
        }

        if (connect)
        {
            await transport.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await EvaluateCallsAsync(transport).ConfigureAwait(false);
    }

    private async Task<bool> ClaimAsync(AudioSource phone)
    {
        Publish(new CallsSnapshot { State = CallsState.Starting });
        _log.Info(Category, $"Claiming the hands-free role for '{phone.Name}'.");

        var transport = _factory.Create(phone);
        CallClaimResult result;
        try
        {
            result = await transport.ClaimAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new CallClaimResult(CallClaimStatus.Failed, ex.Message);
        }

        if (result.Status != CallClaimStatus.Claimed)
        {
            _log.Warn(Category, $"Could not claim the hands-free role: {result.Status}{(result.Detail is null ? string.Empty : " - " + result.Detail)}");
            transport.Dispose();
            Publish(new CallsSnapshot { State = CallsState.Failed, Failure = result.Status, FailureDetail = result.Detail });
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                transport.Dispose();
                return false;
            }

            _transport = transport;
            _claimedPhone = phone;
        }

        transport.Changed += OnTransportChanged;
        _log.Info(Category, $"Hands-free role held for '{phone.Name}'.");
        return true;
    }

    private async Task EvaluateCallsAsync(ICallTransport transport)
    {
        var calls = await transport.GetActiveCallsAsync(CancellationToken.None).ConfigureAwait(false);

        _handledCalls.IntersectWith(calls.Select(call => call.Id));

        foreach (var call in calls)
        {
            // Only a call the user is already talking on at the phone is moved; a ringing call is left alone.
            if (call.Phase != CallPhase.Talking || call.OnPc || !transport.CanRouteToPc || !_handledCalls.Add(call.Id))
            {
                continue;
            }

            _log.Info(Category, "A call is in progress on the phone; moving its audio to this PC.");
            var moved = await transport.MoveToPcAsync(call.Id, CancellationToken.None).ConfigureAwait(false);
            _log.Info(Category, moved ? "Windows accepted the move." : "Windows did not move the call.");
        }

        var current = calls.FirstOrDefault(call => call.Phase is CallPhase.Talking or CallPhase.Held)
            ?? calls.FirstOrDefault();
        var wasInCall = Current.State == CallsState.InCall;

        if (current is not null)
        {
            if (!wasInCall)
            {
                _log.Info(Category, $"The phone has a call ({current.Phase}).");
            }

            Publish(new CallsSnapshot { State = CallsState.InCall, CallOnPc = current.OnPc });
            return;
        }

        Publish(new CallsSnapshot { State = CallsState.Ready });
        if (wasInCall)
        {
            _log.Info(Category, "The call ended.");
            Raise(CallEnded);
        }
    }

    private void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error(Category, $"A call listener threw: {ex}");
        }
    }

    private void OnTransportChanged(object? sender, EventArgs e) => Kick();

    private ICallTransport? Detach()
    {
        lock (_gate)
        {
            var transport = _transport;
            _transport = null;
            _claimedPhone = null;
            _handledCalls.Clear();
            return transport;
        }
    }

    private void ReleaseTransport(ICallTransport? transport, string reason)
    {
        if (transport is null)
        {
            return;
        }

        transport.Changed -= OnTransportChanged;
        try
        {
            transport.Dispose();
            _log.Info(Category, $"Released the hands-free role ({reason}).");
        }
        catch (Exception ex)
        {
            _log.Warn(Category, $"Releasing the hands-free role failed: {ex.Message}");
        }
    }

    private void Publish(CallsSnapshot snapshot)
    {
        EventHandler? handler;
        lock (_gate)
        {
            if (_disposed || _current == snapshot)
            {
                return;
            }

            _current = snapshot;
            handler = Changed;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error(Category, $"A call-state listener threw: {ex}");
        }
    }
}
