namespace SoundBridge.Core.Audio;

/// <summary>How loud the phone's music is on this PC, as Windows reports it. <see cref="Level"/> runs from 0 to 1.</summary>
public sealed record PhoneVolume(float Level, bool Muted);

/// <summary>
/// Windows plays a phone's music as an audio session of its own on the output device, the same entry the
/// Volume mixer lists. It names that session after the endpoint the audio arrives on, for example
/// "Microphone (Pixel A2DP SNK)". The phone's call audio is a separate session ("… Hands-Free HF Audio").
/// </summary>
public static class PhoneAudioSessions
{
    private const string MusicMarker = "A2DP";

    /// <summary>
    /// True when <paramref name="sessionDisplayName"/> is the music stream of the phone called
    /// <paramref name="phoneName"/>. Deliberately strict: a wrong match would change another stream's volume.
    /// </summary>
    public static bool IsMusicSessionOf(string? sessionDisplayName, string? phoneName)
    {
        if (string.IsNullOrWhiteSpace(sessionDisplayName) || string.IsNullOrWhiteSpace(phoneName))
        {
            return false;
        }

        return sessionDisplayName.Contains(phoneName.Trim(), StringComparison.OrdinalIgnoreCase)
            && sessionDisplayName.Contains(MusicMarker, StringComparison.OrdinalIgnoreCase);
    }
}
