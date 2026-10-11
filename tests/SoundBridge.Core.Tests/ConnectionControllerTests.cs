using Microsoft.Extensions.Time.Testing;
using SoundBridge.Core.Connection;

namespace SoundBridge.Core.Tests;

public sealed class ConnectionControllerTests : IDisposable
{
    private static readonly AudioSource Phone = new("phone-1", "Pixel");
    private static readonly AudioSource Tablet = new("tablet-1", "Tablet");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLinkFactory _factory = new();
    private readonly RecordingLog _log = new();
    private ConnectionController _controller;

    public ConnectionControllerTests()
    {
        // Without a synchronization context, completing a fake open runs the controller's continuation inline.
        SynchronizationContext.SetSynchronizationContext(null);
        _controller = Create(ReconnectPolicy.Default);
    }

    public void Dispose() => _controller.Dispose();

    [Fact]
    public void Manual_connect_opens_the_link_and_reports_connected()
    {
        Assert.True(_controller.Connect(Phone, ConnectOrigin.Manual));
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
        Assert.Equal(Phone, _controller.Current.Device);

        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);

        WaitFor(ConnectionPhase.Connected);
        Assert.Equal(_time.GetLocalNow(), _controller.Current.ConnectedAt);
        Assert.Equal(1, _factory.Latest.OpenCalls);
        Assert.False(_factory.Latest.Disposed);
    }

    [Fact]
    public void Duplicate_connect_while_connecting_does_not_start_a_second_attempt()
    {
        _controller.Connect(Phone, ConnectOrigin.Manual);

        Assert.False(_controller.Connect(Phone, ConnectOrigin.Manual));
        Assert.False(_controller.Connect(Phone, ConnectOrigin.SessionStart));

        Assert.Single(_factory.Links);
        Assert.Equal(1, _factory.Latest.OpenCalls);
        Assert.False(_factory.Latest.Disposed);
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
    }

    [Fact]
    public void Duplicate_connect_while_connected_keeps_the_existing_link()
    {
        var link = ConnectAndOpen(Phone);

        Assert.False(_controller.Connect(Phone, ConnectOrigin.Manual));

        Assert.Single(_factory.Links);
        Assert.False(link.Disposed);
        Assert.Equal(ConnectionPhase.Connected, _controller.Current.Phase);
    }

    [Fact]
    public void Manual_connect_failure_is_reported_and_not_retried()
    {
        _controller.Connect(Phone, ConnectOrigin.Manual);
        _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);

        WaitFor(ConnectionPhase.Failed);
        Assert.Equal(ConnectionFailure.TimedOut, _controller.Current.Failure);
        Assert.False(_controller.Current.RetriesExhausted);
        Assert.True(_factory.Latest.Disposed);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Failed, _controller.Current.Phase);
    }

    [Fact]
    public void Unexpected_drop_retries_with_exponential_backoff()
    {
        var first = ConnectAndOpen(Phone);
        var droppedAt = _time.GetLocalNow();

        first.RaiseClosed();

        var waiting = _controller.Current;
        Assert.Equal(ConnectionPhase.WaitingToRetry, waiting.Phase);
        Assert.Equal(1, waiting.RetryAttempt);
        Assert.Equal(5, waiting.MaxRetries);
        Assert.Equal(ConnectionFailure.ConnectionLost, waiting.Failure);
        Assert.Equal(droppedAt + TimeSpan.FromSeconds(2), waiting.NextRetryAt);
        Assert.True(first.Disposed);

        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Single(_factory.Links);

        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, _factory.Links.Count);
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
        Assert.Equal(1, _controller.Current.RetryAttempt);

        foreach (var (retry, seconds) in new[] { (2, 4), (3, 8), (4, 16), (5, 32) })
        {
            var failedAt = _time.GetLocalNow();
            _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);
            WaitFor(ConnectionPhase.WaitingToRetry);

            Assert.Equal(retry, _controller.Current.RetryAttempt);
            Assert.Equal(failedAt + TimeSpan.FromSeconds(seconds), _controller.Current.NextRetryAt);

            _time.Advance(TimeSpan.FromSeconds(seconds));
            Assert.Equal(retry + 1, _factory.Links.Count);
        }
    }

    [Fact]
    public void Successful_retry_reconnects_and_restores_the_full_retry_budget()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        _time.Advance(TimeSpan.FromSeconds(2));
        _factory.Latest.CompleteOpen(LinkOpenStatus.DeviceUnavailable);
        WaitFor(ConnectionPhase.WaitingToRetry);
        _time.Advance(TimeSpan.FromSeconds(4));

        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
        Assert.Equal(0, _controller.Current.RetryAttempt);

        _factory.Latest.RaiseClosed();

        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
        Assert.Equal(1, _controller.Current.RetryAttempt);
    }

    [Fact]
    public void Retries_stop_after_the_configured_maximum()
    {
        Recreate(new ReconnectPolicy { MaxRetries = 3, KeepWaiting = false });
        ConnectAndOpen(Phone).RaiseClosed();

        foreach (var seconds in new[] { 2, 4, 8 })
        {
            WaitFor(ConnectionPhase.WaitingToRetry);
            _time.Advance(TimeSpan.FromSeconds(seconds));
            _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);
        }

        WaitFor(ConnectionPhase.Failed);
        Assert.True(_controller.Current.RetriesExhausted);
        Assert.Equal(ConnectionFailure.TimedOut, _controller.Current.Failure);
        Assert.Equal(4, _factory.Links.Count);
        Assert.All(_factory.Links, link => Assert.True(link.Disposed));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(4, _factory.Links.Count);
    }

    [Fact]
    public void When_the_retries_run_out_it_waits_for_the_phone_instead_of_giving_up()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();

        var waiting = _controller.Current;
        Assert.Equal(ConnectionPhase.WaitingForPhone, waiting.Phase);
        Assert.Equal(_time.GetLocalNow() + TimeSpan.FromMinutes(2), waiting.NextRetryAt);
        Assert.Equal(ConnectionFailure.TimedOut, waiting.Failure);
        Assert.True(waiting.IsActive);
        Assert.Equal(6, _factory.Links.Count);
    }

    [Fact]
    public void While_waiting_it_checks_at_the_slow_interval_and_reconnects_when_the_phone_answers()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();

        _time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(6, _factory.Links.Count);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(7, _factory.Links.Count);
        Assert.True(_controller.Current is { Phase: ConnectionPhase.Connecting, BackgroundCheck: true });

        _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);
        WaitFor(ConnectionPhase.WaitingForPhone);

        _time.Advance(TimeSpan.FromMinutes(2));
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);

        WaitFor(ConnectionPhase.Connected);
        Assert.Equal(8, _factory.Links.Count);
        Assert.False(_controller.Current.BackgroundCheck);
    }

    [Fact]
    public void Seeing_the_phone_while_waiting_checks_at_once()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();
        _time.Advance(TimeSpan.FromSeconds(30));

        _controller.NotifyPhoneSeen();

        Assert.Equal(7, _factory.Links.Count);
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);

        // The slow check that was pending must not fire on top of the new connection.
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(7, _factory.Links.Count);
    }

    [Fact]
    public void A_sighting_right_after_a_failed_check_is_ignored()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();
        _time.Advance(TimeSpan.FromMinutes(2));
        _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);
        WaitFor(ConnectionPhase.WaitingForPhone);

        _time.Advance(TimeSpan.FromSeconds(5));
        _controller.NotifyPhoneSeen();
        Assert.Equal(7, _factory.Links.Count);

        _time.Advance(TimeSpan.FromSeconds(6));
        _controller.NotifyPhoneSeen();
        Assert.Equal(8, _factory.Links.Count);
    }

    [Fact]
    public void A_sighting_does_nothing_unless_the_controller_is_waiting_for_the_phone()
    {
        _controller.NotifyPhoneSeen();
        Assert.Empty(_factory.Links);

        ConnectAndOpen(Phone);
        _controller.NotifyPhoneSeen();
        Assert.Single(_factory.Links);

        _controller.Disconnect();
        _controller.NotifyPhoneSeen();
        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void Waiting_for_the_phone_ends_after_the_limit()
    {
        Recreate(new ReconnectPolicy { MaxRetries = 1, WaitLimit = TimeSpan.FromMinutes(5) });
        ConnectAndOpen(Phone).RaiseClosed();
        _time.Advance(TimeSpan.FromSeconds(2));
        _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);
        WaitFor(ConnectionPhase.WaitingForPhone);

        // The first two checks fall inside the five-minute limit; the third does not.
        for (var check = 0; check < 3; check++)
        {
            // The controller handles each result on another thread; let it schedule the next check first.
            WaitFor(ConnectionPhase.WaitingForPhone);
            _time.Advance(TimeSpan.FromMinutes(2));
            _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);
        }

        WaitFor(ConnectionPhase.Failed);
        Assert.True(_controller.Current.RetriesExhausted);
        var links = _factory.Links.Count;
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(links, _factory.Links.Count);
    }

    [Fact]
    public void Manual_disconnect_while_waiting_for_the_phone_stops_the_checks()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();

        _controller.Disconnect();

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        _controller.NotifyPhoneSeen();
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(6, _factory.Links.Count);
    }

    [Fact]
    public void Bluetooth_off_while_waiting_for_the_phone_resumes_with_fresh_retries_when_it_returns()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();

        _controller.SetBluetoothAvailable(false);
        Assert.Equal(ConnectionPhase.WaitingForBluetooth, _controller.Current.Phase);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(6, _factory.Links.Count);

        _controller.SetBluetoothAvailable(true);

        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
        Assert.Equal(1, _controller.Current.RetryAttempt);
    }

    [Fact]
    public void Turning_waiting_off_while_waiting_gives_up()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();

        _controller.SetPolicy(new ReconnectPolicy { KeepWaiting = false });

        Assert.Equal(ConnectionPhase.Failed, _controller.Current.Phase);
        Assert.True(_controller.Current.RetriesExhausted);
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(6, _factory.Links.Count);
    }

    [Fact]
    public void A_phone_that_keeps_closing_the_link_is_not_reconnected_for_ever()
    {
        ConnectAndOpen(Phone);

        // Each automatic reconnect succeeds and is closed again within seconds.
        for (var round = 0; round < 3; round++)
        {
            _factory.Latest.RaiseClosed();
            WaitFor(ConnectionPhase.WaitingToRetry);
            _time.Advance(TimeSpan.FromSeconds(2));
            _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
            WaitFor(ConnectionPhase.Connected);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        _factory.Latest.RaiseClosed();

        Assert.Equal(ConnectionPhase.Failed, _controller.Current.Phase);
        Assert.Equal(ConnectionFailure.Unstable, _controller.Current.Failure);
        var links = _factory.Links.Count;
        _controller.NotifyPhoneSeen();
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(links, _factory.Links.Count);
    }

    [Fact]
    public void A_restored_link_that_lasts_counts_as_recovered()
    {
        ConnectAndOpen(Phone);

        // Many drops, but each restored link holds for longer than the stability window.
        for (var round = 0; round < 8; round++)
        {
            _factory.Latest.RaiseClosed();
            WaitFor(ConnectionPhase.WaitingToRetry);
            _time.Advance(TimeSpan.FromSeconds(2));
            _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
            WaitFor(ConnectionPhase.Connected);
            _time.Advance(TimeSpan.FromMinutes(5));
        }

        _factory.Latest.RaiseClosed();

        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
    }

    [Fact]
    public void Connecting_by_hand_forgives_earlier_instability()
    {
        ConnectAndOpen(Phone);
        for (var round = 0; round < 2; round++)
        {
            _factory.Latest.RaiseClosed();
            WaitFor(ConnectionPhase.WaitingToRetry);
            _time.Advance(TimeSpan.FromSeconds(2));
            _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
            WaitFor(ConnectionPhase.Connected);
        }

        _controller.Disconnect();
        ConnectAndOpen(Phone);
        _factory.Latest.RaiseClosed();
        WaitFor(ConnectionPhase.WaitingToRetry);
        _time.Advance(TimeSpan.FromSeconds(2));
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
        _factory.Latest.RaiseClosed();

        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
    }

    [Fact]
    public void A_refusal_while_waiting_for_the_phone_ends_the_wait()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();
        _time.Advance(TimeSpan.FromMinutes(2));

        _factory.Latest.CompleteOpen(LinkOpenStatus.DeniedBySystem);

        WaitFor(ConnectionPhase.Failed);
        Assert.Equal(ConnectionFailure.DeniedBySystem, _controller.Current.Failure);
        var links = _factory.Links.Count;
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(links, _factory.Links.Count);
    }

    [Fact]
    public void The_last_failed_retry_is_not_mistaken_for_the_phone_returning()
    {
        ConnectAndOpen(Phone);
        ExhaustRetries();

        // Windows reports the phone "connected" for a moment because of the attempt that just failed.
        _controller.NotifyPhoneSeen();

        Assert.Equal(6, _factory.Links.Count);
        Assert.Equal(ConnectionPhase.WaitingForPhone, _controller.Current.Phase);
    }

    [Fact]
    public void Manual_disconnect_cancels_a_pending_retry()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);

        _controller.Disconnect();

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void Manual_disconnect_while_connecting_cancels_and_ignores_a_late_success()
    {
        _controller.Connect(Phone, ConnectOrigin.Manual);
        var link = _factory.Latest;

        _controller.Disconnect();

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.True(link.CancellationRequested);
        Assert.True(link.Disposed);

        link.CompleteOpen(LinkOpenStatus.Opened);

        Until(() => _log.Contains("Ignored stale result (Opened)"));
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.Equal(1, link.DisposeCalls);
    }

    [Fact]
    public void Manual_disconnect_during_a_retry_attempt_stops_all_further_retries()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        _time.Advance(TimeSpan.FromSeconds(2));
        var retryLink = _factory.Latest;
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);

        _controller.Disconnect();
        retryLink.CompleteOpen(LinkOpenStatus.TimedOut);

        Until(() => _log.Contains("Ignored stale result (TimedOut)"));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, _factory.Links.Count);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void Closed_event_from_a_released_link_cannot_trigger_a_reconnect()
    {
        var link = ConnectAndOpen(Phone);
        _controller.Disconnect();

        Assert.Equal(0, link.ClosedSubscribers);
        link.RaiseClosed();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void Nothing_reconnects_after_a_manual_disconnect()
    {
        ConnectAndOpen(Phone);
        _controller.Disconnect();

        _controller.SetBluetoothAvailable(false);
        _controller.SetBluetoothAvailable(true);
        _controller.OnSuspending();
        _controller.OnResumed();
        _controller.SetPolicy(new ReconnectPolicy { MaxRetries = 10 });
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void Manual_connect_after_a_manual_disconnect_works_again()
    {
        ConnectAndOpen(Phone);
        _controller.Disconnect();

        ConnectAndOpen(Phone);

        Assert.Equal(2, _factory.Links.Count);
        Assert.Equal(ConnectionPhase.Connected, _controller.Current.Phase);
    }

    [Fact]
    public void Drop_without_automatic_reconnection_reports_the_lost_connection()
    {
        Recreate(ReconnectPolicy.Disabled);
        ConnectAndOpen(Phone).RaiseClosed();

        Assert.Equal(ConnectionPhase.Failed, _controller.Current.Phase);
        Assert.Equal(ConnectionFailure.ConnectionLost, _controller.Current.Failure);
        Assert.False(_controller.Current.RetriesExhausted);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void Session_start_connect_retries_when_the_first_attempt_fails()
    {
        _controller.Connect(Phone, ConnectOrigin.SessionStart);
        _factory.Latest.CompleteOpen(LinkOpenStatus.DeviceUnavailable, "not reachable");

        WaitFor(ConnectionPhase.WaitingToRetry);
        Assert.Equal(1, _controller.Current.RetryAttempt);
        Assert.Equal(ConnectionFailure.DeviceUnavailable, _controller.Current.Failure);
        Assert.Equal("not reachable", _controller.Current.FailureDetail);
    }

    [Fact]
    public void Session_start_connect_is_not_retried_when_automatic_reconnection_is_off()
    {
        Recreate(ReconnectPolicy.Disabled);
        _controller.Connect(Phone, ConnectOrigin.SessionStart);
        _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);

        WaitFor(ConnectionPhase.Failed);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void A_denial_by_windows_is_not_retried()
    {
        _controller.Connect(Phone, ConnectOrigin.SessionStart);
        _factory.Latest.CompleteOpen(LinkOpenStatus.DeniedBySystem);

        WaitFor(ConnectionPhase.Failed);
        Assert.Equal(ConnectionFailure.DeniedBySystem, _controller.Current.Failure);
        Assert.False(_controller.Current.RetriesExhausted);
    }

    [Fact]
    public void Connecting_to_another_device_releases_the_current_link()
    {
        var phoneLink = ConnectAndOpen(Phone);

        Assert.True(_controller.Connect(Tablet, ConnectOrigin.Manual));

        Assert.True(phoneLink.Disposed);
        Assert.Equal(0, phoneLink.ClosedSubscribers);
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
        Assert.Equal(Tablet, _controller.Current.Device);
        Assert.Equal(Tablet, _factory.Latest.Device);
    }

    [Fact]
    public void Link_that_closes_while_opening_is_a_failure_even_if_open_reports_success()
    {
        _controller.Connect(Phone, ConnectOrigin.Manual);
        var link = _factory.Latest;

        link.RaiseClosed();
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
        link.CompleteOpen(LinkOpenStatus.Opened);

        WaitFor(ConnectionPhase.Failed);
        Assert.Equal(ConnectionFailure.ConnectionLost, _controller.Current.Failure);
        Assert.True(link.Disposed);
    }

    [Fact]
    public void Exception_from_open_is_reported_as_a_failure()
    {
        _controller.Connect(Phone, ConnectOrigin.Manual);
        _factory.Latest.ThrowFromOpen(new InvalidOperationException("boom"));

        WaitFor(ConnectionPhase.Failed);
        Assert.Equal(ConnectionFailure.Unknown, _controller.Current.Failure);
        Assert.Equal("boom", _controller.Current.FailureDetail);
    }

    [Fact]
    public void Bluetooth_off_while_connected_waits_and_reconnects_when_it_returns()
    {
        var link = ConnectAndOpen(Phone);

        _controller.SetBluetoothAvailable(false);

        Assert.Equal(ConnectionPhase.WaitingForBluetooth, _controller.Current.Phase);
        Assert.True(link.Disposed);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);

        _controller.SetBluetoothAvailable(true);

        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
        Assert.Equal(1, _controller.Current.RetryAttempt);
        _time.Advance(TimeSpan.FromSeconds(2));
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
    }

    [Fact]
    public void Drop_reported_before_the_radio_change_still_ends_up_waiting_for_bluetooth()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);

        _controller.SetBluetoothAvailable(false);

        Assert.Equal(ConnectionPhase.WaitingForBluetooth, _controller.Current.Phase);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void Manual_connect_with_bluetooth_off_fails_at_once_and_clears_when_bluetooth_returns()
    {
        _controller.SetBluetoothAvailable(false);

        _controller.Connect(Phone, ConnectOrigin.Manual);

        Assert.Equal(ConnectionPhase.Failed, _controller.Current.Phase);
        Assert.Equal(ConnectionFailure.BluetoothOff, _controller.Current.Failure);
        Assert.Empty(_factory.Links);

        _controller.SetBluetoothAvailable(true);

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.Empty(_factory.Links);
    }

    [Fact]
    public void Bluetooth_off_without_automatic_reconnection_is_an_error_not_a_wait()
    {
        Recreate(ReconnectPolicy.Disabled);
        ConnectAndOpen(Phone);

        _controller.SetBluetoothAvailable(false);

        Assert.Equal(ConnectionPhase.Failed, _controller.Current.Phase);
        Assert.Equal(ConnectionFailure.BluetoothOff, _controller.Current.Failure);
    }

    [Fact]
    public void Sleep_releases_the_link_and_resume_restores_it()
    {
        var link = ConnectAndOpen(Phone);

        _controller.OnSuspending();

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.True(link.Disposed);
        _time.Advance(TimeSpan.FromHours(8));
        Assert.Single(_factory.Links);

        _controller.OnResumed();

        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
        _time.Advance(TimeSpan.FromSeconds(2));
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
    }

    [Fact]
    public void Resume_restores_a_reconnection_that_sleep_interrupted()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);

        _controller.OnSuspending();
        _controller.OnResumed();

        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);
        Assert.Equal(1, _controller.Current.RetryAttempt);
    }

    [Fact]
    public void Resume_does_nothing_without_automatic_reconnection()
    {
        Recreate(ReconnectPolicy.Disabled);
        ConnectAndOpen(Phone);

        _controller.OnSuspending();
        _controller.OnResumed();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void Manual_disconnect_while_asleep_cancels_the_resume_reconnect()
    {
        ConnectAndOpen(Phone);
        _controller.OnSuspending();

        _controller.Disconnect();
        _controller.OnResumed();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void Turning_automatic_reconnection_off_cancels_a_pending_retry()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);

        _controller.SetPolicy(ReconnectPolicy.Disabled);

        Assert.Equal(ConnectionPhase.Failed, _controller.Current.Phase);
        Assert.Equal(ConnectionFailure.ConnectionLost, _controller.Current.Failure);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
    }

    [Fact]
    public void Manual_connect_during_a_retry_wait_replaces_the_scheduled_retry()
    {
        ConnectAndOpen(Phone).RaiseClosed();
        Assert.Equal(ConnectionPhase.WaitingToRetry, _controller.Current.Phase);

        Assert.True(_controller.Connect(Phone, ConnectOrigin.Manual));
        Assert.Equal(2, _factory.Links.Count);
        Assert.Equal(0, _controller.Current.RetryAttempt);

        // The superseded timer must not start a third attempt alongside the manual one.
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, _factory.Links.Count);
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);
    }

    [Fact]
    public void Reopen_replaces_an_established_link_with_a_fresh_one()
    {
        var first = ConnectAndOpen(Phone);

        Assert.True(_controller.Reopen("test"));

        Assert.True(first.Disposed);
        Assert.Equal(0, first.ClosedSubscribers);
        Assert.Equal(2, _factory.Links.Count);
        Assert.Equal(ConnectionPhase.Connecting, _controller.Current.Phase);

        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
    }

    [Fact]
    public void Reopen_does_nothing_unless_a_link_is_connected()
    {
        Assert.False(_controller.Reopen("nothing to reopen"));

        _controller.Connect(Phone, ConnectOrigin.Manual);
        Assert.False(_controller.Reopen("still connecting"));
        Assert.Single(_factory.Links);

        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
        _controller.Disconnect();

        Assert.False(_controller.Reopen("the user disconnected"));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(_factory.Links);
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
    }

    [Fact]
    public void A_reopen_that_fails_is_recovered_like_a_dropped_link()
    {
        ConnectAndOpen(Phone);

        _controller.Reopen("test");
        _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);

        WaitFor(ConnectionPhase.WaitingToRetry);
        Assert.Equal(1, _controller.Current.RetryAttempt);
    }

    [Fact]
    public void Changed_is_raised_for_each_transition()
    {
        var phases = new List<ConnectionPhase>();
        _controller.Changed += (_, _) => phases.Add(_controller.Current.Phase);

        ConnectAndOpen(Phone);
        _controller.Disconnect();

        Assert.Equal([ConnectionPhase.Connecting, ConnectionPhase.Connected, ConnectionPhase.Disconnected], phases);
    }

    [Fact]
    public void A_throwing_listener_does_not_break_the_controller()
    {
        _controller.Changed += (_, _) => throw new InvalidOperationException("listener bug");

        ConnectAndOpen(Phone);

        Assert.Equal(ConnectionPhase.Connected, _controller.Current.Phase);
        Assert.True(_log.Contains("listener bug"));
    }

    [Fact]
    public void Dispose_releases_the_link_and_rejects_further_requests()
    {
        var link = ConnectAndOpen(Phone);
        var raised = 0;
        _controller.Changed += (_, _) => raised++;

        _controller.Dispose();

        Assert.True(link.Disposed);
        Assert.Equal(0, link.ClosedSubscribers);
        Assert.False(_controller.Connect(Phone, ConnectOrigin.Manual));
        Assert.Equal(0, raised);
        Assert.Single(_factory.Links);
    }

    [Fact]
    public async Task Concurrent_requests_never_leak_a_link_or_resurrect_a_connection()
    {
        var random = new Random(1234);
        var seeds = Enumerable.Range(0, 8).Select(_ => random.Next()).ToArray();

        await Task.WhenAll(seeds.Select(seed => Task.Run(() =>
        {
            var rng = new Random(seed);
            for (var i = 0; i < 400; i++)
            {
                switch (rng.Next(9))
                {
                    case 0: _controller.Connect(Phone, ConnectOrigin.Manual); break;
                    case 1: _controller.Connect(Tablet, ConnectOrigin.SessionStart); break;
                    case 2: _controller.Disconnect(); break;
                    case 3: _factory.LatestOrDefault?.CompleteOpen(LinkOpenStatus.Opened); break;
                    case 4: _factory.LatestOrDefault?.CompleteOpen(LinkOpenStatus.TimedOut); break;
                    case 5: _factory.LatestOrDefault?.RaiseClosed(); break;
                    case 6: _controller.SetBluetoothAvailable(rng.Next(2) == 0); break;
                    case 7: _time.Advance(TimeSpan.FromSeconds(rng.Next(1, 40))); break;
                    default: _controller.OnSuspending(); _controller.OnResumed(); break;
                }
            }
        })));

        _controller.Disconnect();
        foreach (var link in _factory.Links)
        {
            link.CompleteOpen(LinkOpenStatus.Opened);
        }

        Until(() => _factory.Links.All(link => link.Disposed));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.All(_factory.Links, link => Assert.Equal(1, link.DisposeCalls));
        Assert.All(_factory.Links, link => Assert.True(link.OpenCalls <= 1));
    }

    [Fact]
    public void The_sessions_automatic_connect_gives_way_to_a_choice_the_user_already_made()
    {
        // The app is still starting when the user connects and then cancels.
        _controller.Connect(Phone, ConnectOrigin.Manual);
        _controller.Disconnect();

        Assert.False(_controller.Connect(Phone, ConnectOrigin.SessionStart));

        Assert.Equal(ConnectionPhase.Disconnected, _controller.Current.Phase);
        Assert.Single(_factory.Links);
        Assert.True(_log.Contains("already changed by hand"));
    }

    [Fact]
    public void A_new_link_is_not_opened_while_the_old_one_is_still_closing()
    {
        _controller.Connect(Phone, ConnectOrigin.Manual);
        _factory.Latest.CompleteOpen(LinkOpenStatus.Opened);
        WaitFor(ConnectionPhase.Connected);
        var first = _factory.Latest;
        using var release = new ManualResetEventSlim();
        first.HoldDispose = release;

        // One thread is part-way through closing the old link when another asks for a new one.
        var disconnecting = Task.Run(() => _controller.Disconnect());
        Assert.True(first.DisposeStarted.Wait(TimeSpan.FromSeconds(5)));
        var connecting = Task.Run(() => _controller.Connect(Phone, ConnectOrigin.Manual));
        Until(() => _factory.Links.Count == 2);
        Thread.Sleep(150);

        Assert.Equal(0, _factory.Latest.OpenCalls);

        release.Set();
        Assert.True(Task.WaitAll([disconnecting, connecting], TimeSpan.FromSeconds(5)));
        Until(() => _factory.Latest.OpenCalls == 1);
    }

    private ConnectionController Create(ReconnectPolicy policy) => new(_factory, policy, _time, _log);

    private void Recreate(ReconnectPolicy policy)
    {
        _controller.Dispose();
        _controller = Create(policy);
    }

    /// <summary>Drops the current link and fails all five default retries, leaving the controller past its retry limit.</summary>
    private void ExhaustRetries()
    {
        _factory.Latest.RaiseClosed();
        foreach (var seconds in new[] { 2, 4, 8, 16, 32 })
        {
            WaitFor(ConnectionPhase.WaitingToRetry);
            _time.Advance(TimeSpan.FromSeconds(seconds));
            _factory.Latest.CompleteOpen(LinkOpenStatus.TimedOut);
        }

        WaitFor(ConnectionPhase.WaitingForPhone);
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
