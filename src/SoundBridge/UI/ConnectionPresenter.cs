using SoundBridge.Core.Calls;
using SoundBridge.Core.Connection;

namespace SoundBridge.UI;

internal enum StatusTone
{
    Idle,
    Busy,
    On,
    Waiting,
    Error,
}

internal sealed record ConnectionPresentation(StatusTone Tone, string Title, string Detail, string ActionText);

/// <summary>
/// Turns a connection snapshot into words. It only describes the link: an open link does not mean the phone
/// is playing anything, and nothing here claims that it is.
/// </summary>
internal static class ConnectionPresenter
{
    public static ConnectionPresentation Describe(ConnectionSnapshot snapshot, bool hasTarget, bool hasSources, DateTimeOffset now)
    {
        var max = snapshot.MaxRetries;
        return snapshot.Phase switch
        {
            ConnectionPhase.Connecting when snapshot.BackgroundCheck =>
                new(StatusTone.Busy, "Checking for the phone…", "Seeing whether it is back in range.", "Cancel"),

            ConnectionPhase.WaitingForPhone =>
                new(StatusTone.Waiting, "Waiting for the phone", "It is out of reach. SoundBridge checks every couple of minutes and reconnects as soon as it is back.", "Cancel"),

            ConnectionPhase.Connecting when snapshot.RetryAttempt == 0 =>
                new(StatusTone.Busy, "Connecting…", "Asking Windows to open the audio link to this phone.", "Cancel"),

            ConnectionPhase.Connecting =>
                new(StatusTone.Busy, "Reconnecting…", $"Attempt {snapshot.RetryAttempt} of {max}.", "Cancel"),

            ConnectionPhase.Connected =>
                new(StatusTone.On, "Connected", $"Since {snapshot.ConnectedAt:t}. Media played on the phone comes out of this PC's current output device.", "Disconnect"),

            ConnectionPhase.WaitingToRetry =>
                new(StatusTone.Waiting, RetryTitle(snapshot, now), $"Attempt {snapshot.RetryAttempt} of {max} is next. {Reason(snapshot)}".TrimEnd(), "Cancel"),

            ConnectionPhase.WaitingForBluetooth =>
                new(StatusTone.Waiting, "Waiting for Bluetooth", "Bluetooth is off on this PC. SoundBridge will reconnect when it is back on.", "Cancel"),

            ConnectionPhase.Failed =>
                new(StatusTone.Error, FailureTitle(snapshot), FailureDetail(snapshot), "Connect"),

            _ when !hasTarget && !hasSources =>
                new(StatusTone.Idle, "Not connected", "Pair your phone with this PC in Windows Bluetooth settings, then choose it here.", "Connect"),

            _ when !hasTarget =>
                new(StatusTone.Idle, "Not connected", "Choose a phone below to get started.", "Connect"),

            _ => new(StatusTone.Idle, "Not connected", "Connect to hear this phone alongside this PC's own audio.", "Connect"),
        };
    }

    /// <summary>One line on call routing. Like the rest, it reports what Windows says, not what is audible.</summary>
    public static string DescribeCalls(CallsSnapshot snapshot, CallSupport support)
    {
        if (support == CallSupport.NeedsNewerWindows)
        {
            return "Not available: moving calls to the PC needs Windows 11.";
        }

        if (support == CallSupport.NeedsInstalledApp)
        {
            return @"Not available in this copy. Windows only lets an installed app take calls: run packaging\Install.ps1, then start SoundBridge from the Start menu.";
        }

        return snapshot.State switch
        {
            CallsState.Off => "Off.",
            CallsState.NoPhone => "Choose a phone first.",
            CallsState.Starting => "Setting up…",
            CallsState.Ready when !snapshot.LinkUp => "On, but the phone is not connected for calls right now. Calls will come here once it is.",
            CallsState.Ready => "Ready. Answer on your phone or here and the call moves to this PC.",
            CallsState.InCall when snapshot.CallOnPc => "Call in progress on this PC.",
            CallsState.InCall => "Call in progress on the phone.",
            CallsState.Failed => snapshot.Failure switch
            {
                CallClaimStatus.PhoneNotFound => "Windows does not offer this phone for calls. Check that it is paired and that phone calls are allowed for this PC in the phone's Bluetooth settings.",
                CallClaimStatus.AccessDenied => "Windows refused access to this phone's calls. Another app, such as Phone Link, may already handle them; removing and re-pairing the phone clears that.",
                CallClaimStatus.NotRegistered => "Windows did not register SoundBridge for this phone's calls.",
                _ when !string.IsNullOrWhiteSpace(snapshot.FailureDetail) => $"Could not set up calls. Windows reported: {snapshot.FailureDetail}",
                _ => "Could not set up calls.",
            },
            _ => string.Empty,
        };
    }

    private static string RetryTitle(ConnectionSnapshot snapshot, DateTimeOffset now)
    {
        var seconds = snapshot.NextRetryAt is { } at ? (int)Math.Ceiling((at - now).TotalSeconds) : 0;
        return seconds > 0 ? $"Reconnecting in {seconds} s" : "Reconnecting…";
    }

    private static string FailureTitle(ConnectionSnapshot snapshot) => snapshot.Failure switch
    {
        ConnectionFailure.ConnectionLost => "Connection lost",
        ConnectionFailure.Unstable => "Stopped reconnecting",
        ConnectionFailure.BluetoothOff => "Bluetooth is off",
        _ when snapshot.RetriesExhausted => "Couldn't reconnect",
        _ => "Couldn't connect",
    };

    private static string FailureDetail(ConnectionSnapshot snapshot)
    {
        var reason = Reason(snapshot);
        return snapshot.RetriesExhausted
            ? $"{reason} Stopped after {snapshot.MaxRetries} reconnection attempts.".TrimStart()
            : reason;
    }

    private static string Reason(ConnectionSnapshot snapshot) => snapshot.Failure switch
    {
        ConnectionFailure.BluetoothOff => "Turn Bluetooth on for this PC, then connect again.",
        ConnectionFailure.DeviceUnavailable => "Windows couldn't reach this phone. Check that it is paired, nearby and has Bluetooth on.",
        ConnectionFailure.TimedOut => "The phone didn't answer in time. Check that it is nearby and has Bluetooth on.",
        ConnectionFailure.DeniedBySystem => "Windows refused the connection. Another app may already be receiving audio from this phone.",
        ConnectionFailure.ConnectionLost => "The phone disconnected or moved out of range.",
        ConnectionFailure.Unstable => "The link was restored several times and the phone closed it again each time. If you switched the phone to another speaker or disconnected this PC on the phone, that is why. Connect when you want it back.",
        ConnectionFailure.Unknown when !string.IsNullOrWhiteSpace(snapshot.FailureDetail) => $"Windows reported: {snapshot.FailureDetail}",
        ConnectionFailure.Unknown => "Windows reported an error without further detail.",
        _ => string.Empty,
    };
}
