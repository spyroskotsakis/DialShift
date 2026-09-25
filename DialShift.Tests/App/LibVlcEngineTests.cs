using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DialShift.App.Services;
using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Core;
using DialShift.Tests.Fakes;
using DialShift.Tests.TestServers;
using Microsoft.Extensions.DependencyInjection;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

/// <summary>
/// The engine audio-output seam (all OSes) and the real <see cref="LibVlcPlaybackEngine"/> behind the real
/// <see cref="PlaybackCoordinator"/> (Windows only: acceptance matrix HS-17, MX-10, and the transport rows of the §11 corpus).
/// </summary>
/// <remarks>
/// <para>The Windows checks LV-01..LV-11 run with LibVLC's <c>adummy</c> output (hosted runners have no audio device)
/// against <see cref="LocalMediaServer"/>, so they need no network except one DNS lookup of a <c>.invalid</c> name. The
/// native runtime comes from <c>VideoLAN.LibVLC.Windows</c>, which DialShift.Tests.csproj references when it is built on
/// Windows. The coordinator runs on the real wall clock and a real monotonic clock that the checks can step forward, so
/// the 25 s stall watchdog and the retry countdown are exercised without waiting for them.</para>
/// <para>Not covered: audible output and the native volume level (<c>adummy</c> implements neither volume nor mute, so
/// LibVLC cannot report them), and the live public corpus streams. Both stay in NC-03.</para>
/// </remarks>
public static partial class LibVlcEngineTests
{
    public static async Task RunAsync()
    {
        OptionChecks();
        if (!OperatingSystem.IsWindows())
        {
            Skip("HS-17 LV-01..LV-11 real LibVLC engine + coordinator (adummy output, local server)", "Windows only");
            return;
        }
        await RunOnWindowsAsync();
    }

    /// <summary>The DIALSHIFT_AUDIO_OUTPUT seam: parsing, the LibVLC option it maps to, and factory composition.</summary>
    private static void OptionChecks()
    {
        var asked = new List<string>();
        var unset = PlaybackEngineOptions.FromEnvironment(name => { asked.Add(name); return null; });
        Check("Audio output: the lookup asks only for DIALSHIFT_AUDIO_OUTPUT", asked.SequenceEqual(["DIALSHIFT_AUDIO_OUTPUT"]));
        Check("Audio output: unset or blank -> no override", unset.AudioOutput is null && !unset.DummyAudioOutput
            && PlaybackEngineOptions.FromEnvironment(_ => "  ") == PlaybackEngineOptions.Default);
        Check("Audio output: 'dummy' (trimmed, any case) selects the dummy output",
            PlaybackEngineOptions.FromEnvironment(_ => " dummy ").DummyAudioOutput && PlaybackEngineOptions.FromEnvironment(_ => "DUMMY").DummyAudioOutput);
        var other = PlaybackEngineOptions.FromEnvironment(_ => "wasapi");
        Check("Audio output: any other value is kept for the warning but has no effect", other.AudioOutput == "wasapi" && !other.DummyAudioOutput);

        Check("LibVLC options: Default keeps LibVLC's output; Dummy is adummy",
            LibVlcEngineOptions.Default.AudioOutput is null && LibVlcEngineOptions.Dummy.AudioOutput == "adummy");
        Check("LibVLC options: a module name that could inject options is rejected",
            Throws<ArgumentException>(() => _ = new LibVlcEngineOptions { AudioOutput = "adummy --extraintf=http" })
            && Throws<ArgumentException>(() => _ = new LibVlcEngineOptions { AudioOutput = "" }));

        using var provider = new ServiceCollection().AddDialShiftPlayback().BuildServiceProvider();
        var factory = provider.GetRequiredService<PlaybackEngineFactory>();
        Check("Composition: AddDialShiftPlayback resolves one factory naming this OS's engine",
            ReferenceEquals(factory, provider.GetRequiredService<PlaybackEngineFactory>())
            && factory.EngineName == (OperatingSystem.IsWindows() ? nameof(LibVlcPlaybackEngine) : nameof(MacAvPlayerPlaybackEngine)));
        Check("Composition: no IPlaybackEngine is registered (D17)", provider.GetService<IPlaybackEngine>() is null);
    }

    /// <summary>LV-01 (the engine as the composition root creates it), then LV-02..LV-11 on that engine.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RunOnWindowsAsync()
    {
        var log = new RecordingAppLog();

        // LV-01 resolve: the factory as the composition root uses it, with DIALSHIFT_AUDIO_OUTPUT=dummy.
        var factory = new PlaybackEngineFactory(log, PlaybackEngineOptions.FromEnvironment(
            name => name == PlaybackEngineOptions.AudioOutputVariable ? PlaybackEngineOptions.DummyAudioOutputValue : null));
        var engine = factory.Create();
        Check($"HS-17 LV-01 the factory creates LibVlcPlaybackEngine with the now-playing capability (got {engine.GetType().Name})",
            engine is LibVlcPlaybackEngine and ITrackMetadataProvider);
        Check("HS-17 LV-01 ... and logs playback.audio_output with --aout=adummy",
            log.Entries.Any(e => e.EventName == "playback.audio_output" && e.Message.Contains("--aout=adummy", StringComparison.Ordinal)));
        Check("HS-17 LV-01 ... and a second Create throws (the coordinator's engine is the only one, D17)",
            Throws<InvalidOperationException>(() => factory.Create()));
        await RunEngineChecksAsync(engine, log);
    }

    /// <summary>
    /// LV-02..LV-11 with a fresh LibVLC <paramref name="engine"/> that logs to <paramref name="log"/>; the coordinator takes
    /// ownership. Internal so an out-of-repo harness can run them against another LibVLC build (docs/spikes.md corpus).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static async Task RunEngineChecksAsync(IPlaybackEngine engine, RecordingAppLog log)
    {
        await using var server = LocalMediaServer.Start();
        await using var rig = new Rig(server, log, engine);
        await PlaybackChecksAsync(rig);
        await StopWhileConnectingChecksAsync(rig);
        await CorpusChecksAsync(rig);
        await RetryCountdownChecksAsync(rig);
        await RapidSwitchChecksAsync(rig);
        await DisposeChecksAsync(rig);
        RedactionChecks(rig);
    }

    // ─── LV-02 play, LV-03 volume, LV-04 ICY title, LV-05 stop ───

    [SupportedOSPlatform("windows")]
    private static async Task PlaybackChecksAsync(Rig rig)
    {
        var outcome = await rig.PlayAndSettleAsync(rig.Live);
        Check($"HS-17 LV-02 a local WAV stream reaches coordinator Playing ({outcome})", outcome.Status == PlaybackStatus.Playing);
        var session = rig.Coordinator.Snapshot.IsPlaying ? rig.LastEngineSession : 0;
        Check($"HS-17 LV-02 ... the engine reported Opening and Playing for session {session} (events: {rig.EngineEventsText(session)})",
            rig.EngineStates(session).Contains(PlaybackEngineState.Opening) && rig.EngineStates(session).Contains(PlaybackEngineState.Playing));
        Check($"HS-17 LV-02 ... the track line falls back to the station tag (TrackText '{rig.S.TrackText}')", rig.S.TrackText == rig.Live.Tag);
        Check($"HS-17 LV-02 ... the server holds exactly one stream connection ({rig.Server.OpenConnectionsText})", rig.Server.OpenConnectionsTotal == 1);

        var errorsBefore = rig.EngineErrorCount;
        await rig.Coordinator.SetVolumeAsync(0);
        var mutedHolds = await rig.StaysAsync(() => rig.S.Status == PlaybackStatus.Playing, TimeSpan.FromSeconds(2));
        Check($"HS-17 LV-03 volume 0 (mute) keeps the session Playing ({rig.Describe()}; the adummy output has no native level to read back)",
            mutedHolds && rig.S.Volume == 0 && rig.Settings.Volume == 0);
        await rig.Coordinator.SetVolumeAsync(40);
        var restoredHolds = await rig.StaysAsync(() => rig.S.Status == PlaybackStatus.Playing, TimeSpan.FromSeconds(2));
        Check($"HS-17 LV-03 volume 40 unmutes and keeps Playing ({rig.Describe()})", restoredHolds && rig.S.Volume == 40);
        Check($"HS-17 LV-03 ... no engine error was logged ({rig.EngineErrorsText})", rig.EngineErrorCount == errorsBefore);

        outcome = await rig.PlayAndSettleAsync(rig.Icy);
        Check($"HS-17 LV-04 the ICY stream plays ({outcome})", outcome.Status == PlaybackStatus.Playing);
        var titled = await rig.TickUntilAsync(() => rig.S.TrackText == LocalMediaServer.IcyTitle, TimeSpan.FromSeconds(10));
        var icyRequests = rig.Server.Requests.Where(r => r.Path == "/icy.wav").ToList();
        Check($"HS-17 LV-04 the ICY StreamTitle becomes the track line (TrackText '{rig.S.TrackText}', CurrentTitle '{rig.Metadata.CurrentTitle}'; "
            + $"{icyRequests.Count} requests, {icyRequests.Count(r => r.Header("Icy-MetaData") == "1")} with Icy-MetaData: 1)",
            titled && rig.Metadata.CurrentTitle == LocalMediaServer.IcyTitle);

        var stoppedSession = rig.LastEngineSession;
        var mark = rig.PublishedCount;
        await rig.Coordinator.StopAsync();
        Check($"HS-17 LV-05 stop -> Stopped, inactive, not playing ({rig.Describe()})",
            rig.S is { Status: PlaybackStatus.Stopped, IsActive: false, IsPlaying: false });
        var released = await rig.TickUntilAsync(() => rig.Server.OpenConnectionsTotal == 0, TimeSpan.FromSeconds(10));
        Check($"HS-17 LV-05 ... the player released its stream connection ({rig.Server.OpenConnectionsText})", released);
        Check($"HS-17 LV-05 ... the title is cleared (CurrentTitle '{rig.Metadata.CurrentTitle}')", rig.Metadata.CurrentTitle is null);
        var quiet = await rig.StaysAsync(() => rig.S.Status == PlaybackStatus.Stopped, TimeSpan.FromSeconds(2));
        Check($"HS-17 LV-05 ... nothing revives playback, and no failure is reported for the stopped session (events: {rig.EngineEventsText(stoppedSession)})",
            quiet && !rig.PublishedSince(mark).Any(s => s.IsPlaying) && !rig.EngineFailed(stoppedSession));
    }

    // ─── LV-07 stop while connecting (coordinator and engine level) ───

    [SupportedOSPlatform("windows")]
    private static async Task StopWhileConnectingChecksAsync(Rig rig)
    {
        foreach (var station in new[] { rig.Live, rig.Hang })
        {
            var mark = rig.PublishedCount;
            await rig.Coordinator.PlayAsync(station.Id);
            await rig.Coordinator.StopAsync();
            var stays = await rig.StaysAsync(() => rig.S.Status == PlaybackStatus.Stopped, TimeSpan.FromSeconds(3));
            Check($"HS-17 LV-07 play then stop at once ({station.Name}): stays Stopped and never Playing ({rig.Describe()})",
                stays && !rig.PublishedSince(mark).Any(s => s.IsPlaying));
            var released = await rig.TickUntilAsync(() => rig.Server.OpenConnectionsTotal == 0, TimeSpan.FromSeconds(10));
            Check($"HS-17 LV-07 ... no connection is left open ({rig.Server.OpenConnectionsText})", released);
        }

        // Connecting to a server that never answers, then stop: the coordinator is past the start when Stop arrives.
        await rig.Coordinator.PlayAsync(rig.Hang.Id);
        var connecting = await rig.TickUntilAsync(() => rig.Server.OpenConnections("/hang") == 1, TimeSpan.FromSeconds(10));
        Check($"HS-17 LV-07 the engine is connecting to a silent server ({rig.Describe()}; {rig.Server.OpenConnectionsText})",
            connecting && rig.S.Status == PlaybackStatus.Connecting);
        await rig.Coordinator.StopAsync();
        var releasedHang = await rig.TickUntilAsync(() => rig.Server.OpenConnectionsTotal == 0, TimeSpan.FromSeconds(10));
        Check($"HS-17 LV-07 ... stop closes the pending connection ({rig.Server.OpenConnectionsText}) and stays Stopped ({rig.Describe()})",
            releasedHang && rig.S.Status == PlaybackStatus.Stopped);

        // The engine itself, with overlapping calls as the coordinator issues them (D16).
        var events = new ConcurrentQueue<string>();
        await using var direct = new LibVlcPlaybackEngine(rig.Log, LibVlcEngineOptions.Dummy);
        direct.StateChanged += (_, e) => events.Enqueue($"{e.SessionId}:{e.State}");
        direct.Failed += (_, e) => events.Enqueue($"{e.SessionId}:Failed({e.Kind})");
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            var start = direct.StartAsync(new StreamSource(rig.Server.Url("/live.wav"), "cancelled"), 0.5, cancelled.Token);
            var ended = await Wait.Finishes(start.ContinueWith(_ => { }, TaskScheduler.Default), TimeSpan.FromSeconds(10));
            Check($"HS-17 LV-07 engine: a start with a cancelled token ends at once, cancelled or completed ({start.Status})",
                ended && start.Status is TaskStatus.Canceled or TaskStatus.RanToCompletion);
        }
        var hangStart = direct.StartAsync(new StreamSource(rig.Server.Url("/hang"), "hang"), 0.5, CancellationToken.None);
        var stop = direct.StopAsync(CancellationToken.None);
        Check("HS-17 LV-07 engine: start + stop without awaiting the start both finish within 10 s",
            await Wait.Finishes(Task.WhenAll(hangStart, stop), TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var released2 = await Wait.Until(() => rig.Server.OpenConnectionsTotal == 0, TimeSpan.FromSeconds(10));
        // Opening may precede the stop (a warm engine reaches Play() before StartAsync returns); nothing may follow it.
        Check($"HS-17 LV-07 engine: the cancelled session 1 raises nothing, the stopped session 2 never plays or fails, and no connection is held "
            + $"(events: [{string.Join(", ", events)}]; {rig.Server.OpenConnectionsText})",
            released2 && !events.Any(e => e.StartsWith("1:", StringComparison.Ordinal))
            && !events.Any(e => e.Contains("Playing", StringComparison.Ordinal) || e.Contains("Failed", StringComparison.Ordinal)));

        var ftp = direct.StartAsync(new StreamSource(new Uri("ftp://127.0.0.1/live.wav"), "ftp"), 0.5, CancellationToken.None);
        var rejected = await Wait.Finishes(ftp, TimeSpan.FromSeconds(5))
            && await Wait.Until(() => events.Contains("3:Failed(InvalidUrl)"), TimeSpan.FromSeconds(5));
        Check($"HS-17 LV-08 engine: an ftp:// source fails as InvalidUrl, reported as an event (events: [{string.Join(", ", events)}])", rejected);
    }

    // ─── LV-08 the corpus transport cases: plays and normalized failure kinds ───

    /// <remarks>
    /// Basic auth runs on two paths of the same host and port, each with its own realm. LibVLC 3 keeps credentials from a
    /// URL's user-info for the life of the LibVLC instance, and 3.0.23.1 sends them with the first request of a later
    /// station on the same scheme://host:port and path, before any challenge (see the <see cref="LibVlcPlaybackEngine"/>
    /// header). So the 401 case uses the other path and realm, which must fail whatever ran before it, and "same realm, no
    /// credentials" runs right after the user-info station to record which behavior this LibVLC build has. Every case
    /// reports the requests the server received, with or without an Authorization header (never its value).
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static async Task CorpusChecksAsync(Rig rig)
    {
        (Station Station, string Expected)[] cases =
        [
            (rig.Redirect, "Playing"), (rig.AuthOk, "Playing"), (rig.AuthSameRealm, SameRealmExpectation), (rig.Pls, "Playing"),
            (rig.M3u, "Playing"), (rig.Status404, nameof(PlaybackFailureKind.HttpError)), (rig.Status403, nameof(PlaybackFailureKind.HttpError)),
            (rig.Status500, nameof(PlaybackFailureKind.HttpError)), (rig.AuthOtherRealm, nameof(PlaybackFailureKind.HttpError)),
            (rig.Portal, nameof(PlaybackFailureKind.UnsupportedFormat)), (rig.Refused, nameof(PlaybackFailureKind.NetworkUnavailable)),
            (rig.InvalidHost, nameof(PlaybackFailureKind.NetworkUnavailable)), (rig.Ftp, nameof(PlaybackFailureKind.InvalidUrl))
        ];
        var results = new List<(Station Station, string Expected, Outcome Outcome, IReadOnlyList<ServerRequest> Requests)>();
        foreach (var (station, expected) in cases)
        {
            var requestMark = rig.Server.Requests.Count;
            var outcome = await rig.PlayAndSettleAsync(station);
            await rig.StopAndDrainAsync(); // every request of the attempt has arrived by then
            results.Add((station, expected, outcome, [.. rig.Server.Requests.Skip(requestMark)]));
        }

        // A stream that ends: Playing, then the engine's Ended and a failure of kind EndOfStream.
        var ends = await rig.PlayAndSettleAsync(rig.Ends);
        var endsSession = rig.LastEngineSession;
        var endsMark = rig.Log.Entries.Count;
        var ended = ends.Status == PlaybackStatus.Playing
            && await rig.TickUntilAsync(() => rig.S.Status == PlaybackStatus.Failed, TimeSpan.FromSeconds(20));
        var endsFailure = await rig.FailureSinceAsync(endsMark);
        await rig.StopAndDrainAsync();

        // A server that never answers: no native error, so the coordinator's 25 s watchdog fails it (stepped, not waited).
        await rig.Coordinator.PlayAsync(rig.Hang.Id);
        var hanging = await rig.TickUntilAsync(() => rig.Server.OpenConnections("/hang") == 1, TimeSpan.FromSeconds(10));
        var mark = rig.Log.Entries.Count;
        for (var i = 0; i < 3 && rig.S.Status == PlaybackStatus.Connecting; i++) await rig.StepAsync(TimeSpan.FromSeconds(9));
        var watchdog = await rig.FailureSinceAsync(mark);
        await rig.StopAndDrainAsync();

        Console.WriteLine("  Corpus results (case: expected -> outcome):");
        foreach (var (station, expected, outcome, requests) in results) Console.WriteLine($"    {station.Name}: {expected} -> {outcome}; {ServerSaw(requests)}");
        Console.WriteLine($"    {rig.Ends.Name}: Playing, then EndOfStream -> {ends}, then {endsFailure}");
        Console.WriteLine($"    {rig.Hang.Name}: Stalled after 27 stepped seconds -> {watchdog}");

        foreach (var (station, expected, outcome, requests) in results.Where(r => r.Station != rig.AuthSameRealm))
            Check($"HS-17 LV-08 {station.Name}: expected {expected}, got {outcome}; {ServerSaw(requests)}", outcome.Matches(expected));
        Check($"HS-17 LV-08 {rig.Ends.Name}: plays, then the engine reports Ended and the coordinator fails EndOfStream "
            + $"(first: {ends}; then: {endsFailure}; events: {rig.EngineEventsText(endsSession)})",
            ended && endsFailure.Kind == nameof(PlaybackFailureKind.EndOfStream) && rig.EngineStates(endsSession).Contains(PlaybackEngineState.Ended));
        Check($"HS-17 LV-08 {rig.Hang.Name}: the connection is held open, and the watchdog fails it as Stalled after >25 s outside Playing (got {watchdog})",
            hanging && watchdog.Kind == nameof(PlaybackFailureKind.Stalled));
        var authRequests = rig.Server.Requests.Where(r => r.Path == AuthPath).ToList();
        Check($"HS-17 LV-08 user-info credentials reach the server only as a Basic Authorization header ({authRequests.Count} requests)",
            authRequests.Any(r => r.Header("Authorization") == LocalMediaServer.ExpectedAuthorization));

        // Same path and realm, no credentials in the URL, right after the user-info station. Either LibVLC reuses the
        // credentials it kept (3.0.23.1: preemptively, on the first request) and plays, or it kept none and fails with
        // HttpError. Both pass; the check names the behavior. Credentials elsewhere fail the protection-space check below.
        var (_, _, reuse, reuseRequests) = results.Single(r => r.Station == rig.AuthSameRealm);
        var reused = reuseRequests.Any(r => r.Header("Authorization") == LocalMediaServer.ExpectedAuthorization);
        var behavior = !reused ? "kept no credentials: no request carried an Authorization header"
            : reuseRequests[0].Header("Authorization") is not null ? "reused the user-info station's credentials preemptively, on the first request"
            : "reused the user-info station's credentials after the server's 401 challenge";
        Check($"HS-17 LV-08 {rig.AuthSameRealm.Name}: LibVLC {behavior} (got {reuse}; {ServerSaw(reuseRequests)})",
            reuseRequests.Count > 0 && reuseRequests.All(r => r.Path == AuthPath)
            && (reused
                ? reuse.Status == PlaybackStatus.Playing && reuseRequests.All(r => r.Header("Authorization") is not { } a || a == LocalMediaServer.ExpectedAuthorization)
                : reuse.Kind == nameof(PlaybackFailureKind.HttpError) && reuseRequests.All(r => r.Header("Authorization") is null)));

        // No credentials outside the protection space they were given for: not the other realm, not any other path.
        var leaked = rig.Server.Requests.Where(r => r.Path != AuthPath && r.Header("Authorization") is not null).ToList();
        Check($"HS-17 LV-08 credentials stay in their protection space: no request outside {AuthPath} (realm '{LocalMediaServer.AuthRealm}') "
            + $"carried an Authorization header ({rig.Server.Requests.Count(r => r.Path != AuthPath)} requests; with one: [{string.Join(", ", leaked)}])",
            leaked.Count == 0);
    }

    // ─── LV-09 retry countdown (3 s, then 6 s) ───

    [SupportedOSPlatform("windows")]
    private static async Task RetryCountdownChecksAsync(Rig rig)
    {
        var first = await rig.PlayAndSettleAsync(rig.Refused);
        Check($"HS-17 LV-09 a refused connection fails ({first})", first.Status == PlaybackStatus.Failed);
        Check($"HS-17 LV-09 ... first retry in 3 s (RetryInSeconds {rig.S.RetryInSeconds}, '{rig.S.StatusText}')",
            rig.S.RetryInSeconds == 3 && rig.S.StatusText == "Stream unavailable · retry in 3s" && rig.S.IsActive);
        await rig.StepAsync(TimeSpan.FromSeconds(1));
        Check($"HS-17 LV-09 ... one second later it counts down to 2 (RetryInSeconds {rig.S.RetryInSeconds}, {rig.S.Status})",
            rig.S is { Status: PlaybackStatus.Failed, RetryInSeconds: 2 });
        var mark = rig.Log.Entries.Count;
        await rig.StepAsync(TimeSpan.FromSeconds(2));
        Check($"HS-17 LV-09 ... at 3 s the coordinator reconnects ({rig.Describe()})", rig.S.Status == PlaybackStatus.Reconnecting);
        var failedAgain = await rig.TickUntilAsync(() => rig.S.Status == PlaybackStatus.Failed, TimeSpan.FromSeconds(20));
        var second = await rig.FailureSinceAsync(mark);
        Check($"HS-17 LV-09 ... the second failure retries in 6 s ({second}; RetryInSeconds {rig.S.RetryInSeconds})",
            failedAgain && rig.S.RetryInSeconds == 6 && second.Message.Contains("attempt=2;", StringComparison.Ordinal));
        await rig.StopAndDrainAsync();
    }

    // ─── LV-06 rapid switching ───

    [SupportedOSPlatform("windows")]
    private static async Task RapidSwitchChecksAsync(Rig rig)
    {
        Station[] cycle = [rig.Live, rig.Icy, rig.Pls, rig.Redirect, rig.M3u, rig.AuthOk, rig.Hang];
        var random = new Random(20260925);
        var errorsBefore = rig.EngineErrorCount;
        const int rounds = 8, switches = 20;
        var watch = Stopwatch.StartNew();
        for (var round = 1; round <= rounds; round++)
        {
            Station last = rig.Live;
            for (var i = 0; i < switches; i++)
            {
                // Ends on a playable station; the silent /hang server is in the churn but never last.
                last = i == switches - 1 ? cycle[round % 6] : cycle[random.Next(cycle.Length)];
                await rig.Coordinator.PlayAsync(last.Id);
                if (i % 5 == 0) await rig.Coordinator.OnTickAsync(CancellationToken.None);
                await Task.Delay(random.Next(0, 40));
            }
            var playing = await rig.TickUntilAsync(() => rig.S.Status == PlaybackStatus.Playing && rig.S.CurrentStationId == last.Id, TimeSpan.FromSeconds(30));
            Check($"HS-17 LV-06 round {round}/{rounds}: {switches} rapid switches end Playing '{last.Name}' ({rig.Describe()})", playing);
        }
        var single = await rig.TickUntilAsync(() => rig.Server.OpenConnectionsTotal == 1, TimeSpan.FromSeconds(15));
        Check($"HS-17 LV-06 after {rounds * switches} switches in {watch.Elapsed.TotalSeconds:0}s exactly one stream connection remains ({rig.Server.OpenConnectionsText})", single);
        Check($"HS-17 LV-06 ... with no engine or coordinator error logged ({rig.EngineErrorsText})", rig.EngineErrorCount == errorsBefore);
    }

    // ─── LV-10 dispose ───

    [SupportedOSPlatform("windows")]
    private static async Task DisposeChecksAsync(Rig rig)
    {
        if (rig.S.Status != PlaybackStatus.Playing) await rig.PlayAndSettleAsync(rig.Live);
        var errorsBefore = rig.EngineErrorCount;
        var dispose = rig.Coordinator.DisposeAsync().AsTask();
        Check($"HS-17 LV-10 disposing the coordinator while Playing finishes within 10 s (status {dispose.Status})",
            await Wait.Finishes(dispose, TimeSpan.FromSeconds(10)));
        Check($"HS-17 LV-10 ... the coordinator is Disposing ({rig.Describe()})", rig.S.Status == PlaybackStatus.Disposing);
        var released = await Wait.Until(() => rig.Server.OpenConnectionsTotal == 0, TimeSpan.FromSeconds(10));
        Check($"HS-17 LV-10 ... the engine released every stream connection ({rig.Server.OpenConnectionsText})", released);
        Check("HS-17 LV-10 ... the engine it owned is disposed: StartAsync throws ObjectDisposedException",
            await ThrowsAsync<ObjectDisposedException>(() => rig.Engine.StartAsync(new StreamSource(rig.Server.Url("/live.wav"), "late"), 0.5, CancellationToken.None)));
        Check("HS-17 LV-10 ... and StopAsync/DisposeAsync stay harmless",
            await NoThrowAsync(async () => { await rig.Engine.StopAsync(CancellationToken.None); await rig.Engine.DisposeAsync(); }));
        Check($"HS-17 LV-10 ... playback.disposed is logged and teardown logged no engine error ({rig.EngineErrorsText})",
            rig.Log.HasEvent("playback.disposed") && rig.EngineErrorCount == errorsBefore);
    }

    // ─── LV-11 redaction ───

    [SupportedOSPlatform("windows")]
    private static void RedactionChecks(Rig rig)
    {
        var entries = rig.Log.Entries;
        var diagnostics = entries.Count(e => e.Message.Contains("diagnostic=libvlc", StringComparison.Ordinal));
        Check($"HS-17 LV-11 LibVLC failure diagnostics reached the log ({diagnostics} of {entries.Count} entries)", diagnostics > 0);
        var leaks = entries.Where(e => rig.Secrets.Any(s => e.Text.Contains(s, StringComparison.OrdinalIgnoreCase))).Select(e => e.EventName).ToList();
        Check($"HS-17 LV-11 no log entry contains the password, the token, or a URL beyond scheme://host:port (leaking events: [{string.Join(", ", leaks)}])",
            leaks.Count == 0);
    }

    private const string AuthPath = "/auth/live.wav";

    /// <summary>The same-realm case accepts both LibVLC behaviors; its check records which one this build has.</summary>
    private const string SameRealmExpectation = "Playing with the kept credentials (same path), or HttpError";

    /// <summary>"server saw [/auth/live.wav (no Authorization), …]": the requests of one attempt, for check names and CI output.</summary>
    private static string ServerSaw(IReadOnlyList<ServerRequest> requests) => $"server saw [{string.Join(", ", requests)}]";

    /// <summary>How one attempt settled: the coordinator status plus the kind and diagnostic of its <c>playback.failed</c> line.</summary>
    private sealed record Outcome(PlaybackStatus Status, double Seconds, string? Kind, string? Diagnostic)
    {
        public bool Matches(string expected) => expected == nameof(PlaybackStatus.Playing) ? Status == PlaybackStatus.Playing : Kind == expected;

        public override string ToString() => Kind is null
            ? $"{Status} after {Seconds:0.0}s"
            : $"{Status} kind={Kind} after {Seconds:0.0}s{(Diagnostic is null ? "" : $" [{Diagnostic}]")}";
    }

    /// <summary>A <c>playback.failed</c> log line.</summary>
    private sealed record FailureLine(string? Kind, string Message)
    {
        public override string ToString() => Kind is null ? "no playback.failed line" : $"kind={Kind} [{Message}]";
    }

    /// <summary>Real time plus a test-controlled offset: the coordinator's timers can be stepped past without waiting.</summary>
    private sealed class SteppableMonotonicClock : IMonotonicClock
    {
        private long offsetTicks;

        public long GetTimestamp() => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks + Interlocked.Read(ref offsetTicks);

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan by) => Interlocked.Add(ref offsetTicks, by.Ticks);
    }

    /// <summary>The coordinator, its LibVLC engine, the local server, the stations and every observation the checks need.</summary>
    [SupportedOSPlatform("windows")]
    private sealed partial class Rig : IAsyncDisposable
    {
        private const string Token = "tok3n-secret-91c2";
        private readonly ConcurrentQueue<(long Session, string What)> engineEvents = new();
        private readonly Lock publishedGate = new();
        private readonly List<PlaybackSnapshot> published = [];
        private readonly SteppableMonotonicClock mono = new();

        public Rig(LocalMediaServer server, RecordingAppLog log, IPlaybackEngine engine)
        {
            Server = server;
            Log = log;
            Engine = engine;
            Metadata = (ITrackMetadataProvider)engine;
            var closedPort = LocalMediaServer.ClosedPort();
            var port = server.Port;
            Station Local(string name, string path) => Make(name, $"http://127.0.0.1:{port}{path}?token={Token}");
            Live = Local("Live", "/live.wav");
            Icy = Local("ICY", "/icy.wav");
            Ends = Local("Ends", "/ends.wav");
            Redirect = Local("Redirect", "/redirect");
            AuthOk = Make("Auth with user-info", $"http://{LocalMediaServer.AuthUser}:{LocalMediaServer.AuthPassword}@127.0.0.1:{port}/auth/live.wav?token={Token}");
            AuthSameRealm = Local("Auth, same realm, no credentials", AuthPath);
            AuthOtherRealm = Local("Auth, other realm, no credentials (401)", "/other-realm/live.wav");
            Pls = Local("PLS playlist", "/station.pls");
            M3u = Local("M3U playlist", "/station.m3u");
            Status404 = Local("HTTP 404", "/status/404");
            Status403 = Local("HTTP 403", "/status/403");
            Status500 = Local("HTTP 500", "/status/500");
            Portal = Local("Captive-portal HTML", "/portal.html");
            Hang = Local("Never answers", "/hang");
            Refused = Make("Connection refused", $"http://127.0.0.1:{closedPort}/live.wav?token={Token}");
            InvalidHost = Make("Unresolvable .invalid host", $"http://stream.dialshift-test.invalid/live.wav?token={Token}");
            Ftp = Make("ftp:// URL", $"ftp://127.0.0.1:{port}/live.wav?token={Token}");
            Secrets = [LocalMediaServer.AuthPassword, Token, $":{port}/", $":{closedPort}/", ".invalid/", "/live.wav", "/auth/", "/other-realm/"];

            Settings = new Settings
            {
                Volume = 70,
                Stations = [Live, Icy, Ends, Redirect, AuthOk, AuthSameRealm, AuthOtherRealm, Pls, M3u, Status404, Status403, Status500, Portal, Hang, Refused, InvalidHost, Ftp]
            };
            engine.StateChanged += (_, e) => engineEvents.Enqueue((e.SessionId, e.State.ToString()));
            engine.Failed += (_, e) => engineEvents.Enqueue((e.SessionId, $"Failed({e.Kind})"));
            Coordinator = new PlaybackCoordinator(Settings, engine, SystemClock.Instance, mono, log, TimeZoneInfo.Utc);
            Coordinator.SnapshotChanged += (_, s) => { lock (publishedGate) published.Add(s); };

            static Station Make(string name, string url) => new() { Name = name, Tag = name + " tag", Url = url };
        }

        public LocalMediaServer Server { get; }
        public RecordingAppLog Log { get; }
        public IPlaybackEngine Engine { get; }
        public ITrackMetadataProvider Metadata { get; }
        public Settings Settings { get; }
        public PlaybackCoordinator Coordinator { get; }
        public PlaybackSnapshot S => Coordinator.Snapshot;
        public string[] Secrets { get; }

        public Station Live { get; }
        public Station Icy { get; }
        public Station Ends { get; }
        public Station Redirect { get; }
        public Station AuthOk { get; }
        public Station AuthSameRealm { get; }
        public Station AuthOtherRealm { get; }
        public Station Pls { get; }
        public Station M3u { get; }
        public Station Status404 { get; }
        public Station Status403 { get; }
        public Station Status500 { get; }
        public Station Portal { get; }
        public Station Hang { get; }
        public Station Refused { get; }
        public Station InvalidHost { get; }
        public Station Ftp { get; }

        /// <summary>Newest session id seen in an engine event.</summary>
        public long LastEngineSession => engineEvents.IsEmpty ? 0 : engineEvents.Max(e => e.Session);

        public int PublishedCount { get { lock (publishedGate) return published.Count; } }

        public IReadOnlyList<PlaybackSnapshot> PublishedSince(int mark) { lock (publishedGate) return [.. published.Skip(mark)]; }

        public IReadOnlyList<PlaybackEngineState> EngineStates(long session) =>
            [.. engineEvents.Where(e => e.Session == session).Select(e => Enum.TryParse<PlaybackEngineState>(e.What, out var state) ? state : (PlaybackEngineState?)null).OfType<PlaybackEngineState>()];

        public bool EngineFailed(long session) => engineEvents.Any(e => e.Session == session && e.What.StartsWith("Failed", StringComparison.Ordinal));

        public string EngineEventsText(long session) => $"[{string.Join(", ", engineEvents.Where(e => e.Session == session).Select(e => e.What))}]";

        public int EngineErrorCount => Log.Entries.Count(IsEngineError);

        public string EngineErrorsText => string.Join(" | ", Log.Entries.Where(IsEngineError).Select(e => $"{e.EventName}: {e.Message} {e.Exception?.GetType().Name}")) is { Length: > 0 } text ? text : "none";

        public string Describe() =>
            $"status={S.Status}, station='{S.CurrentStationName}', playing={S.IsPlaying}, active={S.IsActive}, retry={S.RetryInSeconds?.ToString() ?? "-"}, text='{S.StatusText}'"
            + (S.Status == PlaybackStatus.Failed ? $", failure: {LastFailure()}" : "");

        /// <summary>Ticks the coordinator (as the app's 1 s loop does, but every 50 ms) until <paramref name="condition"/> holds.</summary>
        public async Task<bool> TickUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                await Coordinator.OnTickAsync(CancellationToken.None);
                if (condition()) return true;
                if (watch.Elapsed > timeout) return false;
                await Task.Delay(50);
            }
        }

        /// <summary>Ticks for <paramref name="duration"/>; true when <paramref name="condition"/> held after every tick.</summary>
        public async Task<bool> StaysAsync(Func<bool> condition, TimeSpan duration)
        {
            var held = true;
            await TickUntilAsync(() => { held &= condition(); return false; }, duration);
            return held;
        }

        /// <summary>Steps the monotonic clock by <paramref name="by"/> (below the 15 s wake-gap threshold) and ticks once.</summary>
        public Task StepAsync(TimeSpan by)
        {
            mono.Advance(by);
            return Coordinator.OnTickAsync(CancellationToken.None);
        }

        public async Task<Outcome> PlayAndSettleAsync(Station station)
        {
            var mark = Log.Entries.Count;
            var watch = Stopwatch.StartNew();
            await Coordinator.PlayAsync(station.Id);
            await TickUntilAsync(() => S.Status is PlaybackStatus.Playing or PlaybackStatus.Failed, TimeSpan.FromSeconds(20));
            var failure = S.Status == PlaybackStatus.Failed ? await FailureSinceAsync(mark) : null;
            return new Outcome(S.Status, watch.Elapsed.TotalSeconds, failure?.Kind, failure is null ? null : DiagnosticPattern().Match(failure.Message) is { Success: true } m ? m.Groups[1].Value : null);
        }

        /// <summary>The newest <c>playback.failed</c> line, optionally only among entries after <paramref name="mark"/>.</summary>
        public FailureLine LastFailure(int mark = 0)
        {
            var entry = Log.Entries.Skip(mark).LastOrDefault(e => e.EventName == "playback.failed");
            return entry is null ? new FailureLine(null, "") : new FailureLine(KindPattern().Match(entry.Message).Groups[1].Value, entry.Message);
        }

        /// <summary>
        /// The <c>playback.failed</c> line logged after <paramref name="mark"/>, waiting up to 2 s for it: the coordinator
        /// publishes the Failed snapshot before it writes its pending log lines.
        /// </summary>
        public async Task<FailureLine> FailureSinceAsync(int mark)
        {
            await Wait.Until(() => LastFailure(mark).Kind is not null, TimeSpan.FromSeconds(2));
            return LastFailure(mark);
        }

        /// <summary>User stop, then wait until the engine has released every server connection.</summary>
        public async Task StopAndDrainAsync()
        {
            await Coordinator.StopAsync();
            await TickUntilAsync(() => Server.OpenConnectionsTotal == 0, TimeSpan.FromSeconds(10));
        }

        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();

        private static bool IsEngineError(AppLogEntry e) =>
            e.EventName is "playback.engine_error" or "playback.engine_event_failed" or "playback.internal_error";

        [GeneratedRegex(@"kind=(\w+)")]
        private static partial Regex KindPattern();

        [GeneratedRegex(@"diagnostic=(.*)$")]
        private static partial Regex DiagnosticPattern();
    }
}
