#if DEBUG
using SoundBridge.Core.Calls;
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Bluetooth;

/// <summary>
/// Development aid, compiled into Debug builds only and enabled with SOUNDBRIDGE_SIMULATE_CALLS. It stands in
/// for the hands-free role so the call strip and the incoming-call prompt can be seen without a phone call.
/// Seven seconds after it is "claimed" a call from "Test Caller" rings; if left alone it stops after a while.
/// </summary>
internal sealed class SimulatedCallTransportFactory(ILog log) : ICallTransportFactory
{
    public CallSupport Support => CallSupport.Available;

    public ICallTransport Create(AudioSource phone)
    {
        log.Warn("Simulation", "Call routing is simulated. No Bluetooth call is made and no real call is affected.");
        return new SimulatedCallTransport();
    }

    private sealed class SimulatedCallTransport : ICallTransport
    {
        private readonly CancellationTokenSource _lifetime = new();
        private volatile IReadOnlyList<ActiveCall> _calls = [];

        public event EventHandler? Changed;

        public bool CanRouteToPc => true;

        public bool LinkUp => true;

        public Task<CallClaimResult> ClaimAsync(CancellationToken cancellationToken)
        {
            _ = RingLaterAsync();
            return Task.FromResult(new CallClaimResult(CallClaimStatus.Claimed));
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ActiveCall>> GetActiveCallsAsync(CancellationToken cancellationToken) => Task.FromResult(_calls);

        public Task<bool> MoveAsync(string callId, bool toPc, CancellationToken cancellationToken) => Update(call => call with { OnPc = toPc });

        public Task<bool> AnswerAsync(string callId, CancellationToken cancellationToken) => Update(call => call with { Phase = CallPhase.Talking });

        public Task<bool> DeclineAsync(string callId, CancellationToken cancellationToken) => Clear();

        public Task<bool> EndAsync(string callId, CancellationToken cancellationToken) => Clear();

        public Task<bool> SetMutedAsync(string callId, bool muted, CancellationToken cancellationToken) => Update(call => call with { Muted = muted });

        public void Dispose() => _lifetime.Cancel();

        private async Task RingLaterAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(7), _lifetime.Token).ConfigureAwait(false);
                _calls = [new ActiveCall("simulated-call", CallPhase.Incoming, OnPc: false, Caller: "Test Caller")];
                Changed?.Invoke(this, EventArgs.Empty);

                await Task.Delay(TimeSpan.FromSeconds(40), _lifetime.Token).ConfigureAwait(false);
                await Clear().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private Task<bool> Update(Func<ActiveCall, ActiveCall> change)
        {
            _calls = [.. _calls.Select(change)];
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }

        private Task<bool> Clear()
        {
            _calls = [];
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }
    }
}
#endif
