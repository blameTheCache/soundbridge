using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SoundBridge.Platform;

/// <summary>
/// Keeps one copy running per user session. A second launch signals the first to show its window, then exits.
/// <para>
/// The names are predictable, so any program in the session could create them first. Two things limit what
/// that achieves: the objects are created for the current user only, so another account cannot signal or
/// open them, and a name that is taken while no other SoundBridge is running is not believed.
/// </para>
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SoundBridge.Instance";
    private const string ActivateEventName = @"Local\SoundBridge.Activate";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex? mutex, EventWaitHandle? activate, bool unguarded)
    {
        _mutex = mutex;
        _activate = activate;
        Unguarded = unguarded;
    }

    /// <summary>
    /// True when the names were already taken by something that is not SoundBridge. This copy runs anyway,
    /// without being able to stop a second one from starting.
    /// </summary>
    public bool Unguarded { get; }

    /// <summary>
    /// Returns null when another instance already owns the session. That instance is asked to show its
    /// window unless <paramref name="showExisting"/> is false (a launch the user did not make).
    /// <paramref name="scope"/> separates instances that must not see each other, such as a development
    /// run with its own data folder beside the installed app.
    /// </summary>
    public static SingleInstance? TryAcquire(bool showExisting, string scope = "")
    {
        Mutex? mutex = null;
        EventWaitHandle? activate = null;
        var createdNew = false;
        try
        {
            var user = WindowsIdentity.GetCurrent().User!;

            var mutexSecurity = new MutexSecurity();
            mutexSecurity.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            mutex = MutexAcl.Create(initiallyOwned: true, MutexName + scope, out createdNew, mutexSecurity);

            var eventSecurity = new EventWaitHandleSecurity();
            eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            activate = EventWaitHandleAcl.Create(initialState: false, EventResetMode.AutoReset, ActivateEventName + scope, out _, eventSecurity);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // The name exists but belongs to something this user may not open, or is not the kind of object
            // SoundBridge creates. Whether to believe it is decided below.
            createdNew = false;
        }

        if (createdNew && activate is not null)
        {
            return new SingleInstance(mutex, activate, unguarded: false);
        }

        if (AnotherCopyIsRunning())
        {
            if (showExisting)
            {
                activate?.Set();
            }

            activate?.Dispose();
            mutex?.Dispose();
            return null;
        }

        // Nothing called SoundBridge is running in this session, so whatever holds the name is not an earlier
        // copy. Refusing to start would let any program keep SoundBridge from ever running.
        return new SingleInstance(mutex, activate, unguarded: true);
    }

    /// <summary>Invokes <paramref name="callback"/> (on a thread-pool thread) each time another launch asks for the window.</summary>
    public void OnActivationRequested(Action callback)
    {
        // An unguarded copy does not own the event, so a signal on it proves nothing about a second launch.
        if (_activate is not null && !Unguarded)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate?.Dispose();
        _mutex?.Dispose();
    }

    /// <summary>
    /// Whether another process with this program's name is running in this session. A name is all that can be
    /// compared: the installed copy and the plain exe live in different folders and still count as one app.
    /// </summary>
    private static bool AnotherCopyIsRunning()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var others = Process.GetProcessesByName(self.ProcessName);
            try
            {
                return others.Any(other => other.Id != self.Id && other.SessionId == self.SessionId);
            }
            finally
            {
                foreach (var other in others)
                {
                    other.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // If the process list cannot be read, keep the old meaning of a taken name.
            return true;
        }
    }
}
