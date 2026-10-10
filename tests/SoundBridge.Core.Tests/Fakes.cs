using System.Collections.Concurrent;
using SoundBridge.Core.Connection;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Core.Tests;

/// <summary>A link whose open result the test decides, and when.</summary>
internal sealed class FakeLink(AudioSource device) : IAudioLink
{
    private readonly TaskCompletionSource<LinkOpenResult> _open = new();

    public event EventHandler? Closed;

    public AudioSource Device { get; } = device;

    public int OpenCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public bool Disposed => DisposeCalls > 0;

    public bool CancellationRequested { get; private set; }

    public int ClosedSubscribers => Closed?.GetInvocationList().Length ?? 0;

    public Task<LinkOpenResult> OpenAsync(CancellationToken cancellationToken)
    {
        OpenCalls++;
        cancellationToken.Register(() => CancellationRequested = true);
        return _open.Task;
    }

    public void CompleteOpen(LinkOpenStatus status, string? detail = null) => _open.TrySetResult(new LinkOpenResult(status, detail));

    public void ThrowFromOpen(Exception exception) => _open.TrySetException(exception);

    public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => DisposeCalls++;
}

internal sealed class FakeLinkFactory : IAudioLinkFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeLink> _links = [];

    public IReadOnlyList<FakeLink> Links
    {
        get
        {
            lock (_gate)
            {
                return [.. _links];
            }
        }
    }

    public FakeLink Latest => Links[^1];

    public FakeLink? LatestOrDefault => Links.LastOrDefault();

    public IAudioLink Create(AudioSource device)
    {
        var link = new FakeLink(device);
        lock (_gate)
        {
            _links.Add(link);
        }

        return link;
    }
}

internal sealed class RecordingLog : ILog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public void Write(LogLevel level, string category, string message) => _lines.Enqueue($"{level} [{category}] {message}");

    public bool Contains(string fragment) => _lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));
}
