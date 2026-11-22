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

    /// <summary>Set once closing has begun, which with <see cref="HoldDispose"/> is before it ends.</summary>
    public ManualResetEventSlim DisposeStarted { get; } = new();

    /// <summary>When set, closing waits for it, like a link that Windows is slow to let go of.</summary>
    public ManualResetEventSlim? HoldDispose { get; set; }

    public void Dispose()
    {
        DisposeCalls++;
        DisposeStarted.Set();
        HoldDispose?.Wait(TimeSpan.FromSeconds(5));
    }
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

/// <summary>A reachability check whose answer the test decides, and when.</summary>
internal sealed class FakeReachability : IPhoneReachability
{
    private readonly Lock _gate = new();
    private readonly List<Probe> _probes = [];

    public IReadOnlyList<Probe> Probes
    {
        get
        {
            lock (_gate)
            {
                return [.. _probes];
            }
        }
    }

    public Probe Latest => Probes[^1];

    /// <summary>When set, the check throws before returning a task, like a radio API that fails at once.</summary>
    public Exception? ThrowOnProbe { get; set; }

    public Task<PhoneReachability> ProbeAsync(AudioSource device, CancellationToken cancellationToken)
    {
        if (ThrowOnProbe is { } exception)
        {
            throw exception;
        }

        var probe = new Probe(device, cancellationToken);
        lock (_gate)
        {
            _probes.Add(probe);
        }

        return probe.Task;
    }

    internal sealed class Probe
    {
        private readonly TaskCompletionSource<PhoneReachability> _answer = new();
        private readonly CancellationToken _token;

        public Probe(AudioSource device, CancellationToken token)
        {
            Device = device;
            _token = token;
        }

        public AudioSource Device { get; }

        public bool CancellationRequested => _token.IsCancellationRequested;

        public Task<PhoneReachability> Task => _answer.Task;

        public void Answer(PhoneReachability reachability) => _answer.TrySetResult(reachability);

        public void Fail(Exception exception) => _answer.TrySetException(exception);
    }
}

internal sealed class RecordingLog : ILog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public void Write(LogLevel level, string category, string message) => _lines.Enqueue($"{level} [{category}] {message}");

    public bool Contains(string fragment) => _lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));
}
