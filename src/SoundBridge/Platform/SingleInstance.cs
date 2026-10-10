namespace SoundBridge.Platform;

/// <summary>
/// Keeps one copy running per user session. A second launch signals the first to show its window, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SoundBridge.Instance";
    private const string ActivateEventName = @"Local\SoundBridge.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>
    /// Returns null when another instance already owns the session. That instance is asked to show its
    /// window unless <paramref name="showExisting"/> is false (a launch the user did not make).
    /// </summary>
    public static SingleInstance? TryAcquire(bool showExisting)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);
        if (createdNew)
        {
            return new SingleInstance(mutex, activate);
        }

        if (showExisting)
        {
            activate.Set();
        }

        activate.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Invokes <paramref name="callback"/> (on a thread-pool thread) each time another launch asks for the window.</summary>
    public void OnActivationRequested(Action callback) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        _mutex.Dispose();
    }
}
