using Microsoft.Win32;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Platform;

/// <summary>Relays Windows sleep and resume notifications.</summary>
internal sealed class PowerMonitor : IDisposable
{
    private const string Category = "Power";

    private readonly ILog _log;

    public PowerMonitor(ILog log)
    {
        _log = log;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _log.Info(Category, "Windows is suspending.");
                Suspending?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.Resume:
                _log.Info(Category, "Windows resumed.");
                Resumed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
