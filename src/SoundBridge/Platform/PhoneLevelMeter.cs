using System.Diagnostics;
using SoundBridge.Core.Audio;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Platform;

/// <summary>
/// Samples how loud the phone's music is, from the level meter Windows keeps for that audio session. This is
/// what lets the app say "playing" and draw a live indicator from a measurement instead of assuming that an
/// open link means sound.
/// <para>
/// It samples about thirty times a second, and only between <see cref="Start"/> and <see cref="Stop"/>. The
/// app runs it while its window is on screen and the link is open, so a hidden app does no sampling at all.
/// It reads a number Windows already computes; the audio itself is never captured.
/// </para>
/// </summary>
internal sealed class PhoneLevelMeter(ILog log) : IDisposable
{
    private const string Category = "Level";
    private static readonly TimeSpan SampleEvery = TimeSpan.FromMilliseconds(33);

    // The session is looked up again this often: Windows may replace it when the link is reopened.
    private static readonly TimeSpan LookupEvery = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly LevelTracker _tracker = new();
    private CancellationTokenSource? _run;
    private string? _phoneName;

    /// <summary>Raised on a background thread when audio starts or stops.</summary>
    public event EventHandler? PlayingChanged;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _tracker.IsPlaying;
            }
        }
    }

    /// <summary>Starts sampling the music of the phone called <paramref name="phoneName"/>. Does nothing if already doing so.</summary>
    public void Start(string phoneName)
    {
        CancellationTokenSource run;
        lock (_gate)
        {
            if (_run is not null && _phoneName == phoneName)
            {
                return;
            }

            _run?.Cancel();
            _run = run = new CancellationTokenSource();
            _phoneName = phoneName;
        }

        _ = Task.Run(() => RunAsync(phoneName, run.Token));
    }

    public void Stop()
    {
        lock (_gate)
        {
            _run?.Cancel();
            _run = null;
            _phoneName = null;
        }
    }

    /// <summary>
    /// Copies the recent levels (oldest first) for drawing and returns the current level. <paramref name="progress"/>
    /// says how far the newest history slot has filled, so the drawing can scroll smoothly between slots.
    /// </summary>
    public float Read(Span<float> history, out float progress)
    {
        lock (_gate)
        {
            _tracker.CopyHistory(history);
            progress = _tracker.StepProgress;
            return _tracker.Level;
        }
    }

    public void Dispose() => Stop();

    private async Task RunAsync(string phoneName, CancellationToken cancellation)
    {
        var meters = new List<IAudioMeterInformation>();
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        var lastLookup = TimeSpan.MinValue;
        var failureLogged = false;

        try
        {
            using var timer = new PeriodicTimer(SampleEvery);
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            {
                var now = clock.Elapsed;
                var elapsed = now - last;
                last = now;

                if (lastLookup == TimeSpan.MinValue || now - lastLookup >= LookupEvery)
                {
                    lastLookup = now;
                    try
                    {
                        meters = [.. CoreAudio.FindPhoneMusicSessions(phoneName).Select(session => (IAudioMeterInformation)session)];
                        failureLogged = false;
                    }
                    catch (Exception ex) when (CoreAudio.IsExpected(ex))
                    {
                        meters.Clear();
                        if (!failureLogged)
                        {
                            failureLogged = true;
                            log.Warn(Category, $"Could not reach the phone's level meter: {ex.Message} (0x{ex.HResult:X8})");
                        }
                    }
                }

                Record(Sample(meters, now), elapsed);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            bool changed;
            lock (_gate)
            {
                // A newer run may already have taken over; only the current one clears the readings.
                changed = (_run is null || _run.Token == cancellation) && _tracker.Reset();
            }

            if (changed)
            {
                PlayingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void Record(float peak, TimeSpan elapsed)
    {
        bool changed;
        lock (_gate)
        {
            changed = _tracker.Push(peak, elapsed);
        }

        if (changed)
        {
            PlayingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The loudest reading across the phone's sessions; a meter that stops answering is dropped until the next lookup.</summary>
    private static float Sample(List<IAudioMeterInformation> meters, TimeSpan now)
    {
#if DEBUG
        if (Simulated)
        {
            // A made-up, music-like envelope for looking at the indicator without a phone. Debug builds only.
            var t = now.TotalSeconds;
            var beat = Math.Pow(Math.Abs(Math.Sin(t * 3.6)), 6);
            var swell = 0.5 + (0.5 * Math.Sin(t * 0.7));
            return (float)(0.004 + (0.12 * beat * swell) + (0.02 * Math.Abs(Math.Sin(t * 11.3))));
        }
#endif
        var peak = 0f;
        for (var i = meters.Count - 1; i >= 0; i--)
        {
            if (meters[i].GetPeakValue(out var value) != 0)
            {
                meters.RemoveAt(i);
            }
            else if (value > peak)
            {
                peak = value;
            }
        }

        return peak;
    }

#if DEBUG
    private static readonly bool Simulated = Environment.GetEnvironmentVariable("SOUNDBRIDGE_SIMULATE_LEVEL") is { Length: > 0 };
#endif
}
