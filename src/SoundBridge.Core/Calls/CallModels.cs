using SoundBridge.Core.Connection;

namespace SoundBridge.Core.Calls;

public enum CallPhase
{
    Incoming,
    Dialing,
    Talking,
    Held,
    Other,
}

/// <summary>A call Windows reports on the phone's line. <see cref="OnPc"/> is true when its audio is on this PC.</summary>
public sealed record ActiveCall(string Id, CallPhase Phase, bool OnPc);

public enum CallClaimStatus
{
    Claimed,
    PhoneNotFound,
    AccessDenied,
    NotRegistered,
    Failed,
}

public sealed record CallClaimResult(CallClaimStatus Status, string? Detail = null);

/// <summary>Whether this copy of the app, on this version of Windows, can take the hands-free role at all.</summary>
public enum CallSupport
{
    Available,

    /// <summary>Windows only grants the role to an installed (packaged) app.</summary>
    NeedsInstalledApp,

    /// <summary>Moving a call's audio needs the calling API that arrived with Windows 11.</summary>
    NeedsNewerWindows,
}

/// <summary>
/// The Windows side of hands-free calling for one phone. Claiming registers this app as the phone's
/// hands-free device; disposing releases that registration.
/// </summary>
public interface ICallTransport : IDisposable
{
    /// <summary>Raised on any thread when calls, their audio device, or the routing permission may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>True when Windows currently allows a call's audio to be moved to this PC.</summary>
    bool CanRouteToPc { get; }

    Task<CallClaimResult> ClaimAsync(CancellationToken cancellationToken);

    /// <summary>Asks Windows to bring the hands-free link up. Best effort; the phone may also do so itself.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ActiveCall>> GetActiveCallsAsync(CancellationToken cancellationToken);

    Task<bool> MoveToPcAsync(string callId, CancellationToken cancellationToken);
}

public interface ICallTransportFactory
{
    CallSupport Support { get; }

    ICallTransport Create(AudioSource phone);
}

public enum CallsState
{
    /// <summary>The user has not turned call routing on.</summary>
    Off,

    /// <summary>Turned on, but this copy of the app or this Windows version cannot do it.</summary>
    Unavailable,

    /// <summary>Turned on, but no phone is selected.</summary>
    NoPhone,

    Starting,

    /// <summary>The role is held; calls will be moved to this PC as they are answered.</summary>
    Ready,

    InCall,

    Failed,
}

public sealed record CallsSnapshot
{
    public static readonly CallsSnapshot Off = new();

    public CallsState State { get; init; } = CallsState.Off;

    public CallSupport Support { get; init; } = CallSupport.Available;

    public CallClaimStatus Failure { get; init; } = CallClaimStatus.Claimed;

    public string? FailureDetail { get; init; }

    /// <summary>Meaningful while <see cref="State"/> is InCall: whether the call's audio is on this PC.</summary>
    public bool CallOnPc { get; init; }
}
