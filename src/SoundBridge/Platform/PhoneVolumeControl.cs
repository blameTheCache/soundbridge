using System.Runtime.InteropServices;
using SoundBridge.Core.Audio;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Platform;

/// <summary>
/// Reads and sets the volume of the phone's music on this PC through its Windows audio session, the same
/// control the Volume mixer offers. It changes neither the phone's own volume nor any other sound on the PC,
/// and SoundBridge still never touches the audio itself.
/// <para>
/// Nothing is cached: every call looks the session up afresh, because Windows may create a new one when
/// the link is opened. Call from a background thread.
/// </para>
/// </summary>
internal sealed class PhoneVolumeControl(ILog log)
{
    private const string Category = "Volume";

    /// <summary>
    /// The current volume, or null when Windows has no music stream for this phone right now. If Windows
    /// lists more than one (the previous link's stream can linger briefly), the loudest is reported: that is
    /// the one that matters to the listener, and the one a remembered lower level has not reached yet.
    /// </summary>
    public PhoneVolume? Read(string phoneName)
    {
        PhoneVolume? found = null;
        Visit(phoneName, volume =>
        {
            Marshal.ThrowExceptionForHR(volume.GetMasterVolume(out var level));
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            var candidate = new PhoneVolume(level, muted);
            if (found is null || Loudness(candidate) > Loudness(found))
            {
                found = candidate;
            }
        });
        return found;

        static float Loudness(PhoneVolume volume) => volume.Muted ? 0f : volume.Level;
    }

    public bool SetLevel(string phoneName, float level)
    {
        var context = Guid.Empty;
        return Visit(phoneName, volume => Marshal.ThrowExceptionForHR(volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref context))) > 0;
    }

    public bool SetMuted(string phoneName, bool muted)
    {
        var context = Guid.Empty;
        return Visit(phoneName, volume => Marshal.ThrowExceptionForHR(volume.SetMute(muted, ref context))) > 0;
    }

    /// <summary>Applies <paramref name="action"/> to each of the phone's music sessions. Returns how many there were.</summary>
    private int Visit(string phoneName, Action<ISimpleAudioVolume> action)
    {
        var visited = 0;
        try
        {
            foreach (var session in CoreAudio.FindPhoneMusicSessions(phoneName))
            {
                action((ISimpleAudioVolume)session);
                visited++;
            }
        }
        catch (Exception ex) when (CoreAudio.IsExpected(ex))
        {
            log.Warn(Category, $"Could not reach the phone's audio session: {ex.Message} (0x{ex.HResult:X8})");
        }

        return visited;
    }
}
