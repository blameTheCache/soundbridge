using System.Collections.Concurrent;
using SoundBridge.Core.Calls;
using SoundBridge.Core.Connection;

namespace SoundBridge.Core.Tests;

public sealed class CallsControllerTests : IDisposable
{
    private static readonly AudioSource Phone = new("phone-1", "Pixel");
    private static readonly AudioSource Tablet = new("tablet-1", "Tablet");

    private readonly FakeCallTransportFactory _factory = new();
    private readonly RecordingLog _log = new();
    private readonly CallsController _controller;

    public CallsControllerTests() => _controller = new CallsController(_factory, _log);

    public void Dispose() => _controller.Dispose();

    [Fact]
    public void Stays_off_until_switched_on()
    {
        Assert.Equal(CallsState.Off, _controller.Current.State);
        Assert.Empty(_factory.Transports);
    }

    [Fact]
    public void Switching_on_claims_the_role_and_becomes_ready()
    {
        _controller.Configure(enabled: true, Phone);

        WaitFor(CallsState.Ready);
        var transport = Assert.Single(_factory.Transports);
        Assert.Equal(Phone, transport.Phone);
        Assert.Equal(1, transport.ClaimCalls);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public void Repeating_the_same_request_does_not_claim_twice()
    {
        _controller.Configure(enabled: true, Phone);
        WaitFor(CallsState.Ready);

        _controller.Configure(enabled: true, Phone);
        _controller.Configure(enabled: true, Phone);
        Settle();

        Assert.Single(_factory.Transports);
        Assert.Equal(1, _factory.Transports[0].ClaimCalls);
    }

    [Fact]
    public void Switching_off_releases_the_role()
    {
        _controller.Configure(enabled: true, Phone);
        WaitFor(CallsState.Ready);

        _controller.Configure(enabled: false, Phone);

        WaitFor(CallsState.Off);
        Assert.Equal(1, _factory.Transports[0].DisposeCalls);
    }

    [Fact]
    public void Without_a_phone_nothing_is_claimed()
    {
        _controller.Configure(enabled: true, phone: null);

        WaitFor(CallsState.NoPhone);
        Assert.Empty(_factory.Transports);
    }

    [Theory]
    [InlineData(CallSupport.NeedsInstalledApp)]
    [InlineData(CallSupport.NeedsNewerWindows)]
    public void Unsupported_setups_report_why_and_claim_nothing(CallSupport support)
    {
        _factory.Support = support;

        _controller.Configure(enabled: true, Phone);

        WaitFor(CallsState.Unavailable);
        Assert.Equal(support, _controller.Current.Support);
        Assert.Empty(_factory.Transports);
    }

    [Fact]
    public void A_refused_claim_is_reported_and_not_retried_on_its_own()
    {
        _factory.NextClaim = new CallClaimResult(CallClaimStatus.AccessDenied, "denied");

        _controller.Configure(enabled: true, Phone);

        WaitFor(CallsState.Failed);
        Assert.Equal(CallClaimStatus.AccessDenied, _controller.Current.Failure);
        Assert.Equal("denied", _controller.Current.FailureDetail);
        Assert.True(_factory.Transports[0].Disposed);

        Settle();
        Assert.Single(_factory.Transports);
    }

    [Fact]
    public void A_failed_claim_is_retried_when_the_phone_becomes_reachable()
    {
        _factory.NextClaim = new CallClaimResult(CallClaimStatus.Failed);
        _controller.Configure(enabled: true, Phone);
        WaitFor(CallsState.Failed);

        _factory.NextClaim = new CallClaimResult(CallClaimStatus.Claimed);
        _controller.NotifyPhoneReachable();

        WaitFor(CallsState.Ready);
        Assert.Equal(2, _factory.Transports.Count);
    }

    [Fact]
    public void An_exception_while_claiming_is_reported_as_a_failure()
    {
        _factory.ThrowOnClaim = new InvalidOperationException("boom");

        _controller.Configure(enabled: true, Phone);

        WaitFor(CallsState.Failed);
        Assert.Equal("boom", _controller.Current.FailureDetail);
    }

    [Fact]
    public void Reachability_hint_reconnects_the_link_without_claiming_again()
    {
        _controller.Configure(enabled: true, Phone);
        WaitFor(CallsState.Ready);
        var transport = _factory.Transports[0];

        _controller.NotifyPhoneReachable();

        Until(() => transport.ConnectCalls == 1);
        Assert.Single(_factory.Transports);
    }

    [Fact]
    public void Reachability_hint_is_ignored_while_switched_off()
    {
        _controller.NotifyPhoneReachable();
        Settle();

        Assert.Empty(_factory.Transports);
        Assert.Equal(CallsState.Off, _controller.Current.State);
    }

    [Fact]
    public void A_call_answered_on_the_phone_is_moved_to_the_pc()
    {
        var transport = Ready();

        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: false));

        Until(() => transport.Moved.Contains("call-1"));
        WaitFor(CallsState.InCall);
    }

    [Fact]
    public void A_ringing_call_is_left_on_the_phone()
    {
        var transport = Ready();

        transport.SetCalls(new ActiveCall("call-1", CallPhase.Incoming, OnPc: false));

        WaitFor(CallsState.InCall);
        Settle();
        Assert.Empty(transport.Moved);
    }

    [Fact]
    public void A_call_the_user_sends_back_to_the_phone_is_not_pulled_again()
    {
        var transport = Ready();
        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: false));
        Until(() => transport.Moved.Count == 1);
        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: true));
        Until(() => _controller.Current.CallOnPc);

        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: false));

        Until(() => _controller.Current is { State: CallsState.InCall, CallOnPc: false });
        Settle();
        Assert.Single(transport.Moved);
    }

    [Fact]
    public void A_call_is_moved_only_once_windows_allows_routing()
    {
        var transport = Ready();
        transport.CanRouteToPc = false;

        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: false));
        WaitFor(CallsState.InCall);
        Settle();
        Assert.Empty(transport.Moved);

        transport.CanRouteToPc = true;
        transport.RaiseChanged();

        Until(() => transport.Moved.Contains("call-1"));
    }

    [Fact]
    public void Each_new_call_is_moved()
    {
        var transport = Ready();
        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: false));
        Until(() => transport.Moved.Contains("call-1"));
        transport.SetCalls();
        WaitFor(CallsState.Ready);

        transport.SetCalls(new ActiveCall("call-2", CallPhase.Talking, OnPc: false));

        Until(() => transport.Moved.Contains("call-2"));
        Assert.Equal(2, transport.Moved.Count);
    }

    [Fact]
    public void State_follows_where_the_call_audio_is()
    {
        var transport = Ready();

        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: true));
        Until(() => _controller.Current is { State: CallsState.InCall, CallOnPc: true });

        transport.SetCalls();
        WaitFor(CallsState.Ready);
        Assert.Empty(transport.Moved);
    }

    [Fact]
    public void Call_ended_is_raised_once_when_the_call_finishes()
    {
        var transport = Ready();
        var ended = 0;
        _controller.CallEnded += (_, _) => Interlocked.Increment(ref ended);

        transport.SetCalls(new ActiveCall("call-1", CallPhase.Incoming, OnPc: false));
        WaitFor(CallsState.InCall);
        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: true));
        Until(() => _controller.Current.CallOnPc);
        Assert.Equal(0, ended);

        transport.SetCalls();
        WaitFor(CallsState.Ready);
        transport.RaiseChanged();
        Settle();

        Assert.Equal(1, ended);
    }

    [Fact]
    public void Switching_off_during_a_call_does_not_report_the_call_as_ended()
    {
        var transport = Ready();
        var ended = 0;
        _controller.CallEnded += (_, _) => Interlocked.Increment(ref ended);
        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: true));
        WaitFor(CallsState.InCall);

        _controller.Configure(enabled: false, Phone);

        WaitFor(CallsState.Off);
        Settle();
        Assert.Equal(0, ended);
    }

    [Fact]
    public void Choosing_another_phone_releases_the_first_and_claims_the_second()
    {
        var first = Ready();

        _controller.Configure(enabled: true, Tablet);

        Until(() => _factory.Transports.Count == 2 && _controller.Current.State == CallsState.Ready);
        Assert.True(first.Disposed);
        Assert.Equal(Tablet, _factory.Transports[1].Phone);
        Assert.False(_factory.Transports[1].Disposed);
    }

    [Fact]
    public void Switching_off_while_a_claim_is_in_flight_releases_it_afterwards()
    {
        _factory.HoldClaims = true;
        _controller.Configure(enabled: true, Phone);
        Until(() => _factory.Transports.Count == 1 && _factory.Transports[0].ClaimCalls == 1);

        _controller.Configure(enabled: false, Phone);
        _factory.Transports[0].CompleteClaim(new CallClaimResult(CallClaimStatus.Claimed));

        WaitFor(CallsState.Off);
        Until(() => _factory.Transports[0].Disposed);
    }

    [Fact]
    public void Dispose_releases_the_role_immediately_and_stops_reacting()
    {
        var transport = Ready();
        var raised = 0;
        _controller.Changed += (_, _) => raised++;

        _controller.Dispose();

        Assert.True(transport.Disposed);
        transport.SetCalls(new ActiveCall("call-1", CallPhase.Talking, OnPc: false));
        _controller.Configure(enabled: true, Tablet);
        Settle();
        Assert.Empty(transport.Moved);
        Assert.Single(_factory.Transports);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Changed_is_raised_as_the_state_moves()
    {
        var states = new ConcurrentQueue<CallsState>();
        _controller.Changed += (_, _) => states.Enqueue(_controller.Current.State);

        _controller.Configure(enabled: true, Phone);
        WaitFor(CallsState.Ready);
        _controller.Configure(enabled: false, Phone);
        WaitFor(CallsState.Off);

        Until(() => states.Contains(CallsState.Off));
        Assert.Contains(CallsState.Ready, states);
    }

    private FakeCallTransport Ready()
    {
        _controller.Configure(enabled: true, Phone);
        WaitFor(CallsState.Ready);
        return _factory.Transports[0];
    }

    private void WaitFor(CallsState state) =>
        Assert.True(
            SpinWait.SpinUntil(() => _controller.Current.State == state, TimeSpan.FromSeconds(5)),
            $"Expected {state} but the controller is {_controller.Current.State}.");

    private static void Until(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "Condition was not reached in time.");

    /// <summary>Gives the background worker time to do anything it was (wrongly) going to do.</summary>
    private static void Settle() => Thread.Sleep(150);
}

internal sealed class FakeCallTransport(AudioSource phone, FakeCallTransportFactory owner) : ICallTransport
{
    private readonly TaskCompletionSource<CallClaimResult> _heldClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile IReadOnlyList<ActiveCall> _calls = [];
    private int _claimCalls;
    private int _connectCalls;
    private int _disposeCalls;

    public event EventHandler? Changed;

    public AudioSource Phone { get; } = phone;

    public bool CanRouteToPc { get; set; } = true;

    public int ClaimCalls => _claimCalls;

    public int ConnectCalls => _connectCalls;

    public int DisposeCalls => _disposeCalls;

    public bool Disposed => _disposeCalls > 0;

    public ConcurrentQueue<string> Moved { get; } = new();

    public Task<CallClaimResult> ClaimAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _claimCalls);
        if (owner.ThrowOnClaim is { } exception)
        {
            throw exception;
        }

        return owner.HoldClaims ? _heldClaim.Task : Task.FromResult(owner.NextClaim);
    }

    public void CompleteClaim(CallClaimResult result) => _heldClaim.TrySetResult(result);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectCalls);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActiveCall>> GetActiveCallsAsync(CancellationToken cancellationToken) => Task.FromResult(_calls);

    public Task<bool> MoveToPcAsync(string callId, CancellationToken cancellationToken)
    {
        Moved.Enqueue(callId);
        return Task.FromResult(true);
    }

    public void SetCalls(params ActiveCall[] calls)
    {
        _calls = calls;
        RaiseChanged();
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Interlocked.Increment(ref _disposeCalls);
}

internal sealed class FakeCallTransportFactory : ICallTransportFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeCallTransport> _transports = [];

    public CallSupport Support { get; set; } = CallSupport.Available;

    public CallClaimResult NextClaim { get; set; } = new(CallClaimStatus.Claimed);

    public Exception? ThrowOnClaim { get; set; }

    public bool HoldClaims { get; set; }

    public IReadOnlyList<FakeCallTransport> Transports
    {
        get
        {
            lock (_gate)
            {
                return [.. _transports];
            }
        }
    }

    public ICallTransport Create(AudioSource phone)
    {
        var transport = new FakeCallTransport(phone, this);
        lock (_gate)
        {
            _transports.Add(transport);
        }

        return transport;
    }
}
