using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;
using Windows.Media.Audio;

namespace SoundBridge.Bluetooth;

/// <summary>
/// One <see cref="AudioPlaybackConnection"/> (Windows' Bluetooth A2DP sink) for one device.
/// The enable-then-open sequence follows AudioPlaybackConnector by Richard Yu (MIT) and Microsoft's
/// "Enable audio playback from remote Bluetooth-connected devices" guide.
/// </summary>
internal sealed class BluetoothAudioLink(AudioSource device, ILog log) : IAudioLink
{
    private const string Category = "Bluetooth";

    private readonly Lock _gate = new();
    private AudioPlaybackConnection? _connection;
    private bool _disposed;
    private bool _closedRaised;

    public event EventHandler? Closed;

    // Run on the thread pool so the connection never depends on the UI thread's message pump. The class is
    // declared agile with ThreadingModel.Both in Windows metadata, so any thread may create and close it.
    public Task<LinkOpenResult> OpenAsync(CancellationToken cancellationToken) =>
        Task.Run(() => OpenCoreAsync(cancellationToken), cancellationToken);

    public void Dispose()
    {
        AudioPlaybackConnection? connection;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            Release(connection);
        }
    }

    private async Task<LinkOpenResult> OpenCoreAsync(CancellationToken cancellationToken)
    {
        AudioPlaybackConnection? connection;
        try
        {
            connection = AudioPlaybackConnection.TryCreateFromId(device.Id);
        }
        catch (Exception ex)
        {
            return new LinkOpenResult(LinkOpenStatus.DeviceUnavailable, Describe(ex));
        }

        if (connection is null)
        {
            return new LinkOpenResult(LinkOpenStatus.DeviceUnavailable, "Windows could not create an audio playback connection for this device.");
        }

        bool adopted;
        lock (_gate)
        {
            adopted = !_disposed;
            if (adopted)
            {
                _connection = connection;
                connection.StateChanged += OnStateChanged;
            }
        }

        if (!adopted)
        {
            // Disposed before the connection existed; nothing else holds it.
            connection.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }

        try
        {
            // Start tells Windows this app wants audio from the device; Open establishes the stream.
            await connection.StartAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var result = await connection.OpenAsync().AsTask(cancellationToken).ConfigureAwait(false);

            return result.Status switch
            {
                AudioPlaybackConnectionOpenResultStatus.Success => new LinkOpenResult(LinkOpenStatus.Opened),
                AudioPlaybackConnectionOpenResultStatus.RequestTimedOut => new LinkOpenResult(LinkOpenStatus.TimedOut),
                AudioPlaybackConnectionOpenResultStatus.DeniedBySystem => new LinkOpenResult(LinkOpenStatus.DeniedBySystem),
                _ => new LinkOpenResult(LinkOpenStatus.Failed, result.ExtendedError is { } error ? Describe(error) : null),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LinkOpenResult(LinkOpenStatus.Failed, Describe(ex));
        }
    }

    private void OnStateChanged(AudioPlaybackConnection sender, object args)
    {
        AudioPlaybackConnectionState state;
        try
        {
            state = sender.State;
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Could not read the link state for '{device.Name}': {Describe(ex)}");
            return;
        }

        log.Info(Category, $"Windows reports the link to '{device.Name}' as {state}.");
        if (state != AudioPlaybackConnectionState.Closed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _closedRaised)
            {
                return;
            }

            _closedRaised = true;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Release(AudioPlaybackConnection connection)
    {
        try
        {
            connection.StateChanged -= OnStateChanged;
            connection.Dispose();
            log.Info(Category, $"Released the link to '{device.Name}'.");
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"Releasing the link to '{device.Name}' failed: {Describe(ex)}");
        }
    }

    private static string Describe(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return $"{(message.Length == 0 ? ex.GetType().Name : message)} (0x{ex.HResult:X8})";
    }
}

internal sealed class BluetoothAudioLinkFactory(ILog log) : IAudioLinkFactory
{
    public static bool IsSupported =>
        Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Audio.AudioPlaybackConnection");

    public IAudioLink Create(AudioSource device) => new BluetoothAudioLink(device, log);
}
