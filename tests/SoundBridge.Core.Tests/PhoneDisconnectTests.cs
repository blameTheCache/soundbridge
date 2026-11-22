using Microsoft.Extensions.Time.Testing;
using SoundBridge.Core.Connection;

namespace SoundBridge.Core.Tests;

/// <summary>
/// When the link closes, the controller asks whether the phone still answers on Bluetooth. A phone that
/// answers closed the link on purpose (disconnected on the phone, or switched to another speaker) and is left
/// alone; a phone that does not answer went out of range or lost Bluetooth, and is reconnected as before.
/// </summary>
public sealed class PhoneDisconnectTests : IDisposable
{
    private static readonly AudioSource Phone = new("phone-1", "Pixel");
    private static readonly AudioSource Tablet = new("tablet-1", "Tablet");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLinkFactory _factory = new();
    private readonly FakeReachability _reachability = new();
    private readonly RecordingLog _log = new();
    private readonly ConnectionController _controller;

    public PhoneDisconnectTests()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        _controller = new ConnectionController(_factory, ReconnectPolicy.Default, _time, _log, _reachability);
    }

    public void Dispose() => _controller.Dispose();

    [Fact]
    public void A_closed_link_is_checked_before_anything_reconnects()
    {
        var link = ConnectAndOpen(Phone);

        link.RaiseClosed();

        Assert.Equal(ConnectionPhase.CheckingClosure, _controller.Current.Phase);
        Assert.True(_controller.Current.IsActive);
        Assert.Equal(Phone, _controller.Current.Device);
        Assert.True(link.Disposed);
        Assert.Equal(Phone, Assert.Single(_reachability.Probes).Device);

        // Nothing is opened while the answer is pending, however long it takes.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void Disconnecting_on_the_phone_is_respected()
    {
        ConnectAndOpen(Phone).RaiseClosed();

        _reachability.Latest.Answer(PhoneReachability.Reachable);

        WaitFor(ConnectionPhase.Disconnected);
        var snapshot = _controller.Current;
        Assert.True(snapshot.ClosedByPhone);
        Assert.Equal(Phone, snapshot.Device);
        Assert.Equal(ConnectionFailure.None, snapshot.Failure);
        Assert.False(snapshot.IsActive);
        Assert.True(_log.Contains("closed on the phone"));

        // Nothing brings it back: not time, not the phone showing up, not sleep and resume.
        _controller.NotifyPhoneSeen();
        _controller.OnSuspending();
        _controller.OnResumed();
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void Connecting_again_after_the_phone_disconnected_works_and_clears_the_flag()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        _reachability.Latest.Answer(PhoneReachability.Reachable);
        WaitFor(ConnectionPhase.Disconnected);

        Assert.True(_controller.Connect(Phone, ConnectOrigin.Manual));
        Assert.False(_controller.Current.ClosedByPhone);
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);

        WaitFor(ConnectionPhase.Connected);
        Assert.False(_controller.Current.ClosedByPhone);
    }

    [Fact]
    public void A_phone_that_does_not_answer_went_out_of_range_and_is_reconnected()
    {
        var closedAt = _time.GetLocalNow();
        ConnectAndOpen(Phone).RaiseClosed();

        _reachability.Latest.Answer(PhoneReachability.Unreachable);

        WaitFor(ConnectionPhase.WaitingToRetry);
        Assert.Equal(1, _controller.Current.RetryAttempt);
        Assert.Equal(ConnectionFailure.ConnectionLost, _controller.Current.Failure);
        Assert.False(_controller.Current.ClosedByPhone);
        Assert.Equal(closedAt + TimeSpan.FromSeconds(2), _controller.Current.NextRetryAt);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, _factory.Links.Count);
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
    }

    [Fact]
    public void A_check_that_gets_no_answer_in_time_counts_as_out_of_range()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        var probe = _reachability.Latest;

        _time.Advance(ReconnectPolicy.Default.ReachabilityTimeout - TimeSpan.FromMilliseconds(1));
        Assert.Equal(ConnectionPhase.CheckingClosure, _controller.Current.Phase);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        WaitFor(ConnectionPhase.WaitingToRetry);
        Until(() => probe.CancellationRequested);
        Assert.True(_log.Contains("did not answer"));

        // A late answer from the abandoned check changes nothing.
        probe.Answer(PhoneReachability.Reachable);
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
    }

    [Fact]
    public void A_check_that_cannot_tell_falls_back_to_reconnecting()
    {
        ConnectAndOpen(Phone).RaiseClosed();

        _reachability.Latest.Answer(PhoneReachability.Unknown);

        WaitFor(ConnectionPhase.WaitingToRetry);
    }

    [Fact]
    public void A_check_that_throws_falls_back_to_reconnecting()
    {
        ConnectAndOpen(Phone).RaiseClosed();

        _reachability.Latest.Fail(new InvalidOperationException("radio busy"));

        WaitFor(ConnectionPhase.WaitingToRetry);
        Assert.True(_log.Contains("radio busy"));
    }

    [Fact]
    public void A_probe_that_throws_synchronously_falls_back_to_reconnecting()
    {
        _reachability.ThrowOnProbe = new InvalidOperationException("no adapter");
        ConnectAndOpen(Phone).RaiseClosed();

        WaitFor(ConnectionPhase.WaitingToRetry);
    }

    [Fact]
    public void Cancel_while_checking_stops_everything()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        var probe = _reachability.Latest;

        _controller.Disconnect();

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.False(_controller.Current.ClosedByPhone);
        Until(() => probe.CancellationRequested);

        probe.Answer(PhoneReachability.Unreachable);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void Connecting_by_hand_while_checking_replaces_the_check()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        var probe = _reachability.Latest;

        Assert.True(_controller.Connect(Phone, ConnectOrigin.Manual));
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);

        probe.Answer(PhoneReachability.Reachable);
        Assert.Equal(ConnectionPhase.Connected, _controller.Current.Phase);
    }

    [Fact]
    public void Connecting_to_another_device_while_checking_replaces_the_check()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        var probe = _reachability.Latest;

        _controller.Connect(Tablet, ConnectOrigin.Manual);
        probe.Answer(PhoneReachability.Unreachable);

        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
        Assert.Equal(Tablet, _controller.Current.Device);
    }

    [Fact]
    public void Bluetooth_off_while_checking_waits_for_bluetooth()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        var probe = _reachability.Latest;

        _controller.SetBluetoothAvailable(false);

        Assert.Equal(ConnectionPhase.WaitingForBluetooth, _controller.Current.Phase);
        probe.Answer(PhoneReachability.Reachable);
        Assert.Equal(ConnectionPhase.WaitingForBluetooth, _controller.Current.Phase);

        _controller.SetBluetoothAvailable(true);
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
    }

    [Fact]
    public void Sleep_while_checking_resumes_the_connection_afterwards()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        var probe = _reachability.Latest;

        _controller.OnSuspending();
        probe.Answer(PhoneReachability.Reachable);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);

        _controller.OnResumed();
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
    }

    [Fact]
    public void Without_automatic_reconnection_a_lost_link_is_reported_and_a_phone_disconnect_is_not_an_error()
    {
        _controller.SetPolicy(ReconnectPolicy.Disabled);

        ConnectAndOpen(Phone).RaiseClosed();
        _reachability.Latest.Answer(PhoneReachability.Unreachable);
        WaitFor(ConnectionPhase.Failed);
        Assert.Equal(ConnectionFailure.ConnectionLost, _controller.Current.Failure);

        ConnectAndOpen(Phone).RaiseClosed();
        _reachability.Latest.Answer(PhoneReachability.Reachable);
        WaitFor(ConnectionPhase.Disconnected);
        Assert.True(_controller.Current.ClosedByPhone);
    }

    [Fact]
    public void A_phone_disconnect_resets_the_instability_count()
    {
        ConnectAndOpen(Phone);

        // Two quick losses that recover...
        for (var round = 0; round < 2; round++)
        {
            _factory.Latest.RaiseClosed();
            _reachability.Latest.Answer(PhoneReachability.Unreachable);
            WaitFor(ConnectionPhase.WaitingToRetry);
            _time.Advance(TimeSpan.FromSeconds(2));
            _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
            WaitFor(ConnectionPhase.Connected);
        }

        // ...then a deliberate disconnect, and a fresh connect: the earlier instability is forgotten.
        _factory.Latest.RaiseClosed();
        _reachability.Latest.Answer(PhoneReachability.Reachable);
        WaitFor(ConnectionPhase.Disconnected);

        ConnectAndOpen(Phone).RaiseClosed();
        _reachability.Latest.Answer(PhoneReachability.Unreachable);
        WaitFor(ConnectionPhase.WaitingToRetry);
    }

    [Fact]
    public void Instability_is_still_detected_when_the_phone_never_answers()
    {
        ConnectAndOpen(Phone);

        for (var round = 0; round < 3; round++)
        {
            _factory.Latest.RaiseClosed();
            _reachability.Latest.Answer(PhoneReachability.Unreachable);
            WaitFor(ConnectionPhase.WaitingToRetry);
            _time.Advance(TimeSpan.FromSeconds(2));
            _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
            WaitFor(ConnectionPhase.Connected);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        _factory.Latest.RaiseClosed();
        _reachability.Latest.Answer(PhoneReachability.Unreachable);

        WaitFor(ConnectionPhase.Failed);
        Assert.Equal(ConnectionFailure.Unstable, _controller.Current.Failure);
    }

    [Fact]
    public void A_link_that_closes_during_its_open_is_not_probed()
    {
        _controller.Connect(Phone, ConnectOrigin.Manual);
        _factory.Latest.RaiseClosed();
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);

        WaitFor(ConnectionPhase.Failed);
        Assert.Empty(_reachability.Probes);
    }

    [Fact]
    public void Dispose_while_checking_cancels_the_check()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        var probe = _reachability.Latest;

        _controller.Dispose();

        Until(() => probe.CancellationRequested);
        probe.Answer(PhoneReachability.Unreachable);
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void Each_transition_is_reported_to_listeners()
    {
        var phases = new List<ConnectionPhase>();
        _controller.Changed += (_, _) => phases.Add(_controller.Current.Phase);

        ConnectAndOpen(Phone).RaiseClosed();
        _reachability.Latest.Answer(PhoneReachability.Reachable);
        WaitFor(ConnectionPhase.Disconnected);

        Assert.Equal(
            [ConnectionPhase.Connecting, ConnectionPhase.Connected, ConnectionPhase.CheckingClosure, ConnectionPhase.Disconnected],
            phases);
    }

    [Fact]
    public async Task Concurrent_requests_and_answers_never_leak_a_link_or_resurrect_a_connection()
    {
        var random = new Random(4321);
        var seeds = Enumerable.Range(0, 8).Select(_ => random.Next()).ToArray();

        await Task.WhenAll(seeds.Select(seed => Task.Run(() =>
        {
            var rng = new Random(seed);
            for (var i = 0; i < 400; i++)
            {
                switch (rng.Next(10))
                {
                    case 0: _controller.Connect(Phone, ConnectOrigin.Manual); break;
                    case 1: _controller.Disconnect(); break;
                    case 2: _factory.LatestOrDefault?.CompleteOpen(LinkOpenStatus.Opened); break;
                    case 3: _factory.LatestOrDefault?.CompleteOpen(LinkOpenStatus.TimedOut); break;
                    case 4: _factory.LatestOrDefault?.RaiseClosed(); break;
                    case 5: _reachability.Probes.LastOrDefault()?.Answer(PhoneReachability.Reachable); break;
                    case 6: _reachability.Probes.LastOrDefault()?.Answer(PhoneReachability.Unreachable); break;
                    case 7: _controller.SetBluetoothAvailable(rng.Next(2) == 0); break;
                    case 8: _time.Advance(TimeSpan.FromSeconds(rng.Next(1, 15))); break;
                    default: _controller.OnSuspending(); _controller.OnResumed(); break;
                }
            }
        })));

        _controller.Disconnect();
        foreach (var link in _factory.Links)
        {
            link.CompleteOpen(LinkOpenStatus.Opened);
        }

        foreach (var probe in _reachability.Probes)
        {
            probe.Answer(PhoneReachability.Unreachable);
        }

        Until(() => _factory.Links.All(link => link.Disposed));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.All(_factory.Links, link => Assert.Equal(1, link.DisposeCalls));
        Assert.All(_reachability.Probes, probe => Assert.True(probe.CancellationRequested));
    }

    private FakeLink ConnectAndOpen(AudioSource device)
    {
        Assert.True(_controller.Connect(device, ConnectOrigin.Manual));
        var link = _factory.Latest;
        link.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
        return link;
    }

    private void WaitFor(ConnectionPhase phase) =>
        Assert.True(
            SpinWait.SpinUntil(() => _controller.Current.Phase == phase, TimeSpan.FromSeconds(5)),
            $"Expected {phase} but the controller is {_controller.Current.Phase}.");

    private static void Until(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "Condition was not reached in time.");
}
