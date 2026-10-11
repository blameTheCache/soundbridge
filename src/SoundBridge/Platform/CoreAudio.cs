using System.Runtime.InteropServices;
using SoundBridge.Core.Audio;

namespace SoundBridge.Platform;

/// <summary>
/// The small part of the Windows Core Audio API that SoundBridge uses: finding the audio session Windows
/// plays the phone's music through. The session's volume and its level meter are both reached from there.
/// </summary>
internal static class CoreAudio
{
    private const int Render = 0;
    private const int ActiveDevices = 1;
    private const int AllContexts = 23;
    private const int ExpiredSession = 2;

    /// <summary>
    /// The phone's music sessions on every active output device. Usually one; for a moment after a reconnect
    /// the previous link's session can still be listed beside the new one. Throws on COM failure.
    /// </summary>
    public static List<object> FindPhoneMusicSessions(string phoneName)
    {
        var found = new List<object>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        Check(enumerator.EnumAudioEndpoints(Render, ActiveDevices, out var devices));
        Check(devices.GetCount(out var deviceCount));

        for (var d = 0; d < deviceCount; d++)
        {
            Check(devices.Item(d, out var device));
            var iid = typeof(IAudioSessionManager2).GUID;
            Check(device.Activate(ref iid, AllContexts, 0, out var activated));
            Check(((IAudioSessionManager2)activated).GetSessionEnumerator(out var sessions));
            Check(sessions.GetCount(out var sessionCount));

            for (var s = 0; s < sessionCount; s++)
            {
                Check(sessions.GetSession(s, out var session));
                var control = (IAudioSessionControl2)session;
                if (control.GetState(out var state) == 0 && state != ExpiredSession &&
                    control.GetDisplayName(out var displayName) == 0 &&
                    PhoneAudioSessions.IsMusicSessionOf(displayName, phoneName))
                {
                    found.Add(session);
                }
            }
        }

        return found;
    }

    public static bool IsExpected(Exception exception) =>
        exception is COMException or InvalidCastException or UnauthorizedAccessException;

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
}

// Each interface lists only the methods that are called, plus any that come before them: the order must
// match the COM interface exactly, and nothing after the last one used is needed.

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom;

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
}

[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int Item(int index, out IMMDevice device);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int context, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(nint sessionId, int flags, out nint control);

    [PreserveSig]
    int GetSimpleAudioVolume(nint sessionId, int flags, out nint volume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
}

[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid context);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    /// <summary>Peak sample value over the last device period, 0 to 1, taken before the session's volume is applied.</summary>
    [PreserveSig]
    int GetPeakValue(out float peak);
}
