#if DEBUG
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Bluetooth;

/// <summary>
/// Development aid, compiled into Debug builds only and enabled with SOUNDBRIDGE_SIMULATE. It stands in for
/// the Bluetooth link so every UI state can be reached without a phone. No Bluetooth call is made.
/// <para>
/// The value is a comma-separated script applied to successive attempts, the last entry repeating:
/// "ok" opens, "fail" times out, "deny" is refused, "drop" opens and then closes after a few seconds as if the
/// phone went out of range, and "bye" does the same as if the phone were disconnected on purpose: it still
/// answers the reachability check afterwards.
/// Example: SOUNDBRIDGE_SIMULATE=drop,fail,ok,bye
/// </para>
/// <para>
/// The reachability check answers after a second: reachable if the latest attempt's step was "bye" or
/// "ok", unreachable otherwise.
/// </para>
/// </summary>
internal sealed class SimulatedAudioLinkFactory(string script, ILog log) : IAudioLinkFactory, IPhoneReachability
{
    private readonly string[] _steps = script.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private int _attempt;
    private volatile string _lastStep = "ok";

    public IAudioLink Create(AudioSource device)
    {
        var index = Interlocked.Increment(ref _attempt) - 1;
        var step = _steps.Length == 0 ? "ok" : _steps[Math.Min(index, _steps.Length - 1)];
        _lastStep = step;
        log.Warn("Simulation", $"Attempt {index + 1} for '{device.Name}' uses simulated step '{step}'. No Bluetooth call is made.");
        return new SimulatedLink(step);
    }

    public async Task<PhoneReachability> ProbeAsync(AudioSource device, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        var answer = _lastStep is "bye" or "ok" ? PhoneReachability.Reachable : PhoneReachability.Unreachable;
        log.Warn("Simulation", $"Simulated reachability check for '{device.Name}': {answer}.");
        return answer;
    }

    private sealed class SimulatedLink(string step) : IAudioLink
    {
        private readonly CancellationTokenSource _lifetime = new();

        public event EventHandler? Closed;

        public async Task<LinkOpenResult> OpenAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await Task.Delay(TimeSpan.FromSeconds(1.8), linked.Token).ConfigureAwait(false);

            switch (step)
            {
                case "fail":
                    return new LinkOpenResult(LinkOpenStatus.TimedOut);
                case "deny":
                    return new LinkOpenResult(LinkOpenStatus.DeniedBySystem);
                case "drop" or "bye":
                    _ = DropLaterAsync();
                    return new LinkOpenResult(LinkOpenStatus.Opened);
                default:
                    return new LinkOpenResult(LinkOpenStatus.Opened);
            }
        }

        public void Dispose() => _lifetime.Cancel();

        private async Task DropLaterAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _lifetime.Token).ConfigureAwait(false);
                Closed?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
#endif
