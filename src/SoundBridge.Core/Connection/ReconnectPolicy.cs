namespace SoundBridge.Core.Connection;

/// <summary>Bounded exponential backoff for automatic reconnection.</summary>
public sealed record ReconnectPolicy
{
    public const int MinRetries = 1;
    public const int MaxRetriesLimit = 10;

    public static readonly ReconnectPolicy Default = new();

    public static readonly ReconnectPolicy Disabled = new() { Enabled = false };

    public bool Enabled { get; init; } = true;

    public int MaxRetries { get; init; } = 5;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When the retries run out, keep the request alive: check for the phone now and then and reconnect once
    /// it answers. Still bounded, by <see cref="WaitLimit"/>.
    /// </summary>
    public bool KeepWaiting { get; init; } = true;

    /// <summary>How often to check for the phone while waiting. Each check is one ordinary open attempt.</summary>
    public TimeSpan WaitCheckInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long to go on waiting before giving up for good.</summary>
    public TimeSpan WaitLimit { get; init; } = TimeSpan.FromHours(12);

    /// <summary>A sighting of the phone is ignored if a check ended more recently than this.</summary>
    public TimeSpan MinimumCheckGap { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A restored link that closes again sooner than this did not really recover.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many restored links in a row may close again that quickly before reconnection stops. Without this
    /// limit each successful reconnect would renew the retry budget, and a phone that keeps closing the link
    /// (because its owner chose another output, say) would be reconnected for ever.
    /// </summary>
    public int MaxUnstableRecoveries { get; init; } = 3;

    /// <summary>Delay before the given 1-based retry: base, 2x base, 4x base, ... capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan DelayBefore(int retry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retry, 1);

        // Past 2^30 the cap has long since applied; clamping the exponent keeps the shift well-defined.
        var factor = 1L << Math.Min(retry - 1, 30);
        var ticks = BaseDelay.Ticks * (double)factor;
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
    }

    public static int ClampRetries(int value) => Math.Clamp(value, MinRetries, MaxRetriesLimit);
}
