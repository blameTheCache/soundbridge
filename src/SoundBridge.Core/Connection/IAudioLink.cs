namespace SoundBridge.Core.Connection;

public enum LinkOpenStatus
{
    Opened,
    TimedOut,
    DeniedBySystem,
    DeviceUnavailable,
    Failed,
}

public sealed record LinkOpenResult(LinkOpenStatus Status, string? Detail = null);

/// <summary>
/// One audio link to one device, used for a single open attempt and the connection that follows it.
/// The controller is the only owner: it opens the link once and disposes it exactly once.
/// </summary>
public interface IAudioLink : IDisposable
{
    /// <summary>
    /// Raised when the system or the remote device closes the link. May be raised on any thread, including
    /// while <see cref="OpenAsync"/> is still running. Not raised as a result of <see cref="IDisposable.Dispose"/>.
    /// </summary>
    event EventHandler? Closed;

    /// <summary>
    /// Opens the link. Expected failures are returned as a status; cancellation surfaces as
    /// <see cref="OperationCanceledException"/>. Disposing the link while this is running must abandon the attempt.
    /// </summary>
    Task<LinkOpenResult> OpenAsync(CancellationToken cancellationToken);
}

public interface IAudioLinkFactory
{
    /// <summary>Creates an unopened link. Must be cheap; the real work happens in <see cref="IAudioLink.OpenAsync"/>.</summary>
    IAudioLink Create(AudioSource device);
}
