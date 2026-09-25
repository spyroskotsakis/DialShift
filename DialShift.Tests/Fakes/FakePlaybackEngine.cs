using DialShift.Core.Playback;

namespace DialShift.Tests.Fakes;

/// <summary>One recorded <see cref="IPlaybackEngine.StartAsync"/> invocation (recorded even if the call then fails; a null source is not recorded).</summary>
public sealed record EngineStartCall(long SessionId, StreamSource Source, double Volume);

/// <summary>
/// Scriptable <see cref="IPlaybackEngine"/> + <see cref="ITrackMetadataProvider"/> for coordinator tests.
/// <list type="bullet">
/// <item><b>Session ids</b> follow the contract exactly: a per-instance counter starting at 1, incremented synchronously on
/// entry to every <see cref="StartAsync"/> call — including calls that then throw, are cancelled, or hit a disposed engine.</item>
/// <item><b>Events are never raised on their own.</b> Tests call <see cref="RaiseState"/>/<see cref="RaiseFailed"/> for any
/// session id (current or stale, from any thread) to simulate callbacks, including stale ones racing past the contract.</item>
/// <item><b>Slow connect:</b> with <see cref="HoldStarts"/> set, each start's task stays incomplete until
/// <see cref="ReleaseStart"/>/<see cref="FailStart"/> (or its token is cancelled).</item>
/// <item><b>Throwing start/stop:</b> <see cref="StartException"/>/<see cref="StopException"/> fault every start/stop while
/// set (as a faulted task, or thrown synchronously when <see cref="ThrowSynchronously"/> is set).</item>
/// <item><b>Events inside StartAsync:</b> <see cref="OnStarted"/> runs synchronously at the end of every accepted start, on
/// the caller's stack, to simulate an adapter that raises events inside <see cref="StartAsync"/> (the contract allows it).</item>
/// <item><b>Call order:</b> <see cref="CallLog"/> records every start/stop/volume call in invocation order.</item>
/// </list>
/// All members are thread-safe.
/// </summary>
public sealed class FakePlaybackEngine : IPlaybackEngine, ITrackMetadataProvider
{
    private readonly object gate = new(); // plain monitor: WaitForStarts uses Monitor.Wait
    private readonly List<EngineStartCall> starts = [];
    private readonly List<double> volumeCalls = [];
    private readonly List<string> callLog = [];
    private readonly Dictionary<long, TaskCompletionSource> heldStarts = [];
    private long lastSessionId;
    private long? activeSessionId;
    private int stopCount;
    private int disposeCount;
    private double volume;
    private string? title;

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackEngineFailedEventArgs>? Failed;
    public event EventHandler? MetadataChanged;

    /// <summary>When true, new starts stay pending until released, failed, or cancelled.</summary>
    public bool HoldStarts { get; set; }

    /// <summary>When set, every start faults with this exception (after its session id was assigned).</summary>
    public Exception? StartException { get; set; }

    /// <summary>When set, every stop faults with this exception (the stop still takes effect first).</summary>
    public Exception? StopException { get; set; }

    /// <summary>When true, <see cref="StartException"/>/<see cref="StopException"/> are thrown synchronously instead of returned as faulted tasks.</summary>
    public bool ThrowSynchronously { get; set; }

    /// <summary>Runs synchronously (outside the fake's lock) at the end of every start that was accepted, with its session id.</summary>
    public Action<long>? OnStarted { get; set; }

    /// <summary>Id of the most recently requested session; 0 before the first start.</summary>
    public long LastSessionId { get { lock (gate) return lastSessionId; } }

    /// <summary>Session that is neither stopped, superseded, failed-to-start nor disposed; null when none.</summary>
    public long? ActiveSessionId { get { lock (gate) return activeSessionId; } }

    public IReadOnlyList<EngineStartCall> Starts { get { lock (gate) return [.. starts]; } }
    public IReadOnlyList<double> VolumeCalls { get { lock (gate) return [.. volumeCalls]; } }

    /// <summary>Every call in invocation order: <c>start:N</c>, <c>stop</c>, <c>volume:V</c> (V formatted invariantly).</summary>
    public IReadOnlyList<string> CallLog { get { lock (gate) return [.. callLog]; } }
    public int StopCount { get { lock (gate) return stopCount; } }
    public int DisposeCount { get { lock (gate) return disposeCount; } }
    public bool IsDisposed => DisposeCount > 0;

    /// <summary>Last volume applied by a start or <see cref="SetVolumeAsync"/>, clamped to 0.0–1.0.</summary>
    public double Volume { get { lock (gate) return volume; } }

    /// <summary>Ids of starts currently held incomplete.</summary>
    public IReadOnlyList<long> HeldStartIds { get { lock (gate) return [.. heldStarts.Keys.Order()]; } }

    public string? CurrentTitle { get { lock (gate) return title; } }

    public Task StartAsync(StreamSource source, double volume, CancellationToken ct)
    {
        TaskCompletionSource? held = null;
        long id;
        lock (gate)
        {
            id = ++lastSessionId; // contract: synchronous, on entry, before anything can fail
            if (source is null) return Task.FromException(new ArgumentNullException(nameof(source)));
            starts.Add(new EngineStartCall(id, source, volume));
            callLog.Add($"start:{id}");
            title = null;
            Monitor.PulseAll(gate);
            if (disposeCount > 0) return Task.FromException(new ObjectDisposedException(nameof(FakePlaybackEngine)));
            activeSessionId = id; // implicitly stops the previous session
            this.volume = Math.Clamp(volume, 0.0, 1.0);
            if (StartException is { } error)
            {
                activeSessionId = null;
                if (ThrowSynchronously) throw error;
                return Task.FromException(error);
            }
            if (ct.IsCancellationRequested) { activeSessionId = null; return Task.FromCanceled(ct); }
            if (HoldStarts)
            {
                held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                heldStarts[id] = held;
            }
        }
        OnStarted?.Invoke(id);
        if (held is null) return Task.CompletedTask;
        var pending = held;
        ct.Register(() =>
        {
            lock (gate)
            {
                if (!heldStarts.Remove(id)) return;
                if (activeSessionId == id) activeSessionId = null; // cancelled start: session stopped silently
            }
            pending.TrySetCanceled(ct);
        });
        return pending.Task;
    }

    /// <summary>Completes a held start successfully. Returns false if no such start is held.</summary>
    public bool ReleaseStart(long sessionId) => TakeHeld(sessionId)?.TrySetResult() ?? false;

    /// <summary>Faults a held start (e.g. to simulate an adapter bug). Returns false if no such start is held.</summary>
    public bool FailStart(long sessionId, Exception error)
    {
        var held = TakeHeld(sessionId);
        if (held == null) return false;
        lock (gate) if (activeSessionId == sessionId) activeSessionId = null;
        return held.TrySetException(error);
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (gate)
        {
            stopCount++;
            callLog.Add("stop");
            activeSessionId = null;
            title = null;
            if (StopException is { } error)
            {
                if (ThrowSynchronously) throw error;
                return Task.FromException(error);
            }
        }
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        lock (gate)
        {
            volumeCalls.Add(volume);
            callLog.Add(FormattableString.Invariant($"volume:{volume}"));
            this.volume = Math.Clamp(volume, 0.0, 1.0);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposeCount++;
            activeSessionId = null;
            title = null;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Raises <see cref="StateChanged"/> synchronously on the calling thread for any session id.</summary>
    public void RaiseState(long sessionId, PlaybackEngineState state) =>
        StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(sessionId, state));

    /// <summary>Raises <see cref="Failed"/> synchronously on the calling thread for any session id.</summary>
    public void RaiseFailed(long sessionId, PlaybackFailureKind kind, string? diagnostic = null) =>
        Failed?.Invoke(this, new PlaybackEngineFailedEventArgs(sessionId, kind, diagnostic));

    /// <summary>Sets <see cref="CurrentTitle"/> and raises <see cref="MetadataChanged"/>.</summary>
    public void SetTitle(string? value)
    {
        lock (gate) title = value;
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Blocks until at least <paramref name="count"/> starts were requested (from any thread) or the timeout passes.</summary>
    public bool WaitForStarts(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout; // real time on purpose: this bounds a test's wait, it is not app time
        lock (gate)
        {
            while (starts.Count < count)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                Monitor.Wait(gate, remaining);
            }
            return true;
        }
    }

    private TaskCompletionSource? TakeHeld(long sessionId)
    {
        lock (gate) return heldStarts.Remove(sessionId, out var held) ? held : null;
    }
}
