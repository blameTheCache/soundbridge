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

        // The name has to fill the whole space between the opening bracket and "A2DP". Merely containing it
        // would let "Pixel" claim the session of "Pixel 8" or of "Alice's Pixel".
        var name = phoneName.Trim();
        for (var at = sessionDisplayName.IndexOf(name, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = sessionDisplayName.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = sessionDisplayName.AsSpan(0, at).TrimEnd();
            var after = sessionDisplayName.AsSpan(at + name.Length);
            var opensHere = before.IsEmpty || before[^1] is '(' or '（';
            if (opensHere && after.Length > 0 && char.IsWhiteSpace(after[0]) && after.TrimStart().StartsWith(MusicMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
