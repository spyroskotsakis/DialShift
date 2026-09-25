using System.Diagnostics;
using System.Runtime.CompilerServices;
using DialShift.Core;
using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>
/// Acceptance-matrix §7.6: the §5.2 input × state table (every cell, table-driven), CT-SM-01..20 as named checks,
/// the seeded property test CT-SM-21 (<see cref="PropertyAsync"/>) and the concurrency races CT-SM-22 (<see cref="RacesAsync"/>).
/// </summary>
public static class PlaybackStateMachineTests
{
    private static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(10);

    public enum State { Stopped, ScheduledWaiting, Connecting, Playing, Reconnecting, Failed, Suspended, Disposing }

    private enum Input { UserPlay, UserStop, ScheduleDue, EnginePlaying, EngineBuffering, EngineFailed, StaleEvents, WakeDetected, SettingsChanged, Dispose }

    /// <summary>Expected outcome. <c>To == null</c> means "ign": the snapshot instance is unchanged and the engine sees no call.</summary>
    private sealed record Cell(PlaybackStatus? To, int Starts = 0, int Stops = 0, string? Id = null);

    public static async Task RunAsync()
    {
        await Table();
        await InitialAndIdleMapping();
        await RetryAndWakeTransitions();
        await SuspendedUserInputs();
        await SecondFailureCountsOnce();
        await SnapshotsAreImmutable();
        await NoOpTicksDoNotPublish();
    }

    // ─── §5.2 table ───

    /// <summary>
    /// Puts a fresh rig into <paramref name="state"/>. Stopped uses the schedule OFF; every other state uses the schedule ON
    /// with a manual hold on slot A (PlayAsync(C)), so ticks never override it. Session ids: Connecting/Playing = 1,
    /// Reconnecting = 2 (after one failure), Failed = 1 (retired), Suspended = 1 (retired).
    /// </summary>
    public static async Task<CoordinatorRig> Enter(State state)
    {
        var rig = CoordinatorRig.Create(schedule: state != State.Stopped);
        switch (state)
        {
            case State.Stopped:
                break;
            case State.ScheduledWaiting:
                await rig.Coordinator.StopAsync();
                break;
            case State.Disposing:
                await rig.Coordinator.PlayAsync(rig.C.Id);
                await rig.Coordinator.DisposeAsync();
                break;
            default:
                await rig.Coordinator.PlayAsync(rig.C.Id);
                if (state is State.Playing or State.Suspended) rig.Playing();
                if (state is State.Failed or State.Reconnecting) rig.Fail();
                if (state == State.Reconnecting) await rig.Step(3);
                if (state == State.Suspended) await rig.Coordinator.NotifyWakeAsync();
                break;
        }
        var expected = state switch
        {
            State.Stopped => PlaybackStatus.Stopped,
            State.ScheduledWaiting => PlaybackStatus.ScheduledWaiting,
            State.Connecting => PlaybackStatus.Connecting,
            State.Playing => PlaybackStatus.Playing,
            State.Reconnecting => PlaybackStatus.Reconnecting,
            State.Failed => PlaybackStatus.Failed,
            State.Suspended => PlaybackStatus.SuspendedBySystem,
            _ => PlaybackStatus.Disposing
        };
        if (rig.Status != expected) throw new InvalidOperationException($"Enter({state}) reached {rig.Status}.");
        return rig;
    }

    private static Task Apply(CoordinatorRig rig, Input input)
    {
        switch (input)
        {
            case Input.UserPlay: return rig.Coordinator.PlayAsync(rig.A.Id);
            case Input.UserStop: return rig.Coordinator.StopAsync();
            case Input.ScheduleDue:
                rig.Settings.ScheduleEnabled = true; // Stopped runs with the schedule off; "Follow schedule" + refresh is its ScheduleDue.
                return rig.Coordinator.RefreshScheduleAsync();
            case Input.EnginePlaying: rig.Engine.RaiseState(rig.Session, PlaybackEngineState.Playing); return Task.CompletedTask;
            case Input.EngineBuffering: rig.Engine.RaiseState(rig.Session, PlaybackEngineState.Buffering); return Task.CompletedTask;
            case Input.EngineFailed: rig.Engine.RaiseFailed(rig.Session, PlaybackFailureKind.NetworkUnavailable); return Task.CompletedTask;
            case Input.StaleEvents:
                foreach (var id in new[] { rig.Session - 1, rig.Session + 7 })
                {
                    rig.Engine.RaiseState(id, PlaybackEngineState.Playing);
                    rig.Engine.RaiseState(id, PlaybackEngineState.Buffering);
                    rig.Engine.RaiseFailed(id, PlaybackFailureKind.HttpError);
                }
                return Task.CompletedTask;
            case Input.WakeDetected: return rig.Coordinator.NotifyWakeAsync();
            case Input.SettingsChanged: return rig.Coordinator.NotifySettingsChangedAsync();
            default: return rig.Coordinator.DisposeAsync().AsTask();
        }
    }

    private static Cell Expected(State state, Input input)
    {
        var ign = new Cell(null);
        var idle = state == State.Stopped ? PlaybackStatus.Stopped : PlaybackStatus.ScheduledWaiting;
        var live = state is State.Connecting or State.Playing or State.Reconnecting; // an engine session is open
        var attempt = live || state == State.Failed || state == State.Suspended; // play intent
        if (state == State.Disposing) return ign with { Id = "CT-SM-17" };
        return input switch
        {
            Input.UserPlay => new Cell(PlaybackStatus.Connecting, Starts: 1, Id: state == State.Stopped ? "CT-SM-02" : state == State.Suspended ? "CT-SM-15" : null),
            Input.UserStop => attempt ? new Cell(idle, Stops: live ? 1 : 0, Id: state == State.Suspended ? "CT-SM-14" : "CT-SM-08") : ign,
            Input.ScheduleDue => state == State.Suspended ? ign : new Cell(PlaybackStatus.Connecting, Starts: 1, Id: state == State.ScheduledWaiting ? "CT-SM-16" : null),
            Input.EnginePlaying => state is State.Connecting or State.Reconnecting
                ? new Cell(PlaybackStatus.Playing, Id: state == State.Connecting ? "CT-SM-03" : "CT-SM-06")
                : ign,
            Input.EngineBuffering => state == State.Playing ? new Cell(PlaybackStatus.Playing) : ign,
            Input.EngineFailed => live
                ? new Cell(PlaybackStatus.Failed, Stops: 1, Id: state == State.Connecting ? "CT-SM-04" : state == State.Playing ? "CT-SM-07" : null)
                : ign with { Id = state == State.Stopped ? "CT-SM-09" : state == State.Failed ? "CT-SM-10" : state == State.Suspended ? "CT-SM-13" : null },
            Input.StaleEvents => ign with { Id = state == State.Stopped ? "CT-SM-09" : null },
            Input.WakeDetected => attempt && state != State.Suspended
                ? new Cell(PlaybackStatus.SuspendedBySystem, Stops: live ? 1 : 0, Id: "CT-SM-11")
                : ign with { Id = state == State.Suspended ? "CT-SM-12" : null },
            Input.SettingsChanged => ign,
            _ => new Cell(PlaybackStatus.Disposing, Stops: 1, Id: "CT-SM-17")
        };
    }

    private static async Task Table()
    {
        foreach (var state in Enum.GetValues<State>())
        foreach (var input in Enum.GetValues<Input>())
        {
            await using var rig = await Enter(state);
            var before = rig.S;
            var calls = rig.Engine.CallLog.Count;
            var starts = rig.StartCount;
            var stops = rig.Engine.StopCount;
            await Apply(rig, input);
            var cell = Expected(state, input);
            var name = $"{(cell.Id is null ? "" : cell.Id + " ")}§5.2 {state} + {input} → {(cell.To is null ? "ign" : cell.To.ToString())}";
            if (cell.To is null)
            {
                Check(name + " (same snapshot instance, no engine call)", ReferenceEquals(before, rig.S) && rig.Engine.CallLog.Count == calls);
                continue;
            }
            Check(name + $" (+{cell.Starts} start, +{cell.Stops} stop)",
                rig.Status == cell.To && rig.StartCount == starts + cell.Starts && rig.Engine.StopCount == stops + cell.Stops);
            if (input == Input.Dispose) Check(name + ": engine disposed once", rig.Engine.DisposeCount == 1);
            if (input == Input.EngineBuffering) Check(name + ": IsPlaying false, status text kept", !rig.S.IsPlaying && rig.S.StatusText == Texts.Live);
            if (input == Input.ScheduleDue) Check(name + ": the current slot's station A opens", rig.LastStart.Source.DisplayName == "Alpha" && rig.S.DesiredStationId == rig.A.Id);
            if (input == Input.EngineFailed)
            {
                var wait = state == State.Reconnecting ? 6 : 3; // Reconnecting is the second attempt
                Check(name + $": retry in {wait}s", rig.S.RetryInSeconds == wait);
            }
        }

        // "deferred to recovery" (§5.2 ²): ScheduleDue while SuspendedBySystem is folded into the recovery → one reconnect, to the slot.
        await using (var rig = await Enter(State.Suspended))
        {
            await rig.Coordinator.RefreshScheduleAsync();
            await rig.Step(2);
            await rig.Step(10);
            Check("§5.2 SuspendedBySystem + ScheduleDue (forced) → deferred: the settle opens the forced slot A exactly once",
                rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Alpha" && rig.Status == PlaybackStatus.Connecting);
        }
    }

    // ─── CT-SM-01, 05, 11, 14, 15, 10, 19, 20 ───

    private static async Task InitialAndIdleMapping()
    {
        await using (var rig = CoordinatorRig.Create(schedule: true, slots: false))
        {
            await rig.Coordinator.StartScheduleAsync();
            Check("CT-SM-01 initially Stopped; StartScheduleAsync with the schedule on but no slots stays Stopped",
                rig.Status == PlaybackStatus.Stopped && rig.StartCount == 0 && rig.S.Next is null);
        }
        await using (var rig = CoordinatorRig.Create(schedule: true))
        {
            Check("CT-SM-01 initial status is Stopped", rig.Status == PlaybackStatus.Stopped);
            await rig.Coordinator.NotifySettingsChangedAsync();
            Check("CT-SM-01 schedule on with a next slot and no play intent → ScheduledWaiting (revalidate, no start)",
                rig.Status == PlaybackStatus.ScheduledWaiting && rig.StartCount == 0 && rig.S.Next?.Entry == rig.SlotB);
            // With weekly slots and the ±7-day evaluation a current occurrence always exists, so StartScheduleAsync catches it up.
            await rig.Coordinator.StartScheduleAsync();
            Check("CT-SM-01 [note] StartScheduleAsync with weekly slots always has a current occurrence and catches it up (Connecting A)",
                rig.Status == PlaybackStatus.Connecting && rig.LastStart.Source.DisplayName == "Alpha");
        }
    }

    private static async Task RetryAndWakeTransitions()
    {
        await using (var rig = await Enter(State.Failed))
        {
            await rig.Step(2);
            Check("CT-SM-05 Failed before the retry is due stays Failed", rig.Status == PlaybackStatus.Failed && rig.StartCount == 1);
            await rig.Step(1);
            Check("CT-SM-05 Failed + RetryDue → Reconnecting (one start of the desired station)", rig.Status == PlaybackStatus.Reconnecting && rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Charlie");
        }
        foreach (var state in new[] { State.Connecting, State.Playing, State.Reconnecting, State.Failed })
        {
            await using var rig = await Enter(state);
            var starts = rig.StartCount;
            await rig.Coordinator.NotifyWakeAsync();
            var suspended = rig.Status == PlaybackStatus.SuspendedBySystem;
            await rig.Step(2);
            Check($"CT-SM-11 {state} + Wake → SuspendedBySystem → (settle 2 s) → Reconnecting, one start",
                suspended && rig.Status == PlaybackStatus.Reconnecting && rig.StartCount == starts + 1 && rig.LastStart.Source.DisplayName == "Charlie");
        }
    }

    private static async Task SuspendedUserInputs()
    {
        await using (var rig = await Enter(State.Suspended))
        {
            await rig.Coordinator.StopAsync();
            await rig.Step(30);
            Check("CT-SM-14 SuspendedBySystem + UserStop → ScheduledWaiting and the recovery never reconnects",
                rig.Status == PlaybackStatus.ScheduledWaiting && rig.StartCount == 1 && !rig.Log.HasEvent("wake.recovery"));
        }
        await using (var rig = await Enter(State.Suspended))
        {
            await rig.Coordinator.PlayAsync(rig.B.Id);
            rig.Playing();
            await rig.Step(30);
            Check("CT-SM-15 SuspendedBySystem + UserPlay(B) → Connecting B; the recovery is superseded (no second start)",
                rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Bravo" && rig.Status == PlaybackStatus.Playing && !rig.Log.HasEvent("wake.recovery"));
        }
    }

    private static async Task SecondFailureCountsOnce()
    {
        await using var rig = await Enter(State.Failed);
        rig.Engine.RaiseFailed(rig.Session, PlaybackFailureKind.HttpError);
        await rig.Step(3);
        rig.Fail();
        Check("CT-SM-10 Failed + second error for the same session: failures incremented once (next wait is 6 s)", rig.S.RetryInSeconds == 6);
    }

    private static async Task SnapshotsAreImmutable()
    {
        var properties = typeof(PlaybackSnapshot).GetProperties();
        Check("CT-SM-19 PlaybackSnapshot has only get/init properties (immutable record)",
            properties.Length > 0 && properties.All(p => p.SetMethod is null
                || p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit))));
        await using var rig = CoordinatorRig.Create(schedule: true);
        var initial = rig.S;
        var copy = initial with { };
        await rig.Coordinator.StartScheduleAsync();
        var connecting = rig.S;
        var connectingCopy = connecting with { };
        rig.Playing();
        rig.Fail();
        await rig.Step(5);
        Check("CT-SM-19 snapshots received earlier never change; each transition publishes a new instance",
            initial == copy && connecting == connectingCopy && !ReferenceEquals(initial, connecting) && !ReferenceEquals(connecting, rig.S)
            && rig.Published.Distinct(ReferenceEqualityComparer.Instance).Count() == rig.Published.Count);
    }

    private static async Task NoOpTicksDoNotPublish()
    {
        await using (var rig = CoordinatorRig.Create(schedule: true))
        {
            await rig.Coordinator.StartScheduleAsync();
            rig.Playing();
            var count = rig.Published.Count;
            await rig.Step(10);
            Check("CT-SM-20 steady Playing: 10 ticks that change nothing raise no SnapshotChanged", rig.Published.Count == count);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            var count = rig.Published.Count;
            await rig.Step(10);
            await rig.Coordinator.NotifySettingsChangedAsync();
            await rig.Coordinator.SetVolumeAsync(rig.Settings.Volume);
            Check("CT-SM-20 idle: ticks, an unchanged revalidation and an unchanged volume raise no SnapshotChanged", rig.Published.Count == count && count == 0);
        }
        await using (var rig = CoordinatorRig.Create())
        {
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Fail();
            var count = rig.Published.Count;
            await rig.Step(2);
            Check("CT-SM-20 the Failed countdown publishes once per changed second", rig.Published.Count == count + 2);
        }
    }

    // ─── CT-SM-21: seeded property test ───

    public static async Task PropertyAsync()
    {
        var watch = Stopwatch.StartNew();
        var visited = new HashSet<PlaybackStatus>();
        foreach (var seed in new[] { 42, 7, 1234, 2026, 31337 })
        {
            var (violation, coverage) = await RunProperty(seed, operations: 2000, visited);
            Check($"CT-SM-21 property (seed {seed}, 2000 random commands/ticks/engine events): invariants hold{(violation is null ? "" : " — " + violation)}", violation is null);
            Console.WriteLine($"   (seed {seed}: {coverage})");
        }
        Check("CT-SM-21 the random runs visited every coordinator state", visited.SetEquals(Enum.GetValues<PlaybackStatus>()));
        Console.WriteLine($"   (CT-SM-21 property runs took {watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// Invariants after every operation (single logical thread, so every side effect has completed):
    /// I1 at most one in-flight engine start, and only the newest one;
    /// I2 non-attempt states have no live engine session; attempt states have exactly the newest one live;
    /// I3 no engine start while inactive unless the operation was a UserPlay-like command or a ScheduleDue fired;
    /// I4 Disposing is terminal (no engine call, status unchanged);
    /// I5 SnapshotChanged is never raised while the gate is held, and never delivers a stale snapshot after a newer one;
    /// I6 snapshot field consistency.
    /// Returns a description of the first violation, or null.
    /// </summary>
    private static async Task<(string? Violation, string Coverage)> RunProperty(int seed, int operations, HashSet<PlaybackStatus> visited)
    {
        var random = new Random(seed);
        var rig = CoordinatorRig.Create(schedule: true);
        var settings = rig.Settings;
        settings.FallbackStationId = rig.B.Id;
        for (var minute = 30; minute < 24 * 60; minute += 90) // a daily slot every 90 minutes, rotating A/B/C
            settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[minute / 90 % 3].Id, Time = $"{minute / 60:00}:{minute % 60:00}", Days = [.. Enum.GetValues<DayOfWeek>()] });

        string? violation = null;
        var currentLabel = "";
        var currentStep = 0;
        var delivered = new List<PlaybackSnapshot>();
        rig.Coordinator.SnapshotChanged += (_, s) =>
        {
            delivered.Add(s);
            // Exempt: a start that throws synchronously fails the attempt inside the pump; the intermediate Connecting
            // snapshot is coalesced (never delivered), so Failed → (Connecting) → identical Failed arrives as a repeat.
            if (delivered.Count > 1 && delivered[^2].Equals(s) && rig.Engine.StartException is null) violation ??= $"I5 SnapshotChanged raised without a change during '{currentLabel}' (step {currentStep}): {s}";
            // Gate probe: a no-op command completes synchronously only if nobody holds the gate (single logical thread).
            if (!rig.Coordinator.SetVolumeAsync(settings.Volume).IsCompleted) violation ??= "I5 SnapshotChanged raised while the gate was held";
        };

        var disposed = false;
        var disposedCalls = 0;
        for (var step = 0; step < operations && violation is null; step++)
        {
            var wasActive = rig.S.IsActive;
            var startsBefore = rig.StartCount;
            var firedBefore = rig.LogCount("schedule.fired");
            var deliveredBefore = delivered.Count;
            var playLike = false;
            var op = random.Next(100);
            string label;
            currentStep = step;
            currentLabel = $"op {op}";
            if (!disposed && step == operations - 300) op = -1;
            switch (op)
            {
                case -1:
                    label = "Dispose";
                    await rig.Coordinator.DisposeAsync();
                    disposed = true;
                    disposedCalls = rig.Engine.CallLog.Count;
                    break;
                case < 5:
                    var target = random.Next(5) == 0 ? Guid.NewGuid() : settings.Stations[random.Next(settings.Stations.Count)].Id;
                    label = "Play";
                    playLike = true;
                    await rig.Coordinator.PlayAsync(target);
                    break;
                case < 9:
                    label = "Stop";
                    await rig.Coordinator.StopAsync();
                    break;
                case < 11:
                    label = "Toggle";
                    playLike = !wasActive;
                    await rig.Coordinator.ToggleAsync();
                    break;
                case < 12:
                    label = "Next";
                    playLike = true;
                    await rig.Coordinator.NextStationAsync();
                    break;
                case < 14:
                    label = "Volume";
                    await rig.Coordinator.SetVolumeAsync(random.Next(-20, 121));
                    break;
                case < 15:
                    label = "StartSchedule";
                    await rig.Coordinator.StartScheduleAsync();
                    break;
                case < 16:
                    label = "RefreshSchedule";
                    await rig.Coordinator.RefreshScheduleAsync();
                    break;
                case < 19:
                    label = "Wake(os)";
                    await rig.Coordinator.NotifyWakeAsync();
                    break;
                case < 23:
                    label = "SettingsChanged";
                    switch (random.Next(5))
                    {
                        case 0: settings.Stations[random.Next(settings.Stations.Count)].Url = random.Next(4) == 0 ? "not a url" : $"https://s{random.Next(1000)}.example.org/live"; break;
                        case 1: settings.FallbackStationId = random.Next(3) == 0 ? null : settings.Stations[random.Next(settings.Stations.Count)].Id; break;
                        case 2: settings.ScheduleEnabled = !settings.ScheduleEnabled; break;
                        case 3: settings.Volume = random.Next(-50, 151); break;
                        default: break;
                    }
                    await rig.Coordinator.NotifySettingsChangedAsync();
                    break;
                case < 24:
                    label = "Forget";
                    var forgotten = settings.Stations[random.Next(settings.Stations.Count)];
                    await rig.Coordinator.ForgetStationAsync(forgotten.Id);
                    settings.Stations.Remove(forgotten);
                    var replacement = new Station { Name = $"New{step}", Tag = "Tag N", Url = $"https://n{step}.example.org/live" };
                    settings.Stations.Add(replacement);
                    foreach (var entry in settings.Schedule.Where(e => e.StationId == forgotten.Id)) entry.StationId = replacement.Id; // keep the schedule alive
                    await rig.Coordinator.NotifySettingsChangedAsync();
                    break;
                case < 46:
                    label = "Step";
                    await rig.Step(random.Next(1, 4));
                    break;
                case < 54:
                    label = "Step(long)";
                    await rig.Step(random.Next(20, 40));
                    break;
                case < 60:
                    label = "WallJump";
                    await rig.Jump(TimeSpan.FromSeconds(1), wall: TimeSpan.FromMinutes(random.Next(-90, 240)));
                    break;
                case < 63:
                    label = "MonoGap";
                    await rig.Jump(TimeSpan.FromSeconds(random.Next(15, 60)));
                    break;
                case < 75:
                    label = "EngineState";
                    rig.Engine.RaiseState(PickSession(random, rig), (PlaybackEngineState)random.Next(6));
                    break;
                case < 83:
                    label = "EngineFailed";
                    rig.Engine.RaiseFailed(PickSession(random, rig), (PlaybackFailureKind)random.Next(8));
                    break;
                case < 87:
                    label = "HoldStarts";
                    rig.Engine.HoldStarts = !rig.Engine.HoldStarts;
                    break;
                case < 91:
                    label = "ReleaseStart";
                    foreach (var id in rig.Engine.HeldStartIds) rig.Engine.ReleaseStart(id);
                    break;
                case < 94:
                    label = "StartException";
                    rig.Engine.ThrowSynchronously = random.Next(2) == 0;
                    rig.Engine.StartException = rig.Engine.StartException is null && random.Next(3) == 0 ? new InvalidOperationException("boom") : null;
                    break;
                default:
                    label = "Title";
                    rig.Engine.SetTitle(random.Next(3) == 0 ? null : $"Song {random.Next(100)}");
                    break;
            }

            var s = rig.S;
            visited.Add(s.Status);
            var engine = rig.Engine;
            var where = $"seed {seed}, step {step} ({label}), status {s.Status}";
            var attempt = s.Status is PlaybackStatus.Connecting or PlaybackStatus.Reconnecting or PlaybackStatus.Playing;
            var held = engine.HeldStartIds;
            if (held.Count > 1 || (held.Count == 1 && (held[0] != engine.LastSessionId || !attempt)))
                violation ??= $"I1 in-flight starts [{string.Join(",", held)}] at {where}";
            if (!attempt && engine.ActiveSessionId is not null)
                violation ??= $"I2 engine session {engine.ActiveSessionId} still live at {where}";
            if (attempt && engine.ActiveSessionId != engine.LastSessionId)
                violation ??= $"I2 attempt state without the newest session live (active {engine.ActiveSessionId}, newest {engine.LastSessionId}) at {where}";
            if (!wasActive && rig.StartCount > startsBefore && !playLike && rig.LogCount("schedule.fired") == firedBefore)
                violation ??= $"I3 engine start while inactive without UserPlay/ScheduleDue at {where}";
            if (disposed && (s.Status != PlaybackStatus.Disposing || engine.CallLog.Count != disposedCalls))
                violation ??= $"I4 activity after dispose at {where}";
            if (delivered.Count > deliveredBefore && !ReferenceEquals(delivered[^1], s))
                violation ??= $"I5 the last delivered snapshot is not the current one at {where}";
            if ((s.RetryInSeconds is not null) != (s.Status == PlaybackStatus.Failed) || (s.IsFallback && !s.IsActive)
                || (s.IsPlaying && s.Status != PlaybackStatus.Playing)
                || (s.Status is PlaybackStatus.Stopped or PlaybackStatus.ScheduledWaiting or PlaybackStatus.Disposing) == s.IsActive
                || (s.Status == PlaybackStatus.ScheduledWaiting && (!settings.ScheduleEnabled || s.Next is null))
                || (!disposed && s.Volume != settings.Volume) || s.Volume is < 0 or > 100)
                violation ??= $"I6 inconsistent snapshot {s} at {where}";
        }
        if (!disposed) await rig.Coordinator.DisposeAsync();
        var coverage = $"{rig.StartCount} starts, {rig.Engine.StopCount} stops, {rig.LogCount("schedule.fired")} schedule fires, "
            + $"{rig.LogCount("playback.failed")} failures, {rig.LogCount("playback.fallback")} fallback switches/re-checks, "
            + $"{rig.LogCount("wake.recovery")} wake recoveries, {delivered.Count} snapshots";
        return (violation, coverage);
    }

    private static long PickSession(Random random, CoordinatorRig rig) => random.Next(4) switch
    {
        0 => Math.Max(0, rig.Session - 1),
        1 => rig.Session + 1,
        _ => rig.Session
    };

    // ─── CT-SM-22: races ───

    public static async Task RacesAsync()
    {
        var watch = Stopwatch.StartNew();
        await StopErrorWakeRetryRace(iterations: 200);
        await ConcurrentPlaysRace(iterations: 200);
        await DisposeRace(iterations: 200);
        Console.WriteLine($"   (CT-SM-22 races took {watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>UserStop, a current-session error, WakeDetected and a due RetryDue (primary re-check), issued concurrently.</summary>
    private static async Task StopErrorWakeRetryRace(int iterations)
    {
        string? failure = null;
        var outcomes = new Dictionary<string, int>();
        for (var i = 0; i < iterations && failure is null; i++)
        {
            var rig = CoordinatorRig.Create();
            rig.Settings.FallbackStationId = rig.B.Id;
            await rig.Coordinator.PlayAsync(rig.A.Id);
            rig.Fail();
            await rig.Step(3);
            rig.Fail();
            await rig.Step(6);
            rig.Fail();
            await rig.Step(30);
            rig.Playing();
            await rig.Step(119); // the next tick makes the primary re-check (RetryDue) due
            var baseline = rig.Engine.CallLog.Count;
            var failedBefore = rig.LogCount("playback.failed");

            using var go = new ManualResetEventSlim();
            Task Fire(Func<Task> action) => Task.Run(async () => { go.Wait(); await action(); });
            var all = Task.WhenAll(
                Fire(() => rig.Coordinator.StopAsync()),
                Fire(() => { rig.Fail(); return Task.CompletedTask; }),
                Fire(() => rig.Coordinator.NotifyWakeAsync()),
                Fire(() => rig.Step(1)));
            go.Set();
            if (!await Wait.Finishes(all, Deadlock)) { failure = $"iteration {i}: deadlock (not finished within {Deadlock.TotalSeconds} s)"; break; }
            if (!await Wait.Until(() => rig.Status is PlaybackStatus.Stopped or PlaybackStatus.ScheduledWaiting && rig.Engine.ActiveSessionId is null, Deadlock))
            {
                failure = $"iteration {i}: final status {rig.Status}, engine session {rig.Engine.ActiveSessionId}";
                break;
            }
            // Which interleaving won: engine calls made during the race + whether the error / the wake took effect.
            var outcome = string.Join(",", rig.Engine.CallLog.Skip(baseline))
                + (rig.LogCount("playback.failed") > failedBefore ? " +error" : "")
                + (rig.Log.Entries.Any(e => e.EventName == "wake.detected" && e.Message.Contains("suspending")) ? " +wake" : "");
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            var starts = rig.StartCount;
            await rig.Step(200);
            var log = rig.Engine.CallLog;
            if (rig.StartCount != starts || log[^1] != "stop" || rig.S.IsActive || !ReferenceEquals(rig.Published[^1], rig.S))
                failure = $"iteration {i}: start after stop or stale snapshot; calls [{string.Join(",", log.TakeLast(4))}], status {rig.Status}";
            await rig.Coordinator.DisposeAsync();
        }
        Check($"CT-SM-22 race ×{iterations}: UserStop ‖ current error ‖ Wake ‖ RetryDue → Stopped, no start after the stop, no deadlock{(failure is null ? "" : " — " + failure)}",
            failure is null);
        Console.WriteLine($"   (interleavings seen: {string.Join("; ", outcomes.OrderByDescending(o => o.Value).Select(o => $"[{o.Key}] ×{o.Value}"))})");
    }

    /// <summary>Concurrent UserPlays, ticks and engine events with held starts: one accepted session, at most one in-flight start.</summary>
    private static async Task ConcurrentPlaysRace(int iterations)
    {
        string? failure = null;
        for (var i = 0; i < iterations && failure is null; i++)
        {
            var rig = CoordinatorRig.Create();
            rig.Engine.HoldStarts = i % 2 == 0;
            using var go = new ManualResetEventSlim();
            Task Fire(Func<Task> action) => Task.Run(async () => { go.Wait(); await action(); });
            var all = Task.WhenAll(
                Fire(() => rig.Coordinator.PlayAsync(rig.A.Id)),
                Fire(() => rig.Coordinator.PlayAsync(rig.B.Id)),
                Fire(() => rig.Coordinator.PlayAsync(rig.C.Id)),
                Fire(() => rig.Coordinator.OnTickAsync(CancellationToken.None)),
                Fire(() => { rig.Engine.RaiseState(1, PlaybackEngineState.Playing); return Task.CompletedTask; }),
                Fire(() => { rig.Engine.RaiseState(2, PlaybackEngineState.Playing); return Task.CompletedTask; }),
                Fire(() => rig.Coordinator.SetVolumeAsync(40)));
            go.Set();
            if (!await Wait.Finishes(all, Deadlock)) { failure = $"iteration {i}: deadlock"; break; }
            // Drain any callback still queued behind the gate, then settle.
            await Wait.Until(() => rig.Engine.HeldStartIds.Count <= 1, Deadlock);
            await rig.Coordinator.SetVolumeAsync(40);
            var s = rig.S;
            var engine = rig.Engine;
            var last = engine.LastSessionId;
            // Playing for sessions 1/2 may be accepted while they are current, but a later play always supersedes them.
            var ok = engine.Starts.Count == 3 && engine.HeldStartIds.All(id => id == last) && engine.ActiveSessionId == last
                && s.Status == PlaybackStatus.Connecting && !s.IsPlaying
                && s.CurrentStationName == engine.Starts[^1].Source.DisplayName && s.DesiredStationId == s.CurrentStationId;
            engine.ReleaseStart(last);
            engine.RaiseState(last, PlaybackEngineState.Playing);
            ok &= rig.Status == PlaybackStatus.Playing && rig.S.CurrentStationName == engine.Starts[^1].Source.DisplayName;
            if (!ok) failure = $"iteration {i}: status {s.Status}, current {s.CurrentStationName}, starts [{string.Join(",", engine.Starts.Select(x => x.Source.DisplayName))}], held [{string.Join(",", engine.HeldStartIds)}], active {engine.ActiveSessionId}";
            await rig.Coordinator.DisposeAsync();
        }
        Check($"CT-SM-22 race ×{iterations}: concurrent Play A/B/C + tick + stale Playing + volume → the newest start is the only live session{(failure is null ? "" : " — " + failure)}",
            failure is null);
    }

    /// <summary>DisposeAsync racing plays, ticks, wakes and engine events: terminal, engine disposed once, no start after the final stop.</summary>
    private static async Task DisposeRace(int iterations)
    {
        string? failure = null;
        for (var i = 0; i < iterations && failure is null; i++)
        {
            var rig = CoordinatorRig.Create(schedule: true);
            rig.Engine.HoldStarts = i % 3 == 0;
            await rig.Coordinator.StartScheduleAsync();
            using var go = new ManualResetEventSlim();
            Task Fire(Func<Task> action) => Task.Run(async () => { go.Wait(); await action(); });
            var all = Task.WhenAll(
                Fire(() => rig.Coordinator.DisposeAsync().AsTask()),
                Fire(() => rig.Coordinator.PlayAsync(rig.B.Id)),
                Fire(() => rig.Coordinator.RefreshScheduleAsync()),
                Fire(() => rig.Coordinator.NotifyWakeAsync()),
                Fire(() => rig.Step(1)),
                Fire(() => { rig.Playing(); rig.Fail(); return Task.CompletedTask; }),
                Fire(() => rig.Coordinator.DisposeAsync().AsTask()));
            go.Set();
            if (!await Wait.Finishes(all, Deadlock)) { failure = $"iteration {i}: deadlock"; break; }
            var log = rig.Engine.CallLog;
            var lastStop = log.ToList().LastIndexOf("stop");
            if (rig.Status != PlaybackStatus.Disposing || rig.Engine.DisposeCount != 1 || lastStop < 0 || log.Skip(lastStop + 1).Any(c => c.StartsWith("start", StringComparison.Ordinal))
                || !ReferenceEquals(rig.Published[^1], rig.S))
                failure = $"iteration {i}: status {rig.Status}, disposed {rig.Engine.DisposeCount}, calls [{string.Join(",", log)}]";
        }
        Check($"CT-SM-22 race ×{iterations}: DisposeAsync ‖ Play ‖ Refresh ‖ Wake ‖ tick ‖ engine events → Disposing, engine disposed once, no start after the final stop{(failure is null ? "" : " — " + failure)}",
            failure is null);
    }
}
