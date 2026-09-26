using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using DialShift.App.Services;
using DialShift.Core.Playback;
using DialShift.Tests.Core;
using DialShift.Tests.Fakes;
using DialShift.Tests.TestServers;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

public static partial class LibVlcEngineTests
{
    /// <summary>
    /// LV-12 (D100): the TLS trust warm-up inside the real LibVLC engine, with a <see cref="FakeTrustWarmup"/>. An https
    /// source is warmed before LibVLC opens it, and an http source never is; a warm-up that blocks its caller never blocks
    /// StartAsync's, and is still cut off at TrustWarmupLimit or by cancellation; a warm-up that throws, faults or never
    /// finishes does not stop the start; cancellation, a newer start and disposal during the warm-up keep the engine
    /// contract; a playlist's https entry is warmed before it is played, and if the session is stopped, superseded or
    /// disposed meanwhile, the entry warm-up's token is cancelled and the entry is never played (a listening
    /// <see cref="LocalTlsServer"/> as the entry proves LibVLC never connects to it). The https source points at a closed loopback port: LibVLC's MediaPlayer.Opening (raised as it starts the
    /// media) proves the player was created, and the connection then fails at once. The real warm-up's own checks are the
    /// TrustWarmup suite.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task TrustWarmupChecksAsync(Rig rig)
    {
        var warmup = new FakeTrustWarmup();
        var events = new ConcurrentQueue<(long Session, string What)>();
        await using var engine = new LibVlcPlaybackEngine(rig.Log, LibVlcEngineOptions.Dummy, warmup);
        engine.StateChanged += (_, e) => events.Enqueue((e.SessionId, e.State.ToString()));
        engine.Failed += (_, e) => events.Enqueue((e.SessionId, $"Failed({e.Kind})"));
        bool Saw(long session, string what) => events.Any(e => e.Session == session && e.What == what);
        bool Raised(long session) => events.Any(e => e.Session == session);
        string Events() => $"[{string.Join(", ", events.Select(e => $"{e.Session}:{e.What}"))}]";
        int Warnings(int mark) => rig.Log.Entries.Skip(mark).Count(e => e.EventName == WindowsTrustWarmup.LogEvent && e.Level == AppLogLevel.Warn);
        var https = new StreamSource(new Uri($"https://{LocalMediaServer.AuthUser}:{LocalMediaServer.AuthPassword}@127.0.0.1:{LocalMediaServer.ClosedPort()}/live.wav?token=tok3n-secret-91c2"), "https");
        var http = new StreamSource(rig.Server.Url("/live.wav"), "http");
        long session = 0;

        // https: the player is created only after the warm-up finished.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        warmup.Behavior = (_, _) => gate.Task;
        var first = ++session;
        var start = engine.StartAsync(https, 0.5, CancellationToken.None);
        var asked = await Wait.Until(() => warmup.Calls.Count == 1, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // LibVLC is initialized: without the gate it would have opened by now
        Check($"HS-17 LV-12 https: the warm-up is asked for the source URL, and while it runs no player opens and StartAsync is pending (events: {Events()})",
            asked && warmup.Calls[0] == https.Url && !Raised(first) && !start.IsCompleted);
        gate.SetResult();
        var opened = await Wait.Until(() => Saw(first, nameof(PlaybackEngineState.Opening)), TimeSpan.FromSeconds(10));
        Check($"HS-17 LV-12 ... once it finishes, LibVLC opens the source and StartAsync completes (events: {Events()}; start {start.Status})",
            opened && await Wait.Finishes(start, TimeSpan.FromSeconds(5)) && start.IsCompletedSuccessfully);
        await engine.StopAsync(CancellationToken.None);

        // http: never warmed.
        var calls = warmup.Calls.Count;
        var plain = ++session;
        await engine.StartAsync(http, 0.5, CancellationToken.None);
        var playing = await Wait.Until(() => Saw(plain, nameof(PlaybackEngineState.Playing)), TimeSpan.FromSeconds(15));
        Check($"HS-17 LV-12 http: the source is never warmed, and it plays (events: {Events()})", playing && warmup.Calls.Count == calls);
        await engine.StopAsync(CancellationToken.None);

        // A broken warm-up never stops the start.
        (string Name, Func<Uri, CancellationToken, Task> Behavior)[] broken =
        [
            ("throws", static (_, _) => throw new InvalidOperationException("warm-up defect")),
            ("faults", static (_, _) => Task.FromException(new HttpRequestException("warm-up defect")))
        ];
        foreach (var (name, behavior) in broken)
        {
            warmup.Behavior = behavior;
            var mark = rig.Log.Entries.Count;
            var id = ++session;
            await engine.StartAsync(https, 0.5, CancellationToken.None);
            var goesOn = await Wait.Until(() => Saw(id, nameof(PlaybackEngineState.Opening)), TimeSpan.FromSeconds(10));
            Check($"HS-17 LV-12 a warm-up that {name}: LibVLC still opens the source, and one {WindowsTrustWarmup.LogEvent} warning is logged ({Warnings(mark)}; events: {Events()})",
                goesOn && Warnings(mark) == 1);
            await engine.StopAsync(CancellationToken.None);
        }

        // A warm-up that blocks its caller before returning its task (as a slow system proxy lookup would), for longer
        // than TrustWarmupLimit: StartAsync's caller is never blocked, and the limit still holds.
        var limit = LibVlcPlaybackEngine.TrustWarmupLimit;
        var blockFor = limit + TimeSpan.FromSeconds(3);
        warmup.Behavior = (_, _) => { Thread.Sleep(blockFor); return Task.CompletedTask; };
        var blockMark = rig.Log.Entries.Count;
        var blocking = ++session;
        var call = Stopwatch.StartNew();
        var blockingStart = engine.StartAsync(https, 0.5, CancellationToken.None);
        var returnedAfter = call.Elapsed;
        var blockedOpened = await Wait.Until(() => Saw(blocking, nameof(PlaybackEngineState.Opening)), limit + TimeSpan.FromSeconds(10));
        var openedAfter = call.Elapsed;
        Check($"HS-17 LV-12 a warm-up that blocks its caller for {blockFor.TotalSeconds:0} s: StartAsync returns its task after {returnedAfter.TotalMilliseconds:0} ms, "
            + $"and LibVLC opens at the {limit.TotalSeconds:0} s limit (after {openedAfter.TotalSeconds:0.0} s), with a {WindowsTrustWarmup.LogEvent} warning (events: {Events()})",
            returnedAfter < TimeSpan.FromMilliseconds(500) && blockedOpened && openedAfter >= limit - TimeSpan.FromMilliseconds(100) && openedAfter < blockFor
            && Warnings(blockMark) == 1 && await Wait.Finishes(blockingStart, TimeSpan.FromSeconds(5)));
        await engine.StopAsync(CancellationToken.None);

        // Cancelled while the warm-up blocks: StartAsync throws at once, without a player.
        var blockedCancelId = ++session;
        using (var cts = new CancellationTokenSource())
        {
            var blockedCancel = engine.StartAsync(https, 0.5, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(1));
            var cancelWatch = Stopwatch.StartNew();
            await cts.CancelAsync();
            var threw = await Wait.Finishes(blockedCancel.ContinueWith(static _ => { }, TaskScheduler.Default), TimeSpan.FromSeconds(5)) && blockedCancel.IsCanceled;
            var cancelledAfter = cancelWatch.Elapsed;
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Check($"HS-17 LV-12 cancelled while the warm-up blocks its caller: StartAsync throws OperationCanceledException after {cancelledAfter.TotalMilliseconds:0} ms, "
                + $"and no player opens ({blockedCancel.Status}; events: {Events()})",
                threw && cancelledAfter < TimeSpan.FromSeconds(1) && !Raised(blockedCancelId));
        }

        // A warm-up that never finishes (and ignores its token) holds the start for TrustWarmupLimit at most.
        warmup.Behavior = static (_, _) => new TaskCompletionSource().Task;
        var hungMark = rig.Log.Entries.Count;
        var hung = ++session;
        var watch = Stopwatch.StartNew();
        await engine.StartAsync(https, 0.5, CancellationToken.None);
        var elapsed = watch.Elapsed;
        var afterLimit = await Wait.Until(() => Saw(hung, nameof(PlaybackEngineState.Opening)), TimeSpan.FromSeconds(10));
        Check($"HS-17 LV-12 a warm-up that never finishes: the start goes on after {elapsed.TotalSeconds:0.0} s (limit {limit.TotalSeconds:0} s), LibVLC opens the source, "
            + $"and the timeout is logged as a warning (events: {Events()})",
            afterLimit && elapsed >= limit - TimeSpan.FromMilliseconds(100) && elapsed < limit + TimeSpan.FromSeconds(5)
            && rig.Log.Entries.Skip(hungMark).Any(e => e.EventName == WindowsTrustWarmup.LogEvent && e.Message.Contains(nameof(TimeoutException), StringComparison.Ordinal)));
        await engine.StopAsync(CancellationToken.None);

        // Cancelled during the warm-up: like a cancellation during the init wait.
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        warmup.Behavior = (_, _) => cancelGate.Task;
        calls = warmup.Calls.Count;
        var cancelledId = ++session;
        using (var cts = new CancellationTokenSource())
        {
            var cancelled = engine.StartAsync(https, 0.5, cts.Token);
            var warming = await Wait.Until(() => warmup.Calls.Count == calls + 1, TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            var ended = await Wait.Finishes(cancelled.ContinueWith(static _ => { }, TaskScheduler.Default), TimeSpan.FromSeconds(5));
            cancelGate.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Check($"HS-17 LV-12 cancelled during the warm-up: StartAsync throws OperationCanceledException at once, and no player opens for the session ({cancelled.Status}; events: {Events()})",
                warming && ended && cancelled.IsCanceled && !Raised(cancelledId));
        }

        // Superseded during the warm-up: the newer start plays, the older one never creates a player.
        var supersededGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        warmup.Behavior = (_, _) => supersededGate.Task;
        calls = warmup.Calls.Count;
        var older = ++session;
        var olderStart = engine.StartAsync(https, 0.5, CancellationToken.None);
        var olderWarming = await Wait.Until(() => warmup.Calls.Count == calls + 1, TimeSpan.FromSeconds(10));
        var newer = ++session;
        await engine.StartAsync(http, 0.5, CancellationToken.None);
        var newerPlays = await Wait.Until(() => Saw(newer, nameof(PlaybackEngineState.Playing)), TimeSpan.FromSeconds(15));
        supersededGate.SetResult();
        var olderEnded = await Wait.Finishes(olderStart, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Check($"HS-17 LV-12 superseded during the warm-up: the newer http session plays, and the older https start completes without a player or any event "
            + $"(older {olderStart.Status}; events: {Events()}; {rig.Server.OpenConnectionsText})",
            olderWarming && newerPlays && olderEnded && olderStart.IsCompletedSuccessfully && !Raised(older)
            && !events.Any(e => e.Session == newer && e.What.StartsWith("Failed", StringComparison.Ordinal)));
        await engine.StopAsync(CancellationToken.None);

        // A playlist's https entry: the http playlist is not warmed, its entry is, and LibVLC plays the entry only after
        // that. The entry is a listening HTTPS server, so the probe's accepted connections show whether LibVLC ever
        // connected to it (its handshake then fails on the test certificate, which does not matter here).
        await using var probe = LocalTlsServer.Start();
        var entry = probe.Url("/stream");
        rig.Server.HttpsPlaylistEntry = entry;
        var playlist = new StreamSource(rig.Server.Url("/https-entry.m3u"), "https entry");
        var entryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        warmup.Behavior = (_, _) => entryGate.Task;
        calls = warmup.Calls.Count;
        await engine.StartAsync(playlist, 0.5, CancellationToken.None);
        ++session;
        var entryAsked = await Wait.Until(() => warmup.Calls.Count == calls + 1, TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var heldBack = probe.Accepted == 0;
        entryGate.SetResult();
        var entryPlayed = await Wait.Until(() => probe.Accepted > 0, TimeSpan.FromSeconds(15));
        Check($"HS-17 LV-12 an http playlist with an https entry: only the entry is warmed ({string.Join(", ", warmup.Calls.Skip(calls))}), "
            + $"and LibVLC connects to it only after that (none while the warm-up runs: {heldBack}; {probe.Accepted} afterwards; events: {Events()})",
            entryAsked && warmup.Calls.Skip(calls).SequenceEqual([entry]) && heldBack && entryPlayed);
        await engine.StopAsync(CancellationToken.None);

        // Stopped, or superseded by a newer start, while the entry is warmed: the warm-up's token is cancelled (its request
        // ends), and releasing the warm-up afterwards connects nothing.
        foreach (var how in new[] { "stopped", "superseded" })
        {
            var entryWarmupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entryToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            warmup.Behavior = (_, token) => { entryToken.TrySetResult(token); return entryWarmupGate.Task; };
            await engine.StartAsync(playlist, 0.5, CancellationToken.None);
            ++session;
            var asked2 = await Wait.Finishes(entryToken.Task, TimeSpan.FromSeconds(15));
            var accepted = probe.Accepted;
            var replacementPlays = true;
            if (how == "stopped")
            {
                await engine.StopAsync(CancellationToken.None);
            }
            else
            {
                await engine.StartAsync(http, 0.5, CancellationToken.None);
                var replacement = ++session;
                replacementPlays = await Wait.Until(() => Saw(replacement, nameof(PlaybackEngineState.Playing)), TimeSpan.FromSeconds(15));
            }
            var tokenCancelled = asked2 && await Wait.Until(() => entryToken.Task.Result.IsCancellationRequested, TimeSpan.FromSeconds(5));
            entryWarmupGate.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(2));
            Check($"HS-17 LV-12 {how} while the playlist entry is warmed: the warm-up's token is cancelled ({tokenCancelled}), and after it is released "
                + $"LibVLC never connects to the entry ({probe.Accepted - accepted} connections; events: {Events()})",
                tokenCancelled && probe.Accepted == accepted && replacementPlays);
            await engine.StopAsync(CancellationToken.None);
        }

        // Disposed during the warm-up: disposal finishes, disposes the warm-up it owns, and the pending start creates nothing.
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        warmup.Behavior = (_, _) => disposeGate.Task;
        calls = warmup.Calls.Count;
        var last = ++session;
        var pending = engine.StartAsync(https, 0.5, CancellationToken.None);
        var lastWarming = await Wait.Until(() => warmup.Calls.Count == calls + 1, TimeSpan.FromSeconds(10));
        var disposed = await Wait.Finishes(engine.DisposeAsync().AsTask(), TimeSpan.FromSeconds(10));
        Check($"HS-17 LV-12 disposed during the warm-up: DisposeAsync finishes within 10 s and disposes the warm-up the engine owns (disposed: {warmup.Disposed})",
            lastWarming && disposed && warmup.Disposed);
        disposeGate.SetResult();
        var pendingEnded = await Wait.Finishes(pending.ContinueWith(static _ => { }, TaskScheduler.Default), TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Check($"HS-17 LV-12 ... the pending start then ends without a player or any event ({pending.Status}; events: {Events()})",
            pendingEnded && pending.IsCompletedSuccessfully && !Raised(last));

        // Disposed while a playlist entry is warmed (a second engine: the first is disposed).
        var entryWarmup = new FakeTrustWarmup();
        var entryGateAtDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenAtDispose = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        entryWarmup.Behavior = (_, token) => { tokenAtDispose.TrySetResult(token); return entryGateAtDispose.Task; };
        var second = new LibVlcPlaybackEngine(rig.Log, LibVlcEngineOptions.Dummy, entryWarmup);
        var secondEvents = new ConcurrentQueue<string>();
        second.StateChanged += (_, e) => secondEvents.Enqueue($"{e.SessionId}:{e.State}");
        second.Failed += (_, e) => secondEvents.Enqueue($"{e.SessionId}:Failed({e.Kind})");
        await second.StartAsync(playlist, 0.5, CancellationToken.None);
        var entryWarming = await Wait.Finishes(tokenAtDispose.Task, TimeSpan.FromSeconds(15));
        var acceptedAtDispose = probe.Accepted;
        var secondDisposed = await Wait.Finishes(second.DisposeAsync().AsTask(), TimeSpan.FromSeconds(10));
        var cancelledAtDispose = entryWarming && await Wait.Until(() => tokenAtDispose.Task.Result.IsCancellationRequested, TimeSpan.FromSeconds(5));
        var eventsAtDispose = secondEvents.Count;
        entryGateAtDispose.SetResult();
        await Task.Delay(TimeSpan.FromSeconds(2));
        Check($"HS-17 LV-12 disposed while a playlist entry is warmed: DisposeAsync finishes, disposes the warm-up and cancels its token ({cancelledAtDispose}); "
            + $"released afterwards, nothing connects to the entry ({probe.Accepted - acceptedAtDispose} connections) and nothing is raised "
            + $"(events: [{string.Join(", ", secondEvents)}])",
            secondDisposed && entryWarmup.Disposed && cancelledAtDispose && probe.Accepted == acceptedAtDispose && secondEvents.Count == eventsAtDispose);
    }
}
