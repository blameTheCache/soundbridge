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
