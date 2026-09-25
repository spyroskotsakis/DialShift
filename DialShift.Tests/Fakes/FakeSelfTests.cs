using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Fakes;

/// <summary>Self-tests for the test doubles, so coordinator tests can trust them.</summary>
public static class FakeSelfTests
{
    private static readonly StreamSource A = new(new Uri("https://a.example.org/live"), "A");
    private static readonly StreamSource B = new(new Uri("https://b.example.org/live"), "B");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        await EngineSessionIds();
        await EngineHeldStarts();
        await EngineEventsAndMetadata();
        await EngineHooksAndCallLog();
        await PlainEngineHidesMetadata();
        Clocks();
        Log();
    }

    private static async Task EngineSessionIds()
    {
        var engine = new FakePlaybackEngine { HoldStarts = true };
        Check("FakeEngine: LastSessionId is 0 before any start", engine.LastSessionId == 0);
        var first = engine.StartAsync(A, 0.6, CancellationToken.None);
        Check("FakeEngine: first session id is 1, assigned synchronously on entry", engine.LastSessionId == 1 && !first.IsCompleted);
        engine.HoldStarts = false;
        engine.StartException = new InvalidOperationException("boom");
        var thrown = await Faults(engine.StartAsync(B, 0.6, CancellationToken.None));
        Check("FakeEngine: a throwing start still consumes id 2", thrown is InvalidOperationException && engine.LastSessionId == 2 && engine.ActiveSessionId == null);
        engine.StartException = null;
        var cancelled = await Faults(engine.StartAsync(A, 0.6, new CancellationToken(canceled: true)));
        Check("FakeEngine: a pre-cancelled start consumes id 3 and throws OperationCanceledException", cancelled is OperationCanceledException && engine.LastSessionId == 3);
        await engine.StartAsync(B, 1.5, CancellationToken.None);
        Check("FakeEngine: completed start 4 is active with clamped volume and is recorded",
            engine.ActiveSessionId == 4 && engine.Volume == 1.0 && engine.Starts.Select(s => s.SessionId).SequenceEqual([1L, 2, 3, 4]) && engine.Starts[3] == new EngineStartCall(4, B, 1.5));
        await engine.StopAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);
        Check("FakeEngine: StopAsync is idempotent, counted, and clears the active session", engine.StopCount == 2 && engine.ActiveSessionId == null);
        await engine.DisposeAsync();
        var disposed = await Faults(engine.StartAsync(A, 0.5, CancellationToken.None));
        Check("FakeEngine: start after dispose throws ObjectDisposedException and still consumes id 5",
            disposed is ObjectDisposedException && engine.LastSessionId == 5 && engine.IsDisposed && engine.ActiveSessionId == null);
    }

    private static async Task EngineHeldStarts()
    {
        var engine = new FakePlaybackEngine { HoldStarts = true };
        var one = engine.StartAsync(A, 0.5, CancellationToken.None);
        var two = engine.StartAsync(B, 0.5, CancellationToken.None);
        Check("FakeEngine: held starts stay pending; the newer one is active", !one.IsCompleted && !two.IsCompleted && engine.HeldStartIds.SequenceEqual([1L, 2]) && engine.ActiveSessionId == 2);
        Check("FakeEngine: ReleaseStart completes only that start", engine.ReleaseStart(2) && await Completes(two) && !one.IsCompleted && !engine.ReleaseStart(2));
        Check("FakeEngine: FailStart faults the held start", engine.FailStart(1, new IOException("x")) && await Faults(one) is IOException);
        using var cts = new CancellationTokenSource();
        var three = engine.StartAsync(A, 0.5, cts.Token);
        await cts.CancelAsync();
        Check("FakeEngine: cancelling a held start cancels it and stops its session",
            await Faults(three) is OperationCanceledException && engine.ActiveSessionId == null && engine.HeldStartIds.Count == 0);
        var released = Task.Run(() => engine.WaitForStarts(4, Timeout));
        var four = engine.StartAsync(B, 0.5, CancellationToken.None);
        Check("FakeEngine: WaitForStarts observes a start from another thread", await released && engine.ReleaseStart(4) && await Completes(four));
        Check("FakeEngine: WaitForStarts times out when no start arrives", !engine.WaitForStarts(5, TimeSpan.FromMilliseconds(20)));
    }

    private static async Task EngineEventsAndMetadata()
    {
        var engine = new FakePlaybackEngine();
        var states = new List<(long, PlaybackEngineState, int)>();
        var failures = new List<PlaybackEngineFailedEventArgs>();
        engine.StateChanged += (_, e) => { lock (states) states.Add((e.SessionId, e.State, Environment.CurrentManagedThreadId)); };
        engine.Failed += (_, e) => { lock (failures) failures.Add(e); };
        await engine.StartAsync(A, 0.5, CancellationToken.None);
        await engine.StartAsync(B, 0.5, CancellationToken.None);
        Check("FakeEngine: no events are raised on its own", states.Count == 0 && failures.Count == 0);
        var testThread = Environment.CurrentManagedThreadId;
        var other = new Thread(() => engine.RaiseState(1, PlaybackEngineState.Playing)); // stale session, dedicated thread
        other.Start();
        other.Join();
        engine.RaiseFailed(2, PlaybackFailureKind.EndOfStream, "radio.example.org");
        Check("FakeEngine: RaiseState delivers any (even stale) session id, from another thread",
            states.Count == 1 && states[0].Item1 == 1 && states[0].Item2 == PlaybackEngineState.Playing && states[0].Item3 != testThread);
        Check("FakeEngine: RaiseFailed delivers session id, kind and diagnostic", failures.SequenceEqual([new PlaybackEngineFailedEventArgs(2, PlaybackFailureKind.EndOfStream, "radio.example.org")]));

        var metadataEvents = 0;
        engine.MetadataChanged += (_, _) => Interlocked.Increment(ref metadataEvents);
        engine.SetTitle("Artist – Song");
        Check("FakeEngine: SetTitle updates CurrentTitle and raises MetadataChanged", engine.CurrentTitle == "Artist – Song" && metadataEvents == 1);
        await engine.StartAsync(A, 0.5, CancellationToken.None);
        Check("FakeEngine: a start resets CurrentTitle to null (contract)", engine.CurrentTitle == null);
        engine.SetTitle("x");
        await engine.StopAsync(CancellationToken.None);
        Check("FakeEngine: a stop resets CurrentTitle to null (contract)", engine.CurrentTitle == null);
        await engine.SetVolumeAsync(-0.5, CancellationToken.None);
        Check("FakeEngine: SetVolumeAsync is recorded raw and applied clamped", engine.VolumeCalls.SequenceEqual([-0.5]) && engine.Volume == 0.0);
    }

    private static async Task EngineHooksAndCallLog()
    {
        var engine = new FakePlaybackEngine();
        var seen = new List<long>();
        engine.OnStarted = id => seen.Add(id);
        await engine.StartAsync(A, 0.5, CancellationToken.None);
        await engine.SetVolumeAsync(0.25, CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);
        Check("FakeEngine: OnStarted runs synchronously inside StartAsync with the session id", seen.SequenceEqual([1L]));
        Check("FakeEngine: CallLog records start/volume/stop in invocation order", engine.CallLog.SequenceEqual(["start:1", "volume:0.25", "stop"]));
        engine.StopException = new IOException("stop failed");
        Check("FakeEngine: StopException faults StopAsync after the stop took effect",
            await Faults(engine.StopAsync(CancellationToken.None)) is IOException && engine.StopCount == 2);
        engine.ThrowSynchronously = true;
        Check("FakeEngine: ThrowSynchronously throws the stop error on the caller's stack", Throws<IOException>(() => engine.StopAsync(CancellationToken.None)));
        engine.StartException = new InvalidOperationException("start failed");
        Check("FakeEngine: ThrowSynchronously throws the start error and still consumes the id",
            Throws<InvalidOperationException>(() => engine.StartAsync(B, 0.5, CancellationToken.None)) && engine.LastSessionId == 2 && seen.Count == 1);
    }

    private static async Task PlainEngineHidesMetadata()
    {
        var inner = new FakePlaybackEngine();
        await using var plain = new PlainPlaybackEngine(inner);
        var states = new List<PlaybackEngineStateChangedEventArgs>();
        var failures = new List<PlaybackEngineFailedEventArgs>();
        plain.StateChanged += (_, e) => states.Add(e);
        plain.Failed += (_, e) => failures.Add(e);
        await plain.StartAsync(A, 0.4, CancellationToken.None);
        inner.RaiseState(1, PlaybackEngineState.Playing);
        inner.RaiseFailed(1, PlaybackFailureKind.HttpError);
        IPlaybackEngine port = plain; // what the coordinator sees
        Check("PlainEngine: is not an ITrackMetadataProvider", port is not ITrackMetadataProvider);
        Check("PlainEngine: forwards starts and re-raises events", inner.Starts.Count == 1 && states.Count == 1 && failures.Count == 1);
    }

    private static void Clocks()
    {
        var clock = FakeClock.AtUtc(2026, 9, 14, 10);
        clock.Advance(TimeSpan.FromMinutes(90));
        Check("FakeClock: Advance moves UtcNow", clock.UtcNow == new DateTimeOffset(2026, 9, 14, 11, 30, 0, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromHours(-2));
        Check("FakeClock: wall clock may jump backwards", clock.UtcNow.Hour == 9 && clock.UtcNow.Minute == 30);
        Check("FakeClock: non-zero offsets are rejected", Throws<ArgumentException>(() => clock.UtcNow = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.FromHours(3))));

        var mono = new FakeMonotonicClock();
        var t0 = mono.GetTimestamp();
        mono.Advance(TimeSpan.FromSeconds(1));
        var t1 = mono.GetTimestamp();
        Check("FakeMonotonicClock: starts non-zero; 1 s advance = Frequency units = 1 s elapsed",
            t0 == FakeMonotonicClock.DefaultStart && t1 - t0 == FakeMonotonicClock.Frequency && mono.GetElapsedTime(t0, t1) == TimeSpan.FromSeconds(1));
        mono.Advance(TimeSpan.FromMilliseconds(20_500));
        Check("FakeMonotonicClock: elapsed is consistent across advances", mono.GetElapsedTime(t0, mono.GetTimestamp()) == TimeSpan.FromMilliseconds(21_500));
        Check("FakeMonotonicClock: moving backwards is rejected", Throws<ArgumentOutOfRangeException>(() => mono.Advance(TimeSpan.FromTicks(-1))));
    }

    private static void Log()
    {
        var log = new RecordingAppLog();
        log.Info("playback.state", "Connecting to Groove Salad");
        log.Warn("playback.failed", "Stream failed", new IOException("GET https://user:hunter2@radio.example.org/private/live.mp3?token=abc"));
        Check("RecordingAppLog: captures level, event and message", log.Entries.Count == 2 && log.Entries[0] == new AppLogEntry(AppLogLevel.Info, "playback.state", "Connecting to Groove Salad", null) && log.HasEvent("playback.failed"));
        Check("RecordingAppLog: NoEntryContains finds secrets inside exception text", !log.NoEntryContains("hunter2") && !log.NoEntryContains("/PRIVATE/live.mp3"));
        Check("RecordingAppLog: NoEntryContains passes for absent fragments", log.NoEntryContains("password", "/other/path"));
    }

    private static async Task<Exception?> Faults(Task task)
    {
        try { await task.WaitAsync(Timeout); return null; }
        catch (TimeoutException) { return null; }
        catch (Exception ex) { return ex; }
    }

    private static async Task<bool> Completes(Task task)
    {
        try { await task.WaitAsync(Timeout); return true; }
        catch { return false; }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
