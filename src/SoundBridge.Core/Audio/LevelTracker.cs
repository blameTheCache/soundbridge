namespace SoundBridge.Core.Audio;

/// <summary>
/// Turns raw peak readings of the phone's audio into what the indicator shows: a smoothed level, a short
/// history of recent levels, and whether audio is playing at all.
/// <para>
/// Everything here is derived from measured peaks. The history is level over time, not a frequency
/// spectrum: Windows reports how loud the stream is, not what is in it.
/// </para>
/// </summary>
public sealed class LevelTracker
{
    /// <summary>Number of history slots kept, oldest first.</summary>
    public const int HistoryLength = 48;

    /// <summary>Quieter than this (about -66 dB) counts as silence. A silent stream reads exactly zero.</summary>
    public const float SilenceThreshold = 0.0005f;

    private static readonly TimeSpan HistoryStep = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PlayingHold = TimeSpan.FromSeconds(1.5);
    private const float FloorDecibels = -60f;
    private const float DecayPerSecond = 2.2f;

    private readonly float[] _history = new float[HistoryLength];
    private int _next;
    private TimeSpan _sinceSound = PlayingHold;
    private TimeSpan _sinceStep;
    private float _stepPeak;

    /// <summary>Current level from 0 to 1 on a decibel scale: rises at once, falls gradually.</summary>
    public float Level { get; private set; }

    /// <summary>True while sound has been measured within the last second and a half.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>How far, from 0 to 1, the tracker is into the current history slot. Lets a display scroll smoothly.</summary>
    public float StepProgress => (float)Math.Clamp(_sinceStep / HistoryStep, 0, 1);

    /// <summary>Feeds one peak reading (0 to 1, linear) taken <paramref name="elapsed"/> after the previous one.</summary>
    /// <returns>True when <see cref="IsPlaying"/> changed.</returns>
    public bool Push(float peak, TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var target = Scale(peak);
        var decayed = Level - (DecayPerSecond * (float)elapsed.TotalSeconds);
        Level = Math.Clamp(Math.Max(target, decayed), 0f, 1f);

        _stepPeak = Math.Max(_stepPeak, target);
        _sinceStep += elapsed;
        while (_sinceStep >= HistoryStep)
        {
            _history[_next] = _stepPeak;
            _next = (_next + 1) % HistoryLength;
            _stepPeak = target;
            _sinceStep -= HistoryStep;
        }

        _sinceSound = peak > SilenceThreshold ? TimeSpan.Zero : Min(_sinceSound + elapsed, PlayingHold);
        var playing = _sinceSound < PlayingHold;
        if (playing == IsPlaying)
        {
            return false;
        }

        IsPlaying = playing;
        return true;
    }

    /// <summary>Copies the history into <paramref name="destination"/>, oldest first.</summary>
    public void CopyHistory(Span<float> destination)
    {
        var count = Math.Min(destination.Length, HistoryLength);
        for (var i = 0; i < count; i++)
        {
            destination[i] = _history[(_next + HistoryLength - count + i) % HistoryLength];
        }
    }

    /// <summary>Forgets everything, as when the link closes.</summary>
    /// <returns>True when <see cref="IsPlaying"/> changed.</returns>
    public bool Reset()
    {
        Array.Clear(_history);
        _next = 0;
        _stepPeak = 0;
        _sinceStep = TimeSpan.Zero;
        _sinceSound = PlayingHold;
        Level = 0;
        var changed = IsPlaying;
        IsPlaying = false;
        return changed;
    }

    /// <summary>Maps a linear peak onto 0..1 over a 60 dB range, which is roughly how loudness is heard.</summary>
    public static float Scale(float peak)
    {
        if (!(peak > SilenceThreshold))
        {
            return 0f;
        }

        var decibels = 20f * MathF.Log10(Math.Min(peak, 1f));
        return Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0f, 1f);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
