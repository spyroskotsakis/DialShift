using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>Exact status/track texts; they mirror today's <c>RadioController</c> strings.</summary>
public static class Texts
{
    public const string Ready = "Ready when you are";
    public const string ReadyTrack = "Choose a station and make yourself at home.";
    public const string Connecting = "Connecting…";
    public const string ConnectingFallback = "Connecting to fallback…";
    public const string Opening = "Opening the live stream";
    public const string Live = "Live broadcast";
    public const string LiveFallback = "Live · fallback station";
    public const string RetryTrack = "Your next scheduled change will still run.";
    public const string PausedSchedule = "Paused · resumes at the next scheduled change";
    public const string Paused = "Paused";
    public const string PausedTrack = "Press play to return to the live broadcast.";

    public static string Retry(int seconds) => $"Stream unavailable · retry in {seconds}s";
}

/// <summary>
/// <see cref="PlaybackCoordinator"/> behavior: acceptance-matrix §7.5 CT-PB-01..40, the wake rules (§4.4), zone/DST
/// threading of the injected <c>localZone</c>, CT-LOG (Core part), and adversarial checks (ADV-*) that try to break the
/// coordinator's generation/session/cancellation rules. Time moves only through the fake clocks (§7.1 <c>Step(n)</c>).
/// Engine callbacks raised on the test thread are drained synchronously (the gate is free), so single-threaded checks
/// read the snapshot right after raising them.
/// </summary>
public static class PlaybackCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync()
    {
        await ScheduleStartAndLive();
        await ManualHoldWithinSlot();
        await PauseHoldsWithinSlot();
        await BackoffSequence();
        await Countdown();
        await FallbackAfterThreeFailures();
        await NeverGivesUp();
        await FallbackEqualToDesired();
        await PrimaryRecheckAndAlternation();
        await FailingFallbackReturnsToPrimary();
        await StablePlaybackReset();
        await FallbackDoesNotResetFailures();
        await StallWatchdog();
        await EndOfStreamAndDuplicateFailures();
        await StaleSessionEvents();
        await StopCancelsRetryAndFallback();
        await StopDuringHeldRetryStart();
        await Volume();
        await Toggle();
        await NextStation();
        await ForgetStation();
        await ForcedRefreshAndScheduleOff();
        await WakeSettleAndReconnect();
        await WakeTickGapIsMonotonic();
        await WakeKeepsPauseAndOpensNewSlotOnce();
        await WakeIdempotentAndDebounced();
        await Dispose();
        await SettingsChanged();
        await Metadata();
        await PauseTexts();
        await UpcomingAndZone();
        await DstWithInjectedZone();
        await RapidPlays();
        await LogRedaction();
        await Adversarial();
    }

    // ─── CT-PB-01..02 ───

    private static async Task ScheduleStartAndLive()
    {
        await using var rig = CoordinatorRig.Create(schedule: true);
        Check("CT-PB-01 initial snapshot: Stopped with today's first-launch texts and the settings volume",
            rig.Status == PlaybackStatus.Stopped && rig.S.StatusText == Texts.Ready && rig.S.TrackText == Texts.ReadyTrack && rig.S.Volume == 60);
        rig.Settings.Volume = 45;
        await rig.Coordinator.StartScheduleAsync();
        var s = rig.S;
        Check("CT-PB-01 StartScheduleAsync: exactly one engine start, for the current slot's station A",
            rig.StartCount == 1 && rig.LastStart.Source == new StreamSource(new Uri(rig.A.Url), "Alpha"));
        Check("CT-PB-01 the start carries Settings.Volume / 100.0", rig.LastStart.Volume == 45 / 100.0);
        Check("CT-PB-01 snapshot: Connecting, Desired = Current = A, active, not playing, no retry",
            s.Status == PlaybackStatus.Connecting && s.DesiredStationId == rig.A.Id && s.CurrentStationId == rig.A.Id && s.CurrentStationName == "Alpha"
            && s.IsActive && !s.IsPlaying && !s.IsFallback && s.RetryInSeconds is null);
        Check("CT-PB-01 texts: \"Connecting…\" / \"Opening the live stream\"", s.StatusText == Texts.Connecting && s.TrackText == Texts.Opening);
        Check("CT-PB-01 LastStationId and schedule.fired follow the scheduled start", rig.Settings.LastStationId == rig.A.Id && rig.LogCount("schedule.fired") == 1);
        rig.Playing();
        s = rig.S;
        Check("CT-PB-02 engine Playing (current session): Playing, IsPlaying, \"Live broadcast\", track = station tag",
            s.Status == PlaybackStatus.Playing && s.IsPlaying && s.StatusText == Texts.Live && s.TrackText == "Tag A");
    }

    // ─── CT-PB-03..04 ───

    private static async Task ManualHoldWithinSlot()
    {
        await using var rig = CoordinatorRig.Create(schedule: true);
        await rig.Coordinator.StartScheduleAsync();
        rig.Playing();
        await rig.Coordinator.PlayAsync(rig.C.Id);
        rig.Playing();
        await rig.Step(60);
        Check("CT-PB-03 manual PlayAsync(C) inside slot A holds: 60 ticks later Desired is still C, no extra start",
            rig.S.DesiredStationId == rig.C.Id && rig.StartCount == 2 && rig.Status == PlaybackStatus.Playing);
        rig.SetLocal(new DateTime(2026, 9, 14, 10, 59, 59));
        await rig.Step(1);
        Check("CT-PB-03 the next occurrence (B 11:00) overrides the manual choice",
            rig.S.DesiredStationId == rig.B.Id && rig.StartCount == 3 && rig.LastStart.Source.DisplayName == "Bravo" && rig.Status == PlaybackStatus.Connecting);
    }

    private static async Task PauseHoldsWithinSlot()
    {
        await using var rig = CoordinatorRig.Create(schedule: true);
        await rig.Coordinator.StartScheduleAsync();
        rig.Playing();
        await rig.Coordinator.StopAsync();
        await rig.Step(5);
        Check("CT-PB-04 pause inside slot A: no engine start for 5 ticks, status ScheduledWaiting, not active",
            rig.StartCount == 1 && rig.Status == PlaybackStatus.ScheduledWaiting && !rig.S.IsActive && rig.Engine.ActiveSessionId is null);
        rig.SetLocal(new DateTime(2026, 9, 14, 10, 59, 59));
        await rig.Step(1);
        Check("CT-PB-04 11:00 arrives: B starts even though the user paused (a pause holds only within its slot)",
            rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Bravo" && rig.S.IsActive && rig.Status == PlaybackStatus.Connecting);
    }

    // ─── CT-PB-05..14: retry, backoff, fallback ───

    private static async Task BackoffSequence()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Playing();
        rig.Fail();
        var s = rig.S;
        Check("CT-PB-05 failure #1: Failed, RetryInSeconds 3, active, not playing",
            s.Status == PlaybackStatus.Failed && s.RetryInSeconds == 3 && s.IsActive && !s.IsPlaying && s.DesiredStationId == rig.A.Id);
        Check("CT-PB-05 failure #1 texts: \"Stream unavailable · retry in 3s\" / \"Your next scheduled change will still run.\"",
            s.StatusText == Texts.Retry(3) && s.TrackText == Texts.RetryTrack);
        Check("CT-PB-05 the failed session is stopped on the engine", rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]) && rig.Engine.ActiveSessionId is null);
        await rig.Step(2);
        Check("CT-PB-05 Step(2): no retry yet", rig.StartCount == 1 && rig.Status == PlaybackStatus.Failed);
        await rig.Step(1);
        Check("CT-PB-05 Step(1) more: A is retried as Reconnecting with \"Connecting…\"",
            rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Alpha" && rig.Status == PlaybackStatus.Reconnecting
            && rig.S.StatusText == Texts.Connecting && rig.S.TrackText == Texts.Opening && rig.S.RetryInSeconds is null);
        rig.Fail();
        Check("CT-PB-05 failure #2 waits 6 s", rig.S.RetryInSeconds == 6 && rig.S.StatusText == Texts.Retry(6));
        await rig.Step(5);
        Check("CT-PB-05 failure #2: no retry after 5 s", rig.StartCount == 2);
        await rig.Step(1);
        Check("CT-PB-05 failure #2: retry at 6 s", rig.StartCount == 3);
        rig.Fail();
        Check("CT-PB-05 failure #3 waits 30 s", rig.S.RetryInSeconds == 30);
        await rig.Step(29);
        Check("CT-PB-05 failure #3: no retry after 29 s", rig.StartCount == 3);
        await rig.Step(1);
        Check("CT-PB-05 failure #3: retry at 30 s", rig.StartCount == 4 && rig.Status == PlaybackStatus.Reconnecting);
        Check("CT-PB-05 each failure is logged as playback.failed with its attempt number",
            rig.Log.Entries.Count(e => e.EventName == "playback.failed" && e.Level == AppLogLevel.Warn && e.Message.Contains("attempt=")) == 3);
    }

    private static async Task Countdown()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Fail();
        var texts = new List<(string, int?)> { (rig.S.StatusText, rig.S.RetryInSeconds) };
        for (var i = 0; i < 2; i++)
        {
            await rig.Step(1);
            texts.Add((rig.S.StatusText, rig.S.RetryInSeconds));
        }
        Check("CT-PB-06 countdown reads \"retry in 3s\", \"2s\", \"1s\" on successive ticks",
            texts.SequenceEqual([(Texts.Retry(3), 3), (Texts.Retry(2), 2), (Texts.Retry(1), 1)]));
        var published = rig.Published.Count;
        await rig.Step(1);
        Check("CT-PB-06 the third tick retries (Reconnecting, RetryInSeconds cleared)", rig.Status == PlaybackStatus.Reconnecting && rig.S.RetryInSeconds is null && rig.Published.Count == published + 1);
    }

    /// <summary>Plays A, then fails it three times (3 s, 6 s backoff), leaving the coordinator Failed with a 30 s retry pending.</summary>
    private static async Task ThreeFailures(CoordinatorRig rig)
    {
        rig.Fail();
        await rig.Step(3);
        rig.Fail();
        await rig.Step(6);
        rig.Fail();
    }

    /// <summary>A desired, B fallback: three failures of A, then 30 s later B opens (session 4) and reaches Playing.</summary>
    private static async Task<CoordinatorRig> FallbackPlaying(bool schedule = false)
    {
        var rig = CoordinatorRig.Create(schedule);
        rig.Settings.FallbackStationId = rig.B.Id;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await ThreeFailures(rig);
        await rig.Step(30);
        rig.Playing();
        return rig;
    }

    private static async Task FallbackAfterThreeFailures()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Settings.FallbackStationId = rig.B.Id;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await ThreeFailures(rig);
        Check("CT-PB-07 after failure #3 the retry waits 30 s", rig.S.RetryInSeconds == 30 && rig.StartCount == 3);
        await rig.Step(29);
        Check("CT-PB-07 no fallback start before 30 s", rig.StartCount == 3);
        await rig.Step(1);
        var s = rig.S;
        Check("CT-PB-07 the engine starts the fallback B; Desired stays A; IsFallback; Reconnecting",
            rig.StartCount == 4 && rig.LastStart.Source.DisplayName == "Bravo" && s.DesiredStationId == rig.A.Id && s.CurrentStationId == rig.B.Id
            && s.IsFallback && s.Status == PlaybackStatus.Reconnecting);
        Check("CT-PB-07 texts: \"Connecting to fallback…\" / \"Opening the live stream\"", s.StatusText == Texts.ConnectingFallback && s.TrackText == Texts.Opening);
        Check("CT-PB-07 the switch is logged as playback.fallback", rig.Log.Entries.Any(e => e.EventName == "playback.fallback" && e.Message.Contains("'Bravo'")));
        rig.Playing();
        Check("CT-PB-07 fallback Playing: \"Live · fallback station\", track = fallback tag",
            rig.Status == PlaybackStatus.Playing && rig.S.IsFallback && rig.S.StatusText == Texts.LiveFallback && rig.S.TrackText == "Tag B");
    }

    private static async Task NeverGivesUp()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await ThreeFailures(rig);
        var ok = true;
        for (var failure = 3; failure <= 5; failure++)
        {
            ok &= rig.S.RetryInSeconds == 30 && rig.Status == PlaybackStatus.Failed;
            var before = rig.StartCount;
            await rig.Step(29);
            ok &= rig.StartCount == before;
            await rig.Step(1);
            ok &= rig.StartCount == before + 1 && rig.LastStart.Source.DisplayName == "Alpha" && !rig.S.IsFallback;
            rig.Fail();
        }
        Check("CT-PB-08 without a fallback, failures #3, #4 and #5 each wait 30 s and retry A (no terminal state)", ok && rig.S.RetryInSeconds == 30);
    }

    private static async Task FallbackEqualToDesired()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Settings.FallbackStationId = rig.A.Id;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await ThreeFailures(rig);
        await rig.Step(30);
        Check("CT-PB-09 FallbackStationId == Desired behaves as no fallback: A is retried, IsFallback false",
            rig.StartCount == 4 && rig.LastStart.Source.DisplayName == "Alpha" && !rig.S.IsFallback && rig.S.StatusText == Texts.Connecting);
    }

    private static async Task PrimaryRecheckAndAlternation()
    {
        await using var rig = await FallbackPlaying();
        await rig.Step(119);
        Check("CT-PB-10 fallback B playing: no start during 119 s", rig.StartCount == 4 && rig.Status == PlaybackStatus.Playing && rig.S.IsFallback);
        await rig.Step(1);
        var s = rig.S;
        Check("CT-PB-10 at 120 s the primary A is re-tried: Reconnecting, IsFallback false, Current A",
            rig.StartCount == 5 && rig.LastStart.Source.DisplayName == "Alpha" && s.Status == PlaybackStatus.Reconnecting && !s.IsFallback
            && s.CurrentStationId == rig.A.Id && s.StatusText == Texts.Connecting);
        Check("CT-SM-18 Playing (fallback) + primary re-check → Reconnecting (logged)",
            rig.Log.Entries.Any(e => e.EventName == "playback.fallback" && e.Message.Contains("Re-trying primary 'Alpha'")));
        rig.Fail();
        Check("CT-PB-11 [quirk] the re-checked primary fails: 30 s wait (failures keep counting)", rig.S.RetryInSeconds == 30 && !rig.S.IsFallback);
        await rig.Step(30);
        Check("CT-PB-11 [quirk] after 30 s the fallback B starts again (alternation)",
            rig.StartCount == 6 && rig.LastStart.Source.DisplayName == "Bravo" && rig.S.IsFallback && rig.S.StatusText == Texts.ConnectingFallback);
    }

    private static async Task FailingFallbackReturnsToPrimary()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Settings.FallbackStationId = rig.B.Id;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await ThreeFailures(rig);
        await rig.Step(30);
        rig.Fail();
        Check("CT-PB-12 [quirk] the opening fallback B fails: 30 s wait, still flagged fallback", rig.S.RetryInSeconds == 30 && rig.S.IsFallback);
        await rig.Step(30);
        Check("CT-PB-12 [quirk] after 30 s the primary A starts", rig.StartCount == 5 && rig.LastStart.Source.DisplayName == "Alpha" && !rig.S.IsFallback);
    }

    private static async Task StablePlaybackReset()
    {
        foreach (var (seconds, expected) in new[] { (59, 30), (60, 3) })
        {
            await using var rig = CoordinatorRig.Create();
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Fail();
            await rig.Step(3);
            rig.Fail();
            await rig.Step(6);
            rig.Playing();
            await rig.Step(seconds);
            rig.Fail();
            Check($"CT-PB-13 two failures, then {seconds} s of primary playing, then a failure: retry in {expected}s",
                rig.S.RetryInSeconds == expected && rig.S.StatusText == Texts.Retry(expected));
        }
    }

    private static async Task FallbackDoesNotResetFailures()
    {
        await using var rig = await FallbackPlaying();
        await rig.Step(100);
        rig.Fail();
        Check("CT-PB-14 100 s on the fallback does not reset failures: its failure waits 30 s", rig.S.RetryInSeconds == 30);
    }

    // ─── CT-PB-15..19 ───

    private static async Task StallWatchdog()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            await rig.Step(25);
            Check("CT-PB-15 a start that never plays: no failure after exactly 25 s (strict >)", rig.Status == PlaybackStatus.Connecting);
            await rig.Step(1);
            Check("CT-PB-15 at 26 s it fails as Stalled with a 3 s retry and the engine session stopped",
                rig.Status == PlaybackStatus.Failed && rig.S.RetryInSeconds == 3 && rig.Engine.ActiveSessionId is null
                && rig.Log.Entries.Any(e => e.EventName == "playback.failed" && e.Message.Contains("kind=Stalled")));
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            await rig.Step(10);
            rig.Engine.RaiseState(rig.Session, PlaybackEngineState.Buffering);
            var s = rig.S;
            Check("CT-PB-16 Playing → engine Buffering: status stays Playing/\"Live broadcast\", IsPlaying false",
                s.Status == PlaybackStatus.Playing && !s.IsPlaying && s.StatusText == Texts.Live);
            await rig.Step(20);
            rig.Playing();
            await rig.Step(20);
            Check("CT-PB-16 Buffering 20 s then Playing again: the stall clock resets (no failure)", rig.Status == PlaybackStatus.Playing && rig.S.IsPlaying);
            rig.Engine.RaiseState(rig.Session, PlaybackEngineState.Buffering);
            await rig.Step(25);
            Check("CT-PB-16 25 s outside Playing: no failure yet", rig.Status == PlaybackStatus.Playing);
            await rig.Step(1);
            Check("CT-PB-16 more than 25 s outside Playing (measured from Buffering): Failed/Stalled", rig.Status == PlaybackStatus.Failed && rig.S.RetryInSeconds == 3);
        }
    }

    private static async Task EndOfStreamAndDuplicateFailures()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Playing();
        rig.Engine.RaiseState(rig.Session, PlaybackEngineState.Ended);
        rig.Fail(PlaybackFailureKind.EndOfStream);
        Check("CT-PB-17 Ended + Failed(EndOfStream) is handled exactly like an error: Failed, retry in 3s",
            rig.Status == PlaybackStatus.Failed && rig.S.RetryInSeconds == 3 && rig.S.StatusText == Texts.Retry(3)
            && rig.Log.Entries.Any(e => e.EventName == "playback.failed" && e.Message.Contains("kind=EndOfStream")));
        rig.Fail(PlaybackFailureKind.HttpError);
        Check("CT-PB-18 a second Failed for the same session changes nothing", rig.S.RetryInSeconds == 3 && rig.LogCount("playback.failed") == 1);
        await rig.Step(3);
        rig.Fail();
        Check("CT-PB-18 ...and counted once: the next attempt's failure is #2 (6 s), not #3", rig.S.RetryInSeconds == 6);
    }

    private static async Task StaleSessionEvents()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await rig.Coordinator.PlayAsync(rig.B.Id);
        rig.Engine.RaiseState(1, PlaybackEngineState.Playing);
        rig.Engine.RaiseFailed(1, PlaybackFailureKind.NetworkUnavailable);
        Check("CT-PB-19 Playing and Failed for superseded session 1 are ignored: still Connecting to B",
            rig.Status == PlaybackStatus.Connecting && rig.S.CurrentStationId == rig.B.Id && !rig.S.IsPlaying && rig.LogCount("playback.failed") == 0);
        rig.Engine.RaiseState(2, PlaybackEngineState.Playing);
        rig.Engine.RaiseFailed(1, PlaybackFailureKind.NetworkUnavailable);
        rig.Engine.RaiseState(1, PlaybackEngineState.Buffering);
        Check("CT-PB-19 the snapshot follows session 2; late events for session 1 after it plays are still ignored",
            rig.Status == PlaybackStatus.Playing && rig.S.IsPlaying && rig.S.CurrentStationId == rig.B.Id);
    }

    // ─── CT-PB-20..22: stop cancels automatic work ───

    private static async Task StopCancelsRetryAndFallback()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Fail();
            await rig.Coordinator.StopAsync();
            await rig.Step(40);
            Check("CT-PB-20 Failed with a retry pending + StopAsync: no start in 40 s, Stopped/\"Paused\"",
                rig.StartCount == 1 && rig.Status == PlaybackStatus.Stopped && rig.S.StatusText == Texts.Paused && rig.S.RetryInSeconds is null);
        }
        await using (var rig = await FallbackPlaying())
        {
            await rig.Step(60);
            await rig.Coordinator.StopAsync();
            await rig.Step(200);
            Check("CT-PB-21 fallback playing (primary re-check pending) + StopAsync: no start in 200 s, not flagged fallback",
                rig.StartCount == 4 && rig.Status == PlaybackStatus.Stopped && !rig.S.IsFallback && rig.Engine.ActiveSessionId is null);
        }
        await using (var rig = await FallbackPlaying())
        {
            rig.Fail();
            await rig.Coordinator.StopAsync();
            await rig.Step(200);
            Check("CT-PB-21 failed fallback waiting to return to the primary + StopAsync: no start in 200 s", rig.StartCount == 4);
        }
    }

    private static async Task StopDuringHeldRetryStart()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Fail();
        rig.Engine.HoldStarts = true;
        await rig.Step(3);
        Check("CT-PB-22 setup: the retry start (session 2) is held incomplete", rig.Engine.HeldStartIds.SequenceEqual([2L]) && rig.Status == PlaybackStatus.Reconnecting);
        await rig.Coordinator.StopAsync();
        Check("CT-PB-22 StopAsync cancels the held start's token and stops the engine at once (no waiting for the connect)",
            rig.Engine.HeldStartIds.Count == 0 && rig.Engine.CallLog.SequenceEqual(["start:1", "stop", "start:2", "stop"]));
        var released = rig.Engine.ReleaseStart(2);
        rig.Engine.RaiseState(2, PlaybackEngineState.Playing);
        await rig.Step(40);
        Check("CT-PB-22 a late completion + Playing for the stopped attempt never revives playback",
            !released && rig.Status == PlaybackStatus.Stopped && !rig.S.IsPlaying && !rig.S.IsActive && rig.StartCount == 2 && rig.Engine.ActiveSessionId is null);
    }

    // ─── CT-PB-23..28 ───

    private static async Task Volume()
    {
        await using (var idle = CoordinatorRig.Create())
        {
            await idle.Coordinator.SetVolumeAsync(35);
            Check("CT-PB-23 SetVolumeAsync while idle updates Settings/snapshot but makes no engine call (no session)",
                idle.Settings.Volume == 35 && idle.S.Volume == 35 && idle.Engine.VolumeCalls.Count == 0);
        }
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        Check("CT-PB-23 the start receives the default volume 60 → 0.6", rig.LastStart.Volume == 0.6);
        await rig.Coordinator.SetVolumeAsync(200);
        Check("CT-PB-23 SetVolumeAsync(200) → Settings.Volume 100, engine 1.0", rig.Settings.Volume == 100 && rig.S.Volume == 100 && rig.Engine.VolumeCalls.SequenceEqual([1.0]));
        await rig.Coordinator.SetVolumeAsync(100);
        Check("CT-PB-23 an unchanged volume is not re-sent", rig.Engine.VolumeCalls.Count == 1);
        await rig.Coordinator.SetVolumeAsync(-5);
        Check("CT-PB-23 SetVolumeAsync(-5) → 0 (muted), engine 0.0", rig.Settings.Volume == 0 && rig.S.Volume == 0 && rig.Engine.VolumeCalls.SequenceEqual([1.0, 0.0]));
        await rig.Coordinator.PlayAsync(rig.B.Id);
        Check("CT-PB-23 the next start receives the current volume 0.0", rig.LastStart.Volume == 0.0);
    }

    private static async Task Toggle()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            rig.Settings.LastStationId = rig.B.Id;
            await rig.Coordinator.ToggleAsync();
            Check("CT-PB-24 Toggle when inactive with no desired station plays LastStationId (B)", rig.StartCount == 1 && rig.S.DesiredStationId == rig.B.Id && rig.S.IsActive);
            await rig.Coordinator.ToggleAsync();
            Check("CT-PB-24 Toggle when active is UserStop", !rig.S.IsActive && rig.Status == PlaybackStatus.Stopped && rig.S.StatusText == Texts.Paused);
            rig.Settings.LastStationId = rig.C.Id;
            await rig.Coordinator.ToggleAsync();
            Check("CT-PB-24 Toggle prefers the desired station (B) over LastStationId (C)", rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Bravo");
        }
        await using (var rig = CoordinatorRig.Create())
        {
            rig.Settings.LastStationId = Guid.NewGuid();
            await rig.Coordinator.ToggleAsync();
            Check("CT-PB-24 Toggle with an unknown LastStationId plays the first station", rig.StartCount == 1 && rig.LastStart.Source.DisplayName == "Alpha");
        }
        await using (var rig = CoordinatorRig.Create())
        {
            rig.Settings.Stations.Clear();
            var error = await Record(rig.Coordinator.ToggleAsync());
            var nextError = await Record(rig.Coordinator.NextStationAsync());
            Check("CT-PB-24/25 with no stations Toggle and Next are no-ops without exceptions",
                error is null && nextError is null && rig.StartCount == 0 && rig.Status == PlaybackStatus.Stopped);
        }
    }

    private static async Task NextStation()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.NextStationAsync();
        Check("CT-PB-25 Next with no desired/current station plays the first station A", rig.LastStart.Source.DisplayName == "Alpha" && rig.S.DesiredStationId == rig.A.Id);
        await rig.Coordinator.NextStationAsync();
        Check("CT-PB-25 Next from A plays B", rig.LastStart.Source.DisplayName == "Bravo");
        await rig.Coordinator.PlayAsync(rig.C.Id);
        await rig.Coordinator.NextStationAsync();
        Check("CT-PB-25 Next wraps from C to A", rig.LastStart.Source.DisplayName == "Alpha" && rig.Settings.LastStationId == rig.A.Id);
    }

    private static async Task ForgetStation()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Playing();
        await rig.Coordinator.ForgetStationAsync(Guid.NewGuid());
        Check("CT-PB-26 ForgetStationAsync(unknown id) changes nothing", rig.Status == PlaybackStatus.Playing && rig.Engine.StopCount == 0);
        await rig.Coordinator.ForgetStationAsync(rig.A.Id);
        rig.Settings.Stations.Remove(rig.A);
        Check("CT-PB-26 ForgetStationAsync(desired) stops playback and clears Desired and Current",
            !rig.S.IsActive && rig.S.DesiredStationId is null && rig.S.CurrentStationId is null && rig.Status == PlaybackStatus.Stopped
            && rig.Engine.StopCount == 1 && rig.Engine.ActiveSessionId is null);
        await rig.Step(60);
        Check("CT-PB-26 later ticks never start the forgotten station", rig.StartCount == 1);
    }

    private static async Task ForcedRefreshAndScheduleOff()
    {
        await using (var rig = CoordinatorRig.Create(schedule: true))
        {
            await rig.Coordinator.StartScheduleAsync();
            await rig.Coordinator.StopAsync();
            await rig.Coordinator.RefreshScheduleAsync();
            Check("CT-PB-27 [quirk] schedule on, slot A current, user stopped: RefreshScheduleAsync replays A",
                rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Alpha" && rig.S.IsActive && rig.Status == PlaybackStatus.Connecting);
        }
        await using (var rig = CoordinatorRig.Create(schedule: false))
        {
            await rig.Coordinator.StartScheduleAsync();
            await rig.Step(3 * 3600);
            Check("CT-PB-28 schedule off: StartScheduleAsync plus 3 h of ticks never start the engine",
                rig.StartCount == 0 && rig.Status == PlaybackStatus.Stopped && rig.S.Next is null);
        }
    }

    // ─── CT-PB-29..34: wake ───

    private static async Task WakeSettleAndReconnect()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Fail();
        await rig.Step(3);
        rig.Fail();
        await rig.Step(6);
        rig.Playing();
        await rig.Coordinator.NotifyWakeAsync();
        var s = rig.S;
        Check("CT-PB-29 NotifyWakeAsync while playing: SuspendedBySystem, session stopped, still active, \"Connecting…\"",
            s.Status == PlaybackStatus.SuspendedBySystem && s.IsActive && !s.IsPlaying && rig.Engine.ActiveSessionId is null
            && rig.Engine.CallLog[^1] == "stop" && s.StatusText == Texts.Connecting && s.TrackText == Texts.Opening);
        await rig.Step(1);
        Check("CT-PB-29 1 s after wake: still settling, no start", rig.Status == PlaybackStatus.SuspendedBySystem && rig.StartCount == 3);
        await rig.Step(1);
        Check("CT-PB-29 2 s after wake: exactly one start of A (Reconnecting), logged as wake.recovery",
            rig.StartCount == 4 && rig.LastStart.Source.DisplayName == "Alpha" && rig.Status == PlaybackStatus.Reconnecting
            && rig.Log.Entries.Any(e => e.EventName == "wake.recovery" && e.Message.Contains("outcome=reconnecting")));
        rig.Fail();
        Check("CT-PB-29/34 failures were reset by the wake; the failed reconnect follows the normal policy (retry in 3s)",
            rig.Status == PlaybackStatus.Failed && rig.S.RetryInSeconds == 3);
        await rig.Step(3);
        Check("CT-PB-34 ...and the normal retry reopens A", rig.StartCount == 5 && rig.Status == PlaybackStatus.Reconnecting);
    }

    private static async Task WakeTickGapIsMonotonic()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            await rig.Step(1);
            await rig.Jump(TimeSpan.FromSeconds(14));
            Check("CT-PB-30 a 14 s monotonic gap between ticks is not a wake", rig.Status == PlaybackStatus.Playing && !rig.Log.HasEvent("wake.detected"));
            await rig.Jump(TimeSpan.FromSeconds(15));
            Check("CT-PB-30 a 15 s monotonic gap (>= threshold) triggers the wake path",
                rig.Status == PlaybackStatus.SuspendedBySystem && rig.Log.Entries.Any(e => e.EventName == "wake.detected" && e.Message.Contains("source=tick_gap")));
            await rig.Step(2);
            Check("CT-PB-30 tick-gap wake reconnects once after the 2 s settle", rig.StartCount == 2 && rig.Status == PlaybackStatus.Reconnecting);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            await rig.Step(1);
            await rig.Jump(TimeSpan.FromSeconds(20));
            Check("CT-PB-30 a 20 s monotonic gap triggers the wake path", rig.Status == PlaybackStatus.SuspendedBySystem);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            await rig.Step(1);
            await rig.Jump(TimeSpan.FromSeconds(1), wall: TimeSpan.FromHours(1));
            await rig.Jump(TimeSpan.FromSeconds(1), wall: TimeSpan.FromHours(-3));
            Check("CT-PB-30 UtcNow jumps of +1 h / -3 h with 1 s of monotonic time are NOT wakes",
                rig.Status == PlaybackStatus.Playing && rig.StartCount == 1 && rig.Engine.StopCount == 0 && !rig.Log.HasEvent("wake.detected"));
        }
        await using (var rig = CoordinatorRig.Create(schedule: true))
        {
            await rig.Coordinator.StartScheduleAsync();
            rig.Playing();
            await rig.Step(1);
            await rig.Jump(TimeSpan.FromSeconds(1), wall: TimeSpan.FromHours(1));
            Check("CT-PB-30 ...but the schedule sees the new wall time: slot B (11:00) starts without a wake",
                rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Bravo" && rig.Status == PlaybackStatus.Connecting && !rig.Log.HasEvent("wake.detected"));
        }
    }

    private static async Task WakeKeepsPauseAndOpensNewSlotOnce()
    {
        await using (var rig = CoordinatorRig.Create(schedule: true))
        {
            await rig.Coordinator.StartScheduleAsync();
            rig.Playing();
            await rig.Step(1);
            await rig.Coordinator.StopAsync();
            await rig.Coordinator.NotifyWakeAsync();
            await rig.Step(5);
            await rig.Jump(TimeSpan.FromSeconds(30));
            await rig.Step(5);
            Check("CT-PB-31 paused within slot A: OS wake and a tick-gap wake keep it paused (no start, no engine call)",
                rig.StartCount == 1 && rig.Status == PlaybackStatus.ScheduledWaiting && rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]));
        }
        foreach (var path in new[] { "os", "tick_gap", "os+tick_gap" })
        {
            await using var rig = CoordinatorRig.Create(schedule: true);
            await rig.Coordinator.StartScheduleAsync();
            rig.Playing();
            await rig.Step(1);
            if (path == "tick_gap") await rig.Jump(TimeSpan.FromSeconds(20), wall: TimeSpan.FromMinutes(65));
            else
            {
                rig.SetLocal(new DateTime(2026, 9, 14, 11, 5, 0));
                await rig.Coordinator.NotifyWakeAsync();
            }
            Check($"CT-PB-32 [{path}] playing A, slot B started during sleep: suspended first, nothing opened yet",
                rig.Status == PlaybackStatus.SuspendedBySystem && rig.StartCount == 1);
            // The duplicate tick-gap signal arrives after the settle delay, so its tick also completes the recovery.
            if (path == "os+tick_gap") await rig.Jump(TimeSpan.FromSeconds(20));
            else await rig.Step(2);
            rig.Playing();
            await rig.Step(30);
            Check($"CT-PB-32 [{path}] exactly ONE start after wake, of B (double-open fix), logged as schedule_slot_started",
                rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Bravo" && rig.S.DesiredStationId == rig.B.Id && rig.Status == PlaybackStatus.Playing
                && rig.LogCount("wake.recovery") == 1 && rig.Log.Entries.Any(e => e.EventName == "wake.recovery" && e.Message.Contains("schedule_slot_started")));
        }
        foreach (var path in new[] { "os", "tick_gap", "os+tick_gap" })
        {
            await using var rig = CoordinatorRig.Create(schedule: true);
            await rig.Coordinator.StartScheduleAsync();
            await rig.Coordinator.StopAsync();
            await rig.Step(1);
            if (path.StartsWith("os", StringComparison.Ordinal))
            {
                rig.SetLocal(new DateTime(2026, 9, 14, 11, 5, 0));
                await rig.Coordinator.NotifyWakeAsync();
            }
            if (path.EndsWith("tick_gap", StringComparison.Ordinal)) await rig.Jump(TimeSpan.FromSeconds(20), wall: TimeSpan.FromMinutes(65));
            await rig.Step(10);
            Check($"Wake-into-new-slot [{path}] while paused: slot B opens exactly once",
                rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Bravo" && rig.S.IsActive && rig.LogCount("schedule.fired") == 2);
        }
    }

    private static async Task WakeIdempotentAndDebounced()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            await rig.Step(1);
            await rig.Coordinator.NotifyWakeAsync();
            await rig.Coordinator.NotifyWakeAsync();
            Check("CT-PB-33 / CT-SM-12 a second NotifyWakeAsync while SuspendedBySystem is ignored",
                rig.Status == PlaybackStatus.SuspendedBySystem && rig.Engine.StopCount == 1
                && rig.Log.Entries.Any(e => e.EventName == "wake.detected" && e.Message.Contains("recovery already in progress")));
            await rig.Jump(TimeSpan.FromSeconds(20));
            rig.Playing();
            await rig.Step(30);
            Check("CT-PB-33 OS wake + a 20 s tick gap in the same recovery window: exactly one reconnect",
                rig.StartCount == 2 && rig.LogCount("wake.recovery") == 1 && rig.Status == PlaybackStatus.Playing);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            await rig.Step(1);
            await rig.Jump(TimeSpan.FromSeconds(20));
            await rig.Coordinator.NotifyWakeAsync();
            await rig.Step(2);
            rig.Playing();
            Check("CT-PB-33 tick-gap wake then the OS notification: one reconnect", rig.StartCount == 2 && rig.LogCount("wake.recovery") == 1);
            await rig.Step(5);
            await rig.Coordinator.NotifyWakeAsync();
            await rig.Step(4);
            await rig.Coordinator.NotifyWakeAsync();
            Check("Wake debounce: late wake signals 5 s and 9 s after the completed recovery are rate-limited (no second reconnect)",
                rig.Status == PlaybackStatus.Playing && rig.StartCount == 2 && rig.Engine.StopCount == 1
                && rig.Log.Entries.Count(e => e.EventName == "wake.detected" && e.Message.Contains("rate-limited")) == 2);
            await rig.Step(1);
            await rig.Coordinator.NotifyWakeAsync();
            Check("Wake debounce: a wake 10 s after the recovery is a new wake (SuspendedBySystem)", rig.Status == PlaybackStatus.SuspendedBySystem);
        }
    }

    // ─── CT-PB-35..40 ───

    private static async Task Dispose()
    {
        var rig = CoordinatorRig.Create(schedule: true);
        await rig.Coordinator.StartScheduleAsync();
        rig.Playing();
        var session = rig.Session;
        await rig.Coordinator.DisposeAsync();
        Check("CT-PB-35 DisposeAsync: status Disposing, inactive, engine stopped then disposed once",
            rig.Status == PlaybackStatus.Disposing && !rig.S.IsActive && !rig.S.IsPlaying && rig.Engine.CallLog[^1] == "stop"
            && rig.Engine.DisposeCount == 1 && rig.Log.HasEvent("playback.disposed") && rig.Published[^1].Status == PlaybackStatus.Disposing);
        var calls = rig.Engine.CallLog.Count;
        var published = rig.Published.Count;
        var errors = new List<Exception?>
        {
            await Record(rig.Coordinator.PlayAsync(rig.B.Id)), await Record(rig.Coordinator.ToggleAsync()), await Record(rig.Coordinator.NextStationAsync()),
            await Record(rig.Coordinator.StartScheduleAsync()), await Record(rig.Coordinator.RefreshScheduleAsync()), await Record(rig.Coordinator.NotifyWakeAsync()),
            await Record(rig.Coordinator.NotifySettingsChangedAsync()), await Record(rig.Coordinator.SetVolumeAsync(10)), await Record(rig.Coordinator.StopAsync()),
            await Record(rig.Coordinator.ForgetStationAsync(rig.A.Id))
        };
        rig.Engine.RaiseState(session, PlaybackEngineState.Playing);
        rig.Engine.RaiseFailed(session, PlaybackFailureKind.HttpError);
        rig.SetLocal(new DateTime(2026, 9, 14, 11, 0, 0));
        await rig.Step(10);
        await rig.Jump(TimeSpan.FromSeconds(30));
        await rig.Coordinator.DisposeAsync();
        Check("CT-PB-35 / CT-SM-17 after dispose: commands, ticks, a new slot, a tick gap and engine events never touch the engine",
            errors.All(e => e is null) && rig.Engine.CallLog.Count == calls && rig.StartCount == 1 && rig.Status == PlaybackStatus.Disposing
            && rig.Published.Count == published && rig.Engine.DisposeCount == 1);
    }

    private static async Task SettingsChanged()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            rig.A.Url = "https://a2.example.org/new";
            await rig.Coordinator.NotifySettingsChangedAsync();
            Check("CT-PB-36 the active desired station's URL changed: a new start with the new URL (Connecting)",
                rig.StartCount == 2 && rig.LastStart.Source.Url == new Uri("https://a2.example.org/new") && rig.Status == PlaybackStatus.Connecting);
            rig.Playing();
            rig.C.Url = "https://c2.example.org/other";
            rig.Settings.FallbackStationId = rig.B.Id;
            await rig.Coordinator.NotifySettingsChangedAsync();
            Check("CT-PB-36 a non-desired station's URL or the fallback changed: no immediate start", rig.StartCount == 2 && rig.Status == PlaybackStatus.Playing);
            await ThreeFailures(rig);
            await rig.Step(30);
            Check("CT-PB-36 the new fallback applies to the next due retry", rig.LastStart.Source.DisplayName == "Bravo" && rig.S.IsFallback);
        }
        foreach (var urlChanged in new[] { true, false })
        {
            await using var rig = CoordinatorRig.Create(slots: false);
            await rig.Coordinator.PlayAsync(rig.C.Id);
            rig.Playing();
            rig.Settings.Schedule.Add(new ScheduleEntry { StationId = rig.A.Id, Time = "09:00", Days = [DayOfWeek.Monday] });
            rig.Settings.ScheduleEnabled = true;
            if (urlChanged) rig.C.Url = "https://c2.example.org/edited";
            await rig.Coordinator.NotifySettingsChangedAsync();
            rig.Playing();
            await rig.Step(1);
            Check(urlChanged
                    ? "CT-PB-36 the URL-change restart is a manual play: it holds the current slot, the next tick does not switch to A"
                    : "CT-PB-36 (control) without the URL change the next tick takes the slot and switches to A",
                urlChanged
                    ? rig.StartCount == 2 && rig.S.DesiredStationId == rig.C.Id && rig.LastStart.Source.Url.Host == "c2.example.org"
                    : rig.StartCount == 2 && rig.S.DesiredStationId == rig.A.Id);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            await rig.Coordinator.StopAsync();
            rig.A.Url = "https://a3.example.org/edited";
            await rig.Coordinator.NotifySettingsChangedAsync();
            Check("SettingsChanged: a URL edit of the paused desired station does not start it", rig.StartCount == 1 && !rig.S.IsActive);
            await rig.Coordinator.ToggleAsync();
            Check("SettingsChanged: the next play uses the edited URL", rig.LastStart.Source.Url.Host == "a3.example.org");
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            rig.Settings.Stations.Remove(rig.A);
            await rig.Coordinator.NotifySettingsChangedAsync();
            Check("SettingsChanged: removing the desired station behaves like ForgetStation (stopped, Desired cleared)",
                !rig.S.IsActive && rig.S.DesiredStationId is null && rig.Engine.ActiveSessionId is null && rig.Status == PlaybackStatus.Stopped);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Settings.Volume = 250;
            await rig.Coordinator.NotifySettingsChangedAsync();
            Check("SettingsChanged: an out-of-range volume is clamped (100) and re-sent (1.0)", rig.Settings.Volume == 100 && rig.Engine.VolumeCalls.SequenceEqual([1.0]));
        }
    }

    private static async Task Metadata()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            rig.Engine.SetTitle("Artist – Song");
            Check("CT-PB-37 the metadata title becomes TrackText", rig.S.TrackText == "Artist – Song");
            rig.Engine.SetTitle("   ");
            Check("CT-PB-37 a blank title shows the station tag", rig.S.TrackText == "Tag A");
            rig.Engine.SetTitle("Next – Tune");
            rig.Engine.SetTitle(null);
            Check("CT-PB-37 a null title shows the station tag", rig.S.TrackText == "Tag A");
            rig.Engine.RaiseState(rig.Session, PlaybackEngineState.Buffering);
            rig.Engine.SetTitle("While – Buffering");
            Check("Metadata while not playing does not replace the track line", rig.S.TrackText == "Tag A");
        }
        await using (var rig = CoordinatorRig.Create(plainEngine: true))
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            rig.Engine.SetTitle("Invisible – Title");
            await rig.Step(1);
            Check("CT-PB-37 an engine without ITrackMetadataProvider shows the station tag", rig.S.TrackText == "Tag A" && rig.Status == PlaybackStatus.Playing);
        }
    }

    private static async Task PauseTexts()
    {
        foreach (var schedule in new[] { true, false })
        {
            await using var rig = CoordinatorRig.Create(schedule);
            await rig.Coordinator.PlayAsync(rig.C.Id);
            rig.Playing();
            await rig.Coordinator.StopAsync();
            var s = rig.S;
            Check($"CT-PB-38 StopAsync with the schedule {(schedule ? "on" : "off")}: \"{(schedule ? Texts.PausedSchedule : Texts.Paused)}\" / \"{Texts.PausedTrack}\"",
                s.StatusText == (schedule ? Texts.PausedSchedule : Texts.Paused) && s.TrackText == Texts.PausedTrack
                && s.Status == (schedule ? PlaybackStatus.ScheduledWaiting : PlaybackStatus.Stopped) && s.DesiredStationId == rig.C.Id && !s.IsActive);
            await rig.Coordinator.StopAsync();
            Check($"CT-PB-38 StopAsync is idempotent (schedule {(schedule ? "on" : "off")})", ReferenceEquals(s, rig.S) && rig.Engine.StopCount == 1);
        }
    }

    private static async Task UpcomingAndZone()
    {
        await using (var rig = CoordinatorRig.Create(schedule: true, zone: Zones.Athens))
        {
            await rig.Coordinator.NotifySettingsChangedAsync();
            var expected = Scheduler.Evaluate(rig.Settings, new DateTime(2026, 9, 14, 10, 0, 0)).Next;
            Check("CT-PB-39 Snapshot.Next == Scheduler.Evaluate(settings, localNow in the injected zone).Next",
                rig.S.Next is { } next && next == expected && next.Entry == rig.SlotB && next.At == new DateTime(2026, 9, 14, 11, 0, 0));
            Check("CT-PB-39 Next.At is computer-local wall time (Kind Unspecified, never UTC) and names its station",
                rig.S.Next!.At.Kind == DateTimeKind.Unspecified && rig.S.NextStationName == "Bravo");
            rig.Settings.ScheduleEnabled = false;
            await rig.Coordinator.RefreshScheduleAsync();
            Check("CT-PB-39 Next is null when the schedule is off", rig.S.Next is null && rig.S.NextStationName is null && rig.Status == PlaybackStatus.Stopped);
        }
        await using (var athens = CoordinatorRig.Create(schedule: true, zone: Zones.Athens))
        await using (var utc = CoordinatorRig.Create(schedule: true, zone: Zones.Utc, localNow: new DateTime(2026, 9, 14, 7, 0, 0)))
        {
            await athens.Coordinator.StartScheduleAsync();
            await utc.Coordinator.StartScheduleAsync();
            Check("Zone: the same instant (07:00Z) evaluates in the injected zone: Athens 10:00 → slot A; UTC 07:00 → last week's slot B",
                athens.Clock.UtcNow == utc.Clock.UtcNow && athens.LastStart.Source.DisplayName == "Alpha" && utc.LastStart.Source.DisplayName == "Bravo");
        }
    }

    private static async Task DstWithInjectedZone()
    {
        // Spring gap: Athens 2026-03-29 03:00 EET jumps to 04:00 EEST. A 03:30 Sunday slot fires at the first valid instant.
        await using (var rig = CoordinatorRig.Create(schedule: true, slots: false, zone: Zones.Athens, localNow: new DateTime(2026, 3, 29, 2, 0, 0)))
        {
            rig.Settings.Schedule.Add(new ScheduleEntry { StationId = rig.B.Id, Time = "03:30", Days = [DayOfWeek.Sunday] });
            await rig.Coordinator.StartScheduleAsync();
            await rig.Coordinator.StopAsync();
            rig.SetLocal(new DateTime(2026, 3, 29, 2, 59, 58));
            await rig.Step(1);
            Check("DST spring gap (Athens): nothing new fires at 02:59:59 local", rig.StartCount == 1);
            await rig.Step(1);
            Check("DST spring gap (Athens): the skipped 03:30 slot fires on the first tick after the jump (01:00Z = 04:00 EEST)",
                rig.StartCount == 2 && rig.Clock.UtcNow == new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero) && rig.S.IsActive);
            rig.Playing();
            await rig.Step(60);
            Check("DST spring gap (Athens): it fires once", rig.StartCount == 2);
        }
        // Fall overlap: Athens 2026-10-25 04:00 EEST falls back to 03:00 EET; 03:30 local happens at 00:30Z and again at 01:30Z.
        await using (var rig = CoordinatorRig.Create(schedule: true, slots: false, zone: Zones.Athens, localNow: new DateTime(2026, 10, 25, 2, 0, 0)))
        {
            rig.Settings.Schedule.Add(new ScheduleEntry { StationId = rig.B.Id, Time = "03:30", Days = [DayOfWeek.Sunday] });
            await rig.Coordinator.StartScheduleAsync();
            await rig.Coordinator.StopAsync();
            rig.Clock.UtcNow = new DateTimeOffset(2026, 10, 25, 0, 29, 59, TimeSpan.Zero);
            await rig.Step(1);
            Check("DST fall overlap (Athens): the 03:30 slot fires at the first 03:30 (00:30Z, EEST)", rig.StartCount == 2);
            rig.Playing();
            rig.Clock.UtcNow = new DateTimeOffset(2026, 10, 25, 1, 29, 59, TimeSpan.Zero);
            await rig.Step(1);
            await rig.Step(60);
            Check("DST fall overlap (Athens): the repeated 03:30 (01:30Z, EET) does not fire again (session dedup via threaded localZone)",
                rig.StartCount == 2 && rig.Status == PlaybackStatus.Playing && rig.LogCount("schedule.fired") == 2);
        }
    }

    private static async Task RapidPlays()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Engine.HoldStarts = true;
        var held = new List<IReadOnlyList<long>>();
        var inFlightAtStart = new List<IReadOnlyList<long>>();
        rig.Engine.OnStarted = _ => inFlightAtStart.Add(rig.Engine.HeldStartIds);
        foreach (var station in new[] { rig.A, rig.B, rig.C })
        {
            await rig.Coordinator.PlayAsync(station.Id);
            held.Add(rig.Engine.HeldStartIds);
        }
        Check("CT-PB-40 rapid Play A/B/C with held starts: each new play cancels the previous in-flight start (at most one un-stopped session)",
            held[0].SequenceEqual([1L]) && held[1].SequenceEqual([2L]) && held[2].SequenceEqual([3L]) && rig.Engine.ActiveSessionId == 3);
        Check("CT-PB-40 the previous start's token is cancelled BEFORE the next StartAsync is invoked (the engine never sees two live in-flight starts)",
            inFlightAtStart.Count == 3 && inFlightAtStart.Select((ids, i) => ids.SequenceEqual([(long)i + 1])).All(ok => ok));
        rig.Engine.RaiseState(1, PlaybackEngineState.Playing);
        rig.Engine.RaiseState(2, PlaybackEngineState.Playing);
        Check("CT-PB-40 Playing for sessions 1 and 2 is ignored", rig.Status == PlaybackStatus.Connecting && rig.S.CurrentStationId == rig.C.Id);
        rig.Engine.ReleaseStart(3);
        rig.Engine.RaiseState(3, PlaybackEngineState.Playing);
        Check("CT-PB-40 only C's session is accepted", rig.Status == PlaybackStatus.Playing && rig.S.CurrentStationId == rig.C.Id && rig.S.DesiredStationId == rig.C.Id);
    }

    // ─── CT-LOG (Core part) ───

    // HZ-07: every secret is a sentinel that cannot occur in a filesystem path, so the check scans the full entry text
    // (event, message, exception with its stack trace) and still passes when the checkout lives under /private/tmp or
    // /private/var/folders, where stack-trace source paths contain "/private".
    private static readonly string[] LogSentinels =
    [
        "ds-user-q7", "ds-pass-q7", "ds-relay-q7", "ds-pwd-q7", "ds-other-q7", "ds-s3cr3t-q7", "ds-ftpuser-q7", "ds-ftppass-q7",
        "ds-path-alpha-q7", "alpha-q7.mp3", "ds-path-bravo-q7", "bravo-q7.aac", "ds-path-charlie-q7", "ds-path-moved-q7", "charlie2-q7.mp3", "ds-path-ftp-q7",
        "token=", "ALPHA-TOKEN", "key=", "BRAVO-KEY", "sig=", "CHARLIE-SIG", "C2-TOKEN", "ds-frag-q7", "://"
    ];

    private static async Task LogRedaction([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        string[] urls =
        [
            "https://ds-user-q7:ds-pass-q7@secret-a.example.org:8443/ds-path-alpha-q7/alpha-q7.mp3?token=ALPHA-TOKEN#ds-frag-q7",
            "http://ds-relay-q7:ds-pwd-q7@secret-b.example.org/ds-path-bravo-q7/bravo-q7.aac?key=BRAVO-KEY",
            "https://secret-c.example.org/ds-path-charlie-q7/stream?sig=CHARLIE-SIG"
        ];
        await using var rig = CoordinatorRig.Create(schedule: true, urls: urls);
        rig.Settings.FallbackStationId = rig.B.Id;
        await rig.Coordinator.StartScheduleAsync();
        rig.Playing();
        await ThreeFailures(rig);
        await rig.Step(30);
        rig.Playing();
        rig.Engine.StartException = new InvalidOperationException("native start failed");
        await rig.Step(120);
        rig.Engine.StartException = null;
        await rig.Coordinator.PlayAsync(rig.C.Id);
        await rig.Coordinator.NotifyWakeAsync();
        await rig.Step(3);
        rig.C.Url = "https://ds-other-q7:ds-s3cr3t-q7@secret-c2.example.org/ds-path-moved-q7/charlie2-q7.mp3?token=C2-TOKEN";
        await rig.Coordinator.NotifySettingsChangedAsync();
        rig.Settings.Stations.Add(new Station { Name = "Broken", Url = "ftp://ds-ftpuser-q7:ds-ftppass-q7@files.example.org/ds-path-ftp-q7/x.mp3" });
        await rig.Coordinator.PlayAsync(rig.Settings.Stations[3].Id);
        await rig.Coordinator.NextStationAsync();
        await rig.Coordinator.SetVolumeAsync(20);
        await rig.Coordinator.StopAsync();
        rig.SetLocal(new DateTime(2026, 9, 14, 11, 0, 0));
        await rig.Step(1);
        await rig.Coordinator.DisposeAsync();
        string[] required = ["schedule.fired", "playback.state", "playback.failed", "playback.fallback", "playback.engine_error", "wake.detected", "wake.recovery", "playback.invalid_url", "playback.disposed"];
        Check("CT-LOG (core) setup: the scenario produced every coordinator log event", required.All(rig.Log.HasEvent));
        Check("CT-LOG (core) HZ-07 the secret sentinels cannot occur in this checkout's paths (source, binaries, temp)",
            LogSentinels.All(s => !sourceFile.Contains(s, StringComparison.OrdinalIgnoreCase) && !AppContext.BaseDirectory.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !Path.GetTempPath().Contains(s, StringComparison.OrdinalIgnoreCase)));
        Check("CT-LOG (core) HZ-07 the engine_error entry carries a stack trace, so full-text scanning covers exception text",
            rig.Log.Entries.Any(e => e.EventName == "playback.engine_error" && e.Exception?.StackTrace != null));
        Check("CT-LOG (core) no coordinator log entry contains URL user-info, path, query, fragment or scheme://",
            rig.Log.NoEntryContains(LogSentinels));
    }

    // ─── Adversarial (verification wave for the core lane) ───

    private static async Task Adversarial()
    {
        await StopWhileConnecting();
        await LateEventsForOlderSessions();
        await VolumeDuringConnect();
        await ForgetFallbackWhilePlaying();
        await ScheduleDisabledMidRetry();
        await UrlChangedWhileFailed();
        await DisposeDuringHeldStart();
        await EngineExceptions();
        await EventsInsideStartAsync();
        await SnapshotHandlers();
        await WakeDuringHeldStart();
        await InvalidUrls();
        await MiscInputs();
    }

    private static async Task StopWhileConnecting()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Engine.HoldStarts = true;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await rig.Coordinator.StopAsync();
        Check("ADV-01 stop-while-connecting: the Stop reaches the engine while the start is still held, and the start is cancelled",
            rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]) && rig.Engine.HeldStartIds.Count == 0 && rig.Status == PlaybackStatus.Stopped);
        rig.Engine.RaiseState(1, PlaybackEngineState.Playing);
        rig.Fail();
        await rig.Step(60);
        Check("ADV-01 Playing/Failed for the cancelled attempt are ignored and nothing restarts",
            rig.Status == PlaybackStatus.Stopped && rig.StartCount == 1 && rig.LogCount("playback.failed") == 0 && !rig.S.IsPlaying);
    }

    private static async Task LateEventsForOlderSessions()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Fail();
            rig.Engine.RaiseState(1, PlaybackEngineState.Playing);
            Check("ADV-03 Failed then Playing for the same session: stays Failed, not playing", rig.Status == PlaybackStatus.Failed && !rig.S.IsPlaying && rig.S.RetryInSeconds == 3);
            await rig.Step(3);
            rig.Playing();
            rig.Engine.RaiseFailed(1, PlaybackFailureKind.HttpError);
            rig.Engine.RaiseState(1, PlaybackEngineState.Buffering);
            Check("ADV-02 failure/buffering for session N-1 after session N plays: ignored", rig.Status == PlaybackStatus.Playing && rig.S.IsPlaying);
            rig.Engine.RaiseState(99, PlaybackEngineState.Playing);
            rig.Engine.RaiseFailed(99, PlaybackFailureKind.HttpError);
            rig.Engine.RaiseFailed(0, PlaybackFailureKind.HttpError);
            Check("ADV-02 events for unknown future / zero session ids are ignored", rig.Status == PlaybackStatus.Playing && rig.LogCount("playback.failed") == 1);
        }
    }

    private static async Task VolumeDuringConnect()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Engine.HoldStarts = true;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await rig.Coordinator.SetVolumeAsync(30);
        Check("ADV-04 volume during a held connect reaches the engine immediately", rig.Engine.VolumeCalls.SequenceEqual([0.3]) && rig.Engine.HeldStartIds.SequenceEqual([1L]));
        rig.Engine.ReleaseStart(1);
        rig.Playing();
        rig.Fail();
        await rig.Coordinator.SetVolumeAsync(80);
        Check("ADV-04 volume while Failed (no live session) is not sent", rig.Engine.VolumeCalls.Count == 1 && rig.S.Volume == 80);
        rig.Engine.HoldStarts = false;
        await rig.Step(3);
        Check("ADV-04 the retry start carries the volume chosen while Failed", rig.LastStart.Volume == 0.8);
    }

    private static async Task ForgetFallbackWhilePlaying()
    {
        await using var rig = await FallbackPlaying();
        await rig.Coordinator.ForgetStationAsync(rig.B.Id);
        rig.Settings.Stations.Remove(rig.B);
        await rig.Coordinator.NotifySettingsChangedAsync();
        await rig.Step(200);
        Check("ADV-05 ForgetStation of the fallback while it plays: stopped, Current cleared, Desired A kept, nothing restarts",
            !rig.S.IsActive && rig.S.CurrentStationId is null && rig.S.DesiredStationId == rig.A.Id && rig.StartCount == 4 && rig.Engine.ActiveSessionId is null);
    }

    private static async Task ScheduleDisabledMidRetry()
    {
        await using var rig = CoordinatorRig.Create(schedule: true);
        await rig.Coordinator.StartScheduleAsync();
        rig.Fail();
        rig.Settings.ScheduleEnabled = false;
        await rig.Coordinator.RefreshScheduleAsync();
        Check("ADV-06 schedule disabled mid-retry: no schedule start, still Failed", rig.StartCount == 1 && rig.Status == PlaybackStatus.Failed && rig.S.Next is null);
        await rig.Step(3);
        Check("ADV-06 the retry still runs", rig.StartCount == 2 && rig.Status == PlaybackStatus.Reconnecting);
        rig.Playing();
        rig.SetLocal(new DateTime(2026, 9, 14, 11, 0, 0));
        await rig.Step(5);
        Check("ADV-06 with the schedule off slot B never starts", rig.StartCount == 2 && rig.S.DesiredStationId == rig.A.Id);
    }

    private static async Task UrlChangedWhileFailed()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(rig.A.Id);
        rig.Fail();
        await rig.Step(3);
        rig.Fail();
        rig.A.Url = "https://a-fixed.example.org/live";
        await rig.Coordinator.NotifySettingsChangedAsync();
        Check("ADV-07 URL changed while Failed: immediate start with the new URL (like a manual play)",
            rig.StartCount == 3 && rig.LastStart.Source.Url.Host == "a-fixed.example.org" && rig.Status == PlaybackStatus.Connecting && rig.S.RetryInSeconds is null);
        rig.Fail();
        Check("ADV-07 failures were reset by the manual play: the next failure waits 3 s", rig.S.RetryInSeconds == 3);
    }

    private static async Task DisposeDuringHeldStart()
    {
        var rig = CoordinatorRig.Create();
        rig.Engine.HoldStarts = true;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        var finished = await Wait.Finishes(rig.Coordinator.DisposeAsync().AsTask(), Timeout);
        Check("ADV-08 dispose during a held start completes (no deadlock), cancels the start, stops and disposes the engine",
            finished && rig.Engine.HeldStartIds.Count == 0 && rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]) && rig.Engine.DisposeCount == 1
            && rig.Status == PlaybackStatus.Disposing);
        var released = rig.Engine.ReleaseStart(1);
        rig.Engine.RaiseState(1, PlaybackEngineState.Playing);
        Check("ADV-08 the held start cannot complete afterwards and its events are ignored", !released && rig.Status == PlaybackStatus.Disposing);

        var twice = CoordinatorRig.Create();
        await twice.Coordinator.PlayAsync(twice.A.Id);
        var both = Task.WhenAll(Task.Run(async () => await twice.Coordinator.DisposeAsync()), Task.Run(async () => await twice.Coordinator.DisposeAsync()));
        Check("ADV-08 concurrent DisposeAsync calls both finish and dispose the engine once", await Wait.Finishes(both, Timeout) && twice.Engine.DisposeCount == 1);
    }

    private static async Task EngineExceptions()
    {
        foreach (var synchronous in new[] { false, true })
        {
            await using var rig = CoordinatorRig.Create();
            rig.Engine.ThrowSynchronously = synchronous;
            rig.Engine.StartException = new InvalidOperationException("native start failed");
            var error = await Record(rig.Coordinator.PlayAsync(rig.A.Id));
            var label = synchronous ? "thrown synchronously" : "faulted task";
            Check($"ADV-09 StartAsync {label}: PlayAsync does not throw; the attempt fails (Unknown) with the normal 3 s retry",
                error is null && rig.Status == PlaybackStatus.Failed && rig.S.RetryInSeconds == 3
                && rig.Log.Entries.Any(e => e.EventName == "playback.engine_error" && e.Level == AppLogLevel.Error)
                && rig.Log.Entries.Any(e => e.EventName == "playback.failed" && e.Message.Contains("kind=Unknown")));
            Check($"ADV-09 StartAsync {label}: the failed session is stopped after the start", rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]));
            rig.Engine.StartException = null;
            await rig.Step(3);
            rig.Playing();
            Check($"ADV-09 StartAsync {label}: the retry recovers", rig.Status == PlaybackStatus.Playing && rig.StartCount == 2);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            rig.Engine.HoldStarts = true;
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Engine.FailStart(1, new IOException("adapter bug"));
            Check("ADV-09 a held start that later faults (off the caller's thread) fails the attempt",
                await Wait.Until(() => rig.Status == PlaybackStatus.Failed) && rig.S.RetryInSeconds == 3);
        }
        foreach (var synchronous in new[] { false, true })
        {
            await using var rig = CoordinatorRig.Create();
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            rig.Engine.ThrowSynchronously = synchronous;
            rig.Engine.StopException = new IOException("native stop failed");
            var error = await Record(rig.Coordinator.StopAsync());
            Check($"ADV-09 StopAsync {(synchronous ? "thrown synchronously" : "faulted")}: the coordinator still stops and logs a warning",
                error is null && rig.Status == PlaybackStatus.Stopped && rig.Log.Entries.Any(e => e.EventName == "playback.engine_error" && e.Level == AppLogLevel.Warn));
            await rig.Coordinator.PlayAsync(rig.B.Id);
            Check("ADV-09 ...and a later play still works", rig.StartCount == 2 && rig.Status == PlaybackStatus.Connecting);
            var disposed = await Wait.Finishes(rig.Coordinator.DisposeAsync().AsTask(), Timeout);
            Check("ADV-09 dispose with a throwing StopAsync completes and still disposes the engine", disposed && rig.Engine.DisposeCount == 1);
        }
    }

    private static async Task EventsInsideStartAsync()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            rig.Engine.OnStarted = id => rig.Engine.RaiseState(id, PlaybackEngineState.Playing);
            await rig.Coordinator.PlayAsync(rig.A.Id);
            Check("ADV-10 Playing raised synchronously inside StartAsync is accepted", rig.Status == PlaybackStatus.Playing && rig.S.IsPlaying);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            rig.Engine.OnStarted = id => rig.Engine.RaiseFailed(id, PlaybackFailureKind.HttpError);
            await rig.Coordinator.PlayAsync(rig.A.Id);
            Check("ADV-10 Failed raised synchronously inside StartAsync: Failed, and the Stop is issued after that start",
                rig.Status == PlaybackStatus.Failed && rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]) && rig.Engine.ActiveSessionId is null);
        }
    }

    private static async Task SnapshotHandlers()
    {
        await using (var rig = CoordinatorRig.Create())
        {
            rig.Coordinator.SnapshotChanged += (_, _) => throw new InvalidOperationException("handler bug");
            var error = await Record(rig.Coordinator.PlayAsync(rig.A.Id));
            rig.Playing();
            Check("ADV-11 a throwing SnapshotChanged handler is logged and does not break the coordinator",
                error is null && rig.Status == PlaybackStatus.Playing && rig.Log.HasEvent("playback.snapshot_handler_failed"));
        }
        await using (var rig = CoordinatorRig.Create())
        {
            var reentered = 0;
            rig.Coordinator.SnapshotChanged += (_, s) =>
            {
                if (s.Status == PlaybackStatus.Playing && Interlocked.Exchange(ref reentered, 1) == 0) _ = rig.Coordinator.StopAsync();
            };
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Playing();
            Check("ADV-12 a handler that re-enters the coordinator (Stop on Playing) does not deadlock",
                await Wait.Until(() => rig.Status == PlaybackStatus.Stopped, Timeout) && rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]));
        }
    }

    private static async Task WakeDuringHeldStart()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Engine.HoldStarts = true;
        await rig.Coordinator.PlayAsync(rig.A.Id);
        await rig.Coordinator.NotifyWakeAsync();
        Check("ADV-13 wake during a held connect cancels the start and stops the engine", rig.Engine.HeldStartIds.Count == 0 && rig.Engine.CallLog.SequenceEqual(["start:1", "stop"]));
        await rig.Step(2);
        Check("ADV-13 the recovery opens exactly one new attempt", rig.Engine.HeldStartIds.SequenceEqual([2L]) && rig.Status == PlaybackStatus.Reconnecting);
        rig.Engine.RaiseState(1, PlaybackEngineState.Playing);
        Check("ADV-13 Playing for the pre-wake attempt is ignored", rig.Status == PlaybackStatus.Reconnecting);
    }

    private static async Task InvalidUrls()
    {
        await using var rig = CoordinatorRig.Create();
        rig.Settings.FallbackStationId = rig.B.Id;
        rig.A.Url = "not a url";
        await rig.Coordinator.PlayAsync(rig.A.Id);
        Check("ADV-16 an invalid station URL never reaches the engine: Failed (InvalidUrl), retry in 3s",
            rig.StartCount == 0 && rig.Status == PlaybackStatus.Failed && rig.S.RetryInSeconds == 3 && rig.Log.HasEvent("playback.invalid_url"));
        await rig.Step(3);
        await rig.Step(6);
        await rig.Step(30);
        Check("ADV-16 after three invalid-URL failures the fallback B plays", rig.StartCount == 1 && rig.LastStart.Source.DisplayName == "Bravo" && rig.S.IsFallback);
        rig.Playing();
        rig.B.Url = "mailto:nobody@example.org";
        rig.A.Url = "https://a.example.org/live";
        rig.Settings.FallbackStationId = null;
        await rig.Coordinator.PlayAsync(rig.B.Id);
        Check("ADV-17 replacing a live session with an invalid-URL attempt stops the live engine session",
            rig.Status == PlaybackStatus.Failed && rig.Engine.CallLog[^1] == "stop" && rig.Engine.ActiveSessionId is null);
    }

    private static async Task MiscInputs()
    {
        await using var rig = CoordinatorRig.Create();
        await rig.Coordinator.PlayAsync(Guid.NewGuid());
        Check("ADV-19 PlayAsync(unknown id) is ignored", rig.StartCount == 0 && rig.Status == PlaybackStatus.Stopped && rig.Settings.LastStationId is null);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var error = await Record(rig.Coordinator.OnTickAsync(cancelled.Token));
        Check("ADV-15 OnTickAsync with a cancelled token throws OperationCanceledException and changes nothing",
            error is OperationCanceledException && rig.Status == PlaybackStatus.Stopped);

        await using var fb = await FallbackPlaying();
        await fb.Coordinator.NotifyWakeAsync();
        await fb.Step(2);
        Check("ADV-20 wake while the fallback plays: failures and fallback reset, the recovery reopens the primary A",
            fb.LastStart.Source.DisplayName == "Alpha" && !fb.S.IsFallback && fb.Status == PlaybackStatus.Reconnecting);
        fb.Fail();
        Check("ADV-20 ...and its failure is #1 (3 s)", fb.S.RetryInSeconds == 3);

        await using var flap = await FallbackPlaying();
        await flap.Step(60);
        flap.Engine.RaiseState(flap.Session, PlaybackEngineState.Buffering);
        await flap.Step(10);
        flap.Playing();
        await flap.Step(49);
        Check("ADV-22 Buffering/Playing flaps on the fallback do not re-arm the primary re-check (119 s: no start)", flap.StartCount == 4);
        await flap.Step(1);
        Check("ADV-22 the primary re-check fires 120 s after the fallback first played", flap.StartCount == 5 && flap.LastStart.Source.DisplayName == "Alpha");
    }

    private static async Task<Exception?> Record(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
