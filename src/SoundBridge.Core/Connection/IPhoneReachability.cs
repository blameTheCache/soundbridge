namespace SoundBridge.Core.Connection;

public enum PhoneReachability
{
    /// <summary>The phone answered over Bluetooth, so it is in range with Bluetooth on.</summary>
    Reachable,

    /// <summary>The phone did not answer: out of range, Bluetooth off, or switched off.</summary>
    Unreachable,

    /// <summary>The check could not be made, so it says nothing either way.</summary>
    Unknown,
}

/// <summary>
/// Asks whether a phone is still within Bluetooth reach, without opening any audio or changing what the phone
/// is connected to.
/// <para>
/// Used when the audio link closes, to tell the two causes apart. A phone that still answers ended the link
/// itself: its owner disconnected this PC on the phone or sent the audio somewhere else, and reconnecting would
/// overrule them. A phone that does not answer was carried out of range or lost Bluetooth, and is reconnected.
/// </para>
/// </summary>
public interface IPhoneReachability
{
    /// <summary>
    /// Checks the phone. Should honour <paramref name="cancellationToken"/>; the caller also applies its own
    /// time limit and treats a check that runs past it as <see cref="PhoneReachability.Unreachable"/>.
    /// </summary>
    Task<PhoneReachability> ProbeAsync(AudioSource device, CancellationToken cancellationToken);
}
