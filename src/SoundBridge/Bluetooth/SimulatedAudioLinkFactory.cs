#if DEBUG
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Bluetooth;

/// <summary>
/// Development aid, compiled into Debug builds only and enabled with SOUNDBRIDGE_SIMULATE. It stands in for
/// the Bluetooth link so every UI state can be reached without a phone. No Bluetooth call is made.
/// <para>
/// The value is a comma-separated script applied to successive attempts, the last entry repeating:
/// "ok" opens, "fail" times out, "deny" is refused, "drop" opens and then closes after a few seconds.
/// Example: SOUNDBRIDGE_SIMULATE=drop,fail,ok
/// </para>
/// </summary>
internal sealed class SimulatedAudioLinkFactory(string script, ILog log) : IAudioLinkFactory
{
    private readonly string[] _steps = script.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private int _attempt;

    public IAudioLink Create(AudioSource device)
    {
        var index = Interlocked.Increment(ref _attempt) - 1;
        var step = _steps.Length == 0 ? "ok" : _steps[Math.Min(index, _steps.Length - 1)];
        log.Warn("Simulation", $"Attempt {index + 1} for '{device.Name}' uses simulated step '{step}'. No Bluetooth call is made.");
        return new SimulatedLink(step);
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
                case "drop":
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
