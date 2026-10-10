using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SoundBridge.Bluetooth;

/// <summary>
/// Tracks the paired Bluetooth devices Windows can receive audio from. A <see cref="DeviceWatcher"/> keeps the
/// list live; <see cref="RefreshAsync"/> re-reads it on demand.
/// </summary>
internal sealed class AudioSourceWatcher(ILog log) : IDisposable
{
    private const string Category = "Discovery";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AudioSource> _sources = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;

    /// <summary>Raised on a background thread whenever the list changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AudioSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return [.. _sources.Values.OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            var watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            watcher.Stopped += OnStopped;
            watcher.Start();
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            log.Error(Category, $"Could not start watching for audio sources: {ex.Message} (0x{ex.HResult:X8})");
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector()).AsTask().ConfigureAwait(false);
            lock (_gate)
            {
                _sources.Clear();
                foreach (var device in found)
                {
                    _sources[device.Id] = new AudioSource(device.Id, DisplayName(device.Name));
                }
            }

            log.Info(Category, $"Refresh found {found.Count} audio source(s).");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            log.Error(Category, $"Refreshing audio sources failed: {ex.Message} (0x{ex.HResult:X8})");
        }
    }

    public void Dispose()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            watcher.Stopped -= OnStopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Stopping the device watcher failed: {ex.Message}");
        }
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation device)
    {
        lock (_gate)
        {
            _sources[device.Id] = new AudioSource(device.Id, DisplayName(device.Name));
        }

        log.Info(Category, $"Audio source available: '{device.Name}'.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (!update.Properties.TryGetValue("System.ItemNameDisplay", out var value) || value is not string name)
        {
            return;
        }

        lock (_gate)
        {
            if (!_sources.TryGetValue(update.Id, out var existing) || existing.Name == name)
            {
                return;
            }

            _sources[update.Id] = existing with { Name = DisplayName(name) };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        AudioSource? removed;
        lock (_gate)
        {
            _sources.Remove(update.Id, out removed);
        }

        if (removed is not null)
        {
            log.Info(Category, $"Audio source no longer available: '{removed.Name}'.");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnStopped(DeviceWatcher sender, object args) =>
        log.Warn(Category, $"The device watcher stopped ({sender.Status}).");

    private static string DisplayName(string? name) => string.IsNullOrWhiteSpace(name) ? "Unnamed device" : name.Trim();
}
