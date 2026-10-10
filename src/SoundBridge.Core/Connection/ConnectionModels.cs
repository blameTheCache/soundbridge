namespace SoundBridge.Core.Connection;

/// <summary>A paired Bluetooth device that can act as an audio source for this PC.</summary>
public sealed record AudioSource(string Id, string Name);

public enum ConnectionPhase
{
    /// <summary>No link and nothing pending.</summary>
    Disconnected,

    /// <summary>An open request is in flight. <see cref="ConnectionSnapshot.RetryAttempt"/> is 0 for a first attempt.</summary>
    Connecting,

    /// <summary>Windows reports the audio link as open.</summary>
    Connected,

    /// <summary>An automatic retry is scheduled for <see cref="ConnectionSnapshot.NextRetryAt"/>.</summary>
    WaitingToRetry,

    /// <summary>The Bluetooth radio is off; reconnection resumes when it comes back.</summary>
    WaitingForBluetooth,

    /// <summary>
    /// The quick retries ran out. The phone is checked for at a slow pace, and at once if it is seen, until
    /// <see cref="ReconnectPolicy.WaitLimit"/> has passed.
    /// </summary>
    WaitingForPhone,

    /// <summary>The last attempt failed and nothing further is scheduled.</summary>
    Failed,
}

public enum ConnectionFailure
{
    None,
    BluetoothOff,
    DeviceUnavailable,
    TimedOut,
    DeniedBySystem,
    ConnectionLost,

    /// <summary>The link was restored several times and closed again each time, so reconnection stopped.</summary>
    Unstable,

    Unknown,
}

public enum ConnectOrigin
{
    /// <summary>The user asked for it. A failed first attempt is reported immediately rather than retried.</summary>
    Manual,

    /// <summary>Automatic connect at the start of an app session. Retries follow the reconnect policy.</summary>
    SessionStart,
}

/// <summary>Immutable view of the connection state. Everything the UI shows about the link comes from here.</summary>
public sealed record ConnectionSnapshot
{
    public static readonly ConnectionSnapshot Idle = new();

    public ConnectionPhase Phase { get; init; } = ConnectionPhase.Disconnected;

    public AudioSource? Device { get; init; }

    /// <summary>0 while making or after a first attempt; n while waiting for or making the nth automatic retry.</summary>
    public int RetryAttempt { get; init; }

    public int MaxRetries { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }

    public ConnectionFailure Failure { get; init; }

    /// <summary>Raw detail from Windows for <see cref="Failure"/>, when there is one.</summary>
    public string? FailureDetail { get; init; }

    /// <summary>True when <see cref="Phase"/> is Failed because every allowed retry was used.</summary>
    public bool RetriesExhausted { get; init; }

    /// <summary>True while Connecting as one of the slow checks made from <see cref="ConnectionPhase.WaitingForPhone"/>.</summary>
    public bool BackgroundCheck { get; init; }

    /// <summary>True while a link is open or the controller is working towards one.</summary>
    public bool IsActive => Phase is not (ConnectionPhase.Disconnected or ConnectionPhase.Failed);
}
