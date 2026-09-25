// PlaybackCoordinator: the UI-agnostic playback + schedule state machine (brief 1 §5, docs/acceptance-matrix.md §5).
//
// ─── Per-state event acceptance (normative; mirrors docs/acceptance-matrix.md §5.2) ─────────────────────────────────────
// Columns: Stp=Stopped  SW=ScheduledWaiting  Con=Connecting  Ply=Playing  Rec=Reconnecting  Fail=Failed
//          Sus=SuspendedBySystem  Dis=Disposing.
// "ign" = ignored: no state change and no engine call. "→Idle" = →SW when the schedule is on and has a next occurrence,
// else →Stopped (derived when the snapshot is built). "—" = cannot occur in that state.
//
// Input                          Stp      SW       Con       Ply       Rec       Fail      Sus        Dis
// UserPlay                       →Con     →Con     →Con¹     →Con      →Con      →Con      →Con²      ign
// UserStop                       hold³    hold³    →Idle     →Idle     →Idle     →Idle     →Idle      ign
// ScheduleDue (new or forced)    →Con     →Con     →Con      →Con      →Con      →Con      deferred⁴  ign
// Engine Playing (current)       ign      ign      →Ply      refresh   →Ply      ign       ign        ign
// Engine not-Playing (current)⁵  ign      ign      ign       Ply⁵      ign       ign       ign        ign
// Engine Failed/Ended, stall⁶    ign      ign      →Fail     →Fail     →Fail     ign⁷      ign        ign
// Any engine event, stale        ign      ign      ign       ign       ign       ign       ign        ign
// RetryDue (tick, gen-checked)   —        —        —         →Rec⁸     —         →Rec      —          ign
// WakeDetected (os / tick gap)⁹  sched¹⁰  sched¹⁰  →Sus      →Sus      →Sus      →Sus      ign        ign
// Settle elapsed (tick)          —        —        —         —         —         —         →Con/Rec¹¹ —
// SettingsChanged                reval¹²  reval¹²  reval¹²   reval¹²   reval¹²   reval¹²   reval¹²    ign
// Dispose                        →Dis     →Dis     →Dis      →Dis      →Dis      →Dis      →Dis       ign
//
// ¹ Supersedes the attempt in flight (its CTS is cancelled and its session id is no longer accepted).
// ² Cancels the pending wake recovery.
// ³ Only HoldCurrent runs, so the stop holds within the current slot. No engine call. Idempotent.
// ⁴ Folded into the recovery's own schedule check (a forced refresh is remembered), so exactly one reconnect happens.
//   This fixes the legacy "wake after a new slot opens the stream twice" bug.
// ⁵ Opening/Buffering/Ended/Stopped/Idle for the current session. It changes nothing before the first Playing, because
//   the stall clock already runs from the attempt start. In Ply it keeps the status, sets IsPlaying=false, resets the
//   60 s stable timer and starts the stall clock.
// ⁶ The stall watchdog fires on the first tick where an active session has spent strictly more than 25 s outside
//   engine Playing since the attempt started or since it left Playing.
// ⁷ The failed session is retired at once, so a second failure for it is stale: one failure per attempt.
// ⁸ Only as the primary re-check while the fallback plays (120 s after the fallback reached Playing).
// ⁹ Also rate-limited in every state: a wake within WakeDebounce of the last accepted wake, or of the last completed
//   recovery, is ignored. This lets an OS notification and a tick gap for the same wake produce one recovery.
// ¹⁰ Schedule check only: the tick-gap path runs it in the same tick; the OS path leaves it to the next tick.
// ¹¹ Runs the schedule check first (a new slot → Con), else reconnects the desired station once (→Rec) with failures
//    and fallback reset. A failure then follows the normal retry policy.
// ¹² A removed desired/current station behaves like ForgetStation (→Idle). A changed URL of the active desired station
//    behaves like a manual UserPlay of it. A changed fallback applies to the next due retry. A changed volume is re-sent.
//
// ─── Serialization design (brief 1 §5.4–§5.5) ───────────────────────────────────────────────────────────────────────────
// • ONE state gate (SemaphoreSlim(1,1)) guards every field below "State". It is never held across an await: every public
//   member acquires it, runs a synchronous transition, captures its side effects (operation CTSs to cancel, engine
//   commands, log lines, the new snapshot), releases it, and only then performs those side effects.
// • Operation generation + linked operation CTS: every attempt, retry, primary re-check, wake recovery, stop and dispose
//   calls NewOperation(), which bumps `operationGeneration`, clears the accepted session id, and replaces the operation
//   CTS (linked to the one lifetime CTS; the old one is cancelled after the gate is released). Retry and settle timers
//   remember the generation that armed them and fire only while it is still current; engine callbacks are accepted only
//   for the session id AND generation of the current attempt. A stale timer or callback therefore exits harmlessly.
// • Engine commands (Start/Stop/SetVolume) are appended to a FIFO queue while the gate is held, so their order is the
//   order of the transitions that produced them. After the gate is released a non-reentrant "pump" invokes them strictly
//   one after another in that order (at most one thread pumps; an engine callback raised synchronously inside
//   StartAsync cannot recurse into the engine). The engine contract increments its session counter synchronously on
//   entry to each StartAsync, and every enqueued Start is invoked (a superseded one receives an already-cancelled token
//   and ends silently), so the N-th enqueued Start is engine session N: the coordinator assigns that id when it
//   enqueues the command. The pump does NOT await a command's completion before invoking the next one: a Stop must reach
//   the engine immediately even while a slow connect is still in flight (stop-while-connecting). Adapters must therefore
//   tolerate StopAsync/StartAsync while an earlier StartAsync is still running; cancellation of its token tells them it
//   is superseded. The engine-command queue is not the state gate and never blocks it.
// • Engine callbacks (any thread) never touch state directly: they are queued in arrival order and drained under the
//   gate by whichever drainer acquires it first, then validated as above. They never block the engine's thread.
// • SnapshotChanged is raised after the gate is released, only when the immutable snapshot value changed, and in
//   sequence order (an older snapshot is never delivered after a newer one). Handlers must not block.
// • Session ids are committed atomically (CR-01): Open builds and validates everything that can fail first, then
//   enqueues the Start and assigns its id in one step, so nothing can throw between the two.
// • Fault rule (CR-01): a transition that throws never strands the coordinator. After the throw (gate still held) the
//   coordinator checks that play intent has exactly one live driver of the current generation: an accepted engine
//   session (Connecting/Reconnecting/Playing), an armed retry (Failed) or an armed settle timer (Suspended). If not, the
//   attempt becomes an ordinary failure (Failed, retry on the normal backoff), or Stopped without a station. Side effects
//   captured before the throw are still performed, and the exception is logged (playback.internal_error) and rethrown
//   to the caller (engine-event path: logged only).
//
// ─── Threading contract for hosts ─────────────────────────────────────────────────────────────────────────────────────
// Settings is plain mutable data. The host mutates it and calls the commands and OnTickAsync from one logical thread (the
// UI thread). Those members await the gate without ConfigureAwait(false), so the transition runs on the caller's context;
// engine callbacks and NotifyWakeAsync read only scalar settings (volume, ScheduleEnabled) and never enumerate Settings
// collections (NotifyWakeAsync leaves the schedule check of an inactive wake to the next tick).
//
// ─── Timing ───────────────────────────────────────────────────────────────────────────────────────────────────────────
// Every policy timer (retry backoff, primary re-check, 60 s stable reset, 25 s stall watchdog, 15 s wake gap, 2 s wake
// settle, wake debounce) is measured on IMonotonicClock and evaluated in OnTickAsync. There is no Task.Delay, so tests
// drive time by advancing fake clocks and calling OnTickAsync. Wall-clock time (IClock converted to localZone) is used
// only for the schedule.
//
// ─── Engine ownership ─────────────────────────────────────────────────────────────────────────────────────────────────
// The coordinator takes ownership of the engine: DisposeAsync stops it, unsubscribes, and disposes it. The composition
// root must not dispose the engine itself.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace DialShift.Core.Playback;

/// <summary>
/// The playback + schedule coordinator (brief 1 §5). See the source header for per-state event acceptance, the
/// serialization design, the host threading contract and engine ownership.
/// </summary>
public sealed class PlaybackCoordinator : IPlaybackCoordinator
{
    /// <summary>A monotonic gap of at least this much between two ticks is treated as a wake from sleep.</summary>
    public static readonly TimeSpan WakeGapThreshold = TimeSpan.FromSeconds(15);

    /// <summary>After a wake, the coordinator waits this long (monotonic, tick-driven) for the network before reconnecting.</summary>
    public static readonly TimeSpan WakeSettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>A wake detected within this window of the previous accepted wake or completed recovery is ignored (OS notification + tick gap for one wake).</summary>
    public static readonly TimeSpan WakeDebounce = TimeSpan.FromSeconds(10);

    private readonly Settings settings;
    private readonly IPlaybackEngine engine;
    private readonly ITrackMetadataProvider? metadata;
    private readonly IClock clock;
    private readonly IMonotonicClock monotonicClock;
    private readonly IAppLog log;
    private readonly TimeZoneInfo localZone;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<EngineCommand> engineCommands = new();
    private readonly ConcurrentQueue<EngineSignal> engineSignals = new();
    private int pumping;
    private readonly object notifyLock = new();
    private long notifiedSequence;
    private readonly object disposeLock = new();
    private Task? disposal;
    private volatile PlaybackSnapshot snapshot;

    // ─── State: guarded by `gate` ───
    private readonly ScheduleSession scheduleSession = new();
    private CancellationTokenSource operationCts;
    private long operationGeneration;
    private long startsIssued;
    private long currentSessionId;
    private long sessionGeneration;
    private bool engineSessionLive;
    private int lastSentVolume = -1;
    private PlaybackStatus status = PlaybackStatus.Stopped;
    private bool disposed;
    private Station? desired;
    private string? desiredUrl;
    private Station? current;
    private bool isActive;
    private bool isPlaying;
    private bool fallback;
    private int failures;
    private string statusText;
    private string trackText;
    private int? retryInSeconds;
    private long? retryStartedAt;
    private TimeSpan retryDelay;
    private long retryGeneration;
    private long? outsidePlayingSince;
    private long? stableSince;
    private bool hasTicked;
    private long lastTickAt;
    private long? settleStartedAt;
    private long settleGeneration;
    private bool forceScheduleOnRecovery;
    private long? lastWakeAt;
    private Occurrence? upcoming;
    private string? upcomingStationName;
    private long snapshotSequence;
    private readonly List<CancellationTokenSource> retiredOperations = [];
    private readonly List<LogEntry> pendingLogs = [];

    /// <param name="settings">Shared settings; see the host threading contract in the source header.</param>
    /// <param name="engine">Playback port. The coordinator takes ownership and disposes it in <see cref="DisposeAsync"/>.</param>
    /// <param name="clock">Wall clock, used only for schedule evaluation.</param>
    /// <param name="monotonicClock">Monotonic clock for every policy timer and the wake gap.</param>
    /// <param name="log">Structured diagnostic log (never receives stream URLs).</param>
    /// <param name="localZone">The computer's zone for schedule evaluation; <see cref="TimeZoneInfo.Local"/> when null.</param>
    public PlaybackCoordinator(Settings settings, IPlaybackEngine engine, IClock clock, IMonotonicClock monotonicClock, IAppLog log, TimeZoneInfo? localZone = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(monotonicClock);
        ArgumentNullException.ThrowIfNull(log);
        this.settings = settings;
        this.engine = engine;
        this.clock = clock;
        this.monotonicClock = monotonicClock;
        this.log = log;
        this.localZone = localZone ?? TimeZoneInfo.Local;
        metadata = engine as ITrackMetadataProvider;
        operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        snapshot = PlaybackSnapshot.Initial(Math.Clamp(settings.Volume, 0, 100));
        statusText = snapshot.StatusText;
        trackText = snapshot.TrackText;
        engine.StateChanged += OnEngineStateChanged;
        engine.Failed += OnEngineFailed;
        if (metadata != null) metadata.MetadataChanged += OnMetadataChanged;
    }

    public PlaybackSnapshot Snapshot => snapshot;

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;

    public Task PlayAsync(Guid stationId) => RunAsync(now =>
    {
        if (Find(stationId) is { } station) UserPlay(station, now);
    });

    public Task ToggleAsync() => RunAsync(now =>
    {
        if (isActive) UserStop();
        else if ((Find(desired?.Id) ?? Find(settings.LastStationId) ?? settings.Stations.FirstOrDefault()) is { } station) UserPlay(station, now);
    });

    public Task StopAsync() => RunAsync(_ => UserStop());

    public Task NextStationAsync() => RunAsync(now =>
    {
        if (settings.Stations.Count == 0) return;
        var index = settings.Stations.FindIndex(s => s.Id == (desired?.Id ?? current?.Id));
        UserPlay(settings.Stations[(index + 1) % settings.Stations.Count], now);
    });

    public Task SetVolumeAsync(int volume) => RunAsync(_ =>
    {
        settings.Volume = Math.Clamp(volume, 0, 100);
        SendVolumeIfChanged();
    }, refreshSchedule: false);

    public Task StartScheduleAsync() => RunAsync(now => CheckSchedule(force: true, now));

    public Task RefreshScheduleAsync() => RunAsync(now => CheckSchedule(force: true, now));

    public Task OnTickAsync(CancellationToken cancellationToken) => RunAsync(Tick, refreshSchedule: true, cancellationToken);

    public Task NotifyWakeAsync() => RunAsync(now => DetectWake(now, "os"), refreshSchedule: false);

    public Task NotifySettingsChangedAsync() => RunAsync(SettingsChanged);

    public Task ForgetStationAsync(Guid stationId) => RunAsync(_ => Forget(stationId));

    public ValueTask DisposeAsync()
    {
        lock (disposeLock) disposal ??= DisposeCoreAsync();
        return new ValueTask(disposal);
    }

    // ─── Transitions (always called with the gate held; never await) ───

    private void UserPlay(Station station, long now)
    {
        // A manual choice holds the current occurrence so the next tick does not override it.
        scheduleSession.HoldCurrent(settings, LocalNow());
        StartPlayback(station, now);
    }

    private void StartPlayback(Station station, long now)
    {
        desired = station;
        desiredUrl = station.Url;
        settings.LastStationId = station.Id;
        failures = 0;
        fallback = false;
        isActive = true;
        ClearRecovery();
        Open(station, PlaybackStatus.Connecting, now);
    }

    private void UserStop()
    {
        scheduleSession.HoldCurrent(settings, LocalNow());
        Halt();
    }

    /// <summary>Ends play intent: retires the session and every timer. No schedule hold, so it cannot throw on schedule data.</summary>
    private void Halt()
    {
        if (!isActive) return;
        isActive = false;
        RetireSession();
        ClearRetry();
        ClearRecovery();
        status = PlaybackStatus.Stopped;
        statusText = settings.ScheduleEnabled ? "Paused · resumes at the next scheduled change" : "Paused";
        trackText = "Press play to return to the live broadcast.";
    }

    private void Forget(Guid stationId)
    {
        if (desired?.Id == stationId || current?.Id == stationId) UserStop();
        if (desired?.Id == stationId) desired = null;
        if (current?.Id == stationId) current = null;
    }

    private void SettingsChanged(long now)
    {
        settings.Volume = Math.Clamp(settings.Volume, 0, 100);
        SendVolumeIfChanged();
        if (desired is { } d && Find(d.Id) is null) Forget(d.Id);
        if (current is { } c && Find(c.Id) is null) Forget(c.Id);
        if (current is { } cur) current = Find(cur.Id);
        if (desired is { } des && Find(des.Id) is { } station)
        {
            desired = station;
            if (isActive && station.Url != desiredUrl) UserPlay(station, now);
            else desiredUrl = station.Url;
        }
    }

    private void Open(Station station, PlaybackStatus attemptStatus, long now)
    {
        NewOperation();
        ClearRetry();
        current = station;
        isPlaying = false;
        stableSince = null;
        outsidePlayingSince = now;
        status = attemptStatus;
        statusText = fallback ? "Connecting to fallback…" : "Connecting…";
        trackText = "Opening the live stream";
        // The URL is read once, so validation and the Uri the engine receives always agree.
        var url = station.Url;
        if (!SettingsStore.ValidUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Warn("playback.invalid_url", $"Station '{station.Name}' has no valid http(s) stream URL.");
            Fail(PlaybackFailureKind.InvalidUrl, now);
            return;
        }
        // Build the command first; then enqueue and commit its id together, so the N-th Start stays session N (D16).
        var sessionId = startsIssued + 1;
        var volume = settings.Volume;
        var start = EngineCommand.Start(sessionId, new StreamSource(uri, station.Name), volume / 100.0, operationCts.Token);
        engineCommands.Enqueue(start);
        startsIssued = sessionId;
        currentSessionId = sessionId;
        sessionGeneration = operationGeneration;
        engineSessionLive = true;
        lastSentVolume = volume;
    }

    private void Fail(PlaybackFailureKind kind, long now, string? diagnostic = null)
    {
        if (!isActive || status is not (PlaybackStatus.Connecting or PlaybackStatus.Reconnecting or PlaybackStatus.Playing)) return;
        EnterFailed(kind, now, diagnostic);
    }

    private void EnterFailed(PlaybackFailureKind kind, long now, string? diagnostic)
    {
        var session = currentSessionId;
        failures++;
        RetireSession();
        StartRetryTimer(RetryPolicy.DelayAfterFailure(failures), now);
        retryInSeconds = (int)retryDelay.TotalSeconds;
        status = PlaybackStatus.Failed;
        statusText = $"Stream unavailable · retry in {retryInSeconds}s";
        trackText = "Your next scheduled change will still run.";
        Warn("playback.failed", $"kind={kind}; station='{current?.Name}'; session={session}; attempt={failures}; retry_in={retryInSeconds}s; fallback={fallback}"
            + (string.IsNullOrWhiteSpace(diagnostic) ? "" : $"; diagnostic={diagnostic}"));
    }

    private void Tick(long now)
    {
        if (hasTicked && monotonicClock.GetElapsedTime(lastTickAt, now) >= WakeGapThreshold) DetectWake(now, "tick_gap");
        hasTicked = true;
        lastTickAt = now;

        if (status == PlaybackStatus.SuspendedBySystem && settleStartedAt is { } settle && settleGeneration == operationGeneration
            && Elapsed(settle, now) >= WakeSettleDelay)
            CompleteRecovery(now);
        CheckSchedule(force: false, now);

        if (isPlaying && !fallback && stableSince is { } stable && Elapsed(stable, now) >= RetryPolicy.StablePlaybackReset) failures = 0;

        if (status == PlaybackStatus.Failed && retryStartedAt is { } failedAt)
        {
            retryInSeconds = RetryPolicy.SecondsRemaining(retryDelay, Elapsed(failedAt, now));
            statusText = $"Stream unavailable · retry in {retryInSeconds}s";
        }

        if (isActive && desired != null && retryStartedAt is { } armedAt && retryGeneration == operationGeneration
            && (status is PlaybackStatus.Failed or PlaybackStatus.Playing) && Elapsed(armedAt, now) >= retryDelay)
        {
            var (target, useFallback) = RetryPolicy.SelectRetryTarget(settings, desired, failures, fallback);
            if (useFallback) Info("playback.fallback", $"Switching to fallback '{target.Name}' after {failures} failures of '{desired.Name}'.");
            else if (fallback) Info("playback.fallback", $"Re-trying primary '{desired.Name}'.");
            fallback = useFallback;
            Open(target, PlaybackStatus.Reconnecting, now);
        }
        else if (isActive && currentSessionId != 0 && outsidePlayingSince is { } outside && Elapsed(outside, now) > RetryPolicy.StallTimeout)
        {
            Fail(PlaybackFailureKind.Stalled, now);
        }

        RefreshTrack();
    }

    private void CheckSchedule(bool force, long now)
    {
        if (!settings.ScheduleEnabled) return;
        if (status == PlaybackStatus.SuspendedBySystem)
        {
            // ScheduleDue while suspended is folded into the recovery's own schedule check (exactly one reconnect).
            forceScheduleOnRecovery |= force;
            return;
        }
        TakeScheduleChange(force, now);
    }

    private bool TakeScheduleChange(bool force, long now)
    {
        var slot = scheduleSession.TakeChange(settings, LocalNow(), force);
        if (slot == null || Find(slot.Entry.StationId) is not { } station) return false;
        Info("schedule.fired", $"Slot {slot.Entry.Time} starts '{station.Name}'" + (force ? " (forced)." : "."));
        StartPlayback(station, now);
        return true;
    }

    private void DetectWake(long now, string source)
    {
        if (status == PlaybackStatus.SuspendedBySystem)
        {
            Info("wake.detected", $"source={source}; ignored: recovery already in progress.");
            return;
        }
        if (lastWakeAt is { } previous && Elapsed(previous, now) < WakeDebounce)
        {
            Info("wake.detected", $"source={source}; ignored: rate-limited.");
            return;
        }
        lastWakeAt = now;
        if (!isActive)
        {
            Info("wake.detected", $"source={source}; inactive: the next schedule check decides.");
            return;
        }
        Info("wake.detected", $"source={source}; suspending '{desired?.Name}' until the network settles.");
        RetireSession();
        ClearRetry();
        failures = 0;
        fallback = false;
        status = PlaybackStatus.SuspendedBySystem;
        settleStartedAt = now;
        settleGeneration = operationGeneration;
        forceScheduleOnRecovery = false;
        statusText = "Connecting…";
        trackText = "Opening the live stream";
    }

    private void CompleteRecovery(long now)
    {
        var force = forceScheduleOnRecovery;
        ClearRecovery();
        // The debounce window restarts here, so a late duplicate signal for this wake cannot trigger a second reconnect.
        lastWakeAt = now;
        if (settings.ScheduleEnabled && TakeScheduleChange(force, now))
        {
            Info("wake.recovery", "outcome=schedule_slot_started");
            return;
        }
        if (Find(desired?.Id) is { } station)
        {
            Open(station, PlaybackStatus.Reconnecting, now);
            Info("wake.recovery", $"outcome=reconnecting; station='{station.Name}'");
            return;
        }
        UserStop();
        Info("wake.recovery", "outcome=stopped; the desired station no longer exists");
    }

    private void ApplySignal(EngineSignal signal, long now)
    {
        if (signal.Kind == SignalKind.Metadata)
        {
            RefreshTrack();
            return;
        }
        // Validated re-entry: only the current attempt's session, in its own generation, while there is play intent.
        if (signal.SessionId == 0 || signal.SessionId != currentSessionId || sessionGeneration != operationGeneration || !isActive) return;
        if (status is not (PlaybackStatus.Connecting or PlaybackStatus.Reconnecting or PlaybackStatus.Playing)) return;
        if (signal.Kind == SignalKind.Failed)
        {
            Fail(signal.FailureKind, now, signal.Diagnostic);
            return;
        }
        if (signal.State == PlaybackEngineState.Playing)
        {
            if (status != PlaybackStatus.Playing)
            {
                status = PlaybackStatus.Playing;
                statusText = fallback ? "Live · fallback station" : "Live broadcast";
                if (fallback && retryStartedAt is null) StartRetryTimer(RetryPolicy.PrimaryRecheckInterval, now);
            }
            isPlaying = true;
            outsidePlayingSince = null;
            stableSince ??= now;
            RefreshTrack();
        }
        else if (isPlaying)
        {
            // Opening/Buffering/Ended/Stopped/Idle: audible playback stopped advancing; the stall watchdog starts.
            isPlaying = false;
            stableSince = null;
            outsidePlayingSince = now;
        }
    }

    /// <summary>
    /// The fault rule (see the source header). Runs with the gate held after a transition threw. Play intent must have one
    /// live driver of the current generation, or neither engine events nor any timer would ever move the coordinator on
    /// (for example, NewOperation() ran but no Start was enqueued, so currentSessionId is 0 and the stall watchdog skips it).
    /// Without one, the attempt becomes an ordinary failure with a retry (a stop when there is no desired station to retry).
    /// Both outcomes only write fields, replace the operation CTS and enqueue a Stop, so this does not throw in practice.
    /// </summary>
    private void RecoverFromFault(long now)
    {
        var driven = status switch
        {
            PlaybackStatus.Connecting or PlaybackStatus.Reconnecting or PlaybackStatus.Playing =>
                currentSessionId != 0 && sessionGeneration == operationGeneration,
            PlaybackStatus.Failed => retryStartedAt != null && retryGeneration == operationGeneration,
            PlaybackStatus.SuspendedBySystem => settleStartedAt != null && settleGeneration == operationGeneration,
            _ => false
        };
        if (!isActive || driven) return;
        if (desired is null) Halt();
        else EnterFailed(PlaybackFailureKind.Unknown, now, "internal error");
    }

    // ─── Helpers (gate held) ───

    private void NewOperation()
    {
        retiredOperations.Add(operationCts);
        operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationGeneration++;
        currentSessionId = 0;
    }

    private void RetireSession()
    {
        NewOperation();
        isPlaying = false;
        stableSince = null;
        outsidePlayingSince = null;
        if (!engineSessionLive) return;
        engineSessionLive = false;
        engineCommands.Enqueue(EngineCommand.Stop());
    }

    private void StartRetryTimer(TimeSpan delay, long now)
    {
        retryDelay = delay;
        retryStartedAt = now;
        retryGeneration = operationGeneration;
    }

    private void ClearRetry()
    {
        retryStartedAt = null;
        retryInSeconds = null;
    }

    private void ClearRecovery()
    {
        settleStartedAt = null;
        forceScheduleOnRecovery = false;
    }

    private void SendVolumeIfChanged()
    {
        if (!engineSessionLive || lastSentVolume == settings.Volume) return;
        lastSentVolume = settings.Volume;
        engineCommands.Enqueue(EngineCommand.SetVolume(settings.Volume / 100.0));
    }

    private void RefreshTrack()
    {
        if (!isPlaying) return;
        var title = metadata?.CurrentTitle;
        trackText = string.IsNullOrWhiteSpace(title) ? current?.Tag ?? "Live radio" : title;
    }

    private Station? Find(Guid? id) => id is { } value ? settings.Stations.FirstOrDefault(s => s.Id == value) : null;

    private DateTime LocalNow() => TimeZoneInfo.ConvertTime(clock.UtcNow, localZone).DateTime;

    private TimeSpan Elapsed(long from, long now) => monotonicClock.GetElapsedTime(from, now);

    private void Info(string eventName, string message) => pendingLogs.Add(new(LogLevel.Info, eventName, message));

    private void Warn(string eventName, string message) => pendingLogs.Add(new(LogLevel.Warn, eventName, message));

    private void RefreshUpcoming()
    {
        upcoming = settings.ScheduleEnabled ? Scheduler.Evaluate(settings, LocalNow()).Next : null;
        upcomingStationName = upcoming is null ? null : Find(upcoming.Entry.StationId)?.Name;
    }

    private PlaybackSnapshot BuildSnapshot() => new(
        status == PlaybackStatus.Stopped && settings.ScheduleEnabled && upcoming != null ? PlaybackStatus.ScheduledWaiting : status,
        desired?.Id, desired?.Name, current?.Id, current?.Name,
        IsActive: isActive, IsPlaying: isPlaying, IsFallback: isActive && fallback,
        StatusText: statusText, TrackText: trackText,
        RetryInSeconds: status == PlaybackStatus.Failed ? retryInSeconds : null,
        Next: upcoming, NextStationName: upcomingStationName,
        Volume: settings.Volume);

    /// <summary>Ends a transition: publishes the snapshot if it changed and hands the captured side effects to <see cref="Complete"/>.</summary>
    private Exit Leave()
    {
        var built = BuildSnapshot();
        PlaybackSnapshot? changed = null;
        if (!built.Equals(snapshot))
        {
            if (built.Status != snapshot.Status)
                Info("playback.state", $"{snapshot.Status} -> {built.Status}; station='{built.CurrentStationName}'; session={currentSessionId}");
            snapshot = built;
            changed = built;
            snapshotSequence++;
        }
        var exit = new Exit(changed, snapshotSequence, [.. retiredOperations], [.. pendingLogs]);
        retiredOperations.Clear();
        pendingLogs.Clear();
        return exit;
    }

    // ─── Outside the gate ───

    private async Task RunAsync(Action<long> transition, bool refreshSchedule = true, CancellationToken cancellationToken = default)
    {
        // Deliberately context-preserving (no ConfigureAwait(false)): see "Threading contract for hosts" in the header.
        await gate.WaitAsync(cancellationToken);
        Exit? exit = null;
        Exception? failure = null;
        try
        {
            if (disposed) return;
            var now = monotonicClock.GetTimestamp();
            try
            {
                transition(now);
                if (refreshSchedule) RefreshUpcoming();
            }
            catch (Exception ex)
            {
                failure = ex;
                pendingLogs.Add(new(LogLevel.Error, "playback.internal_error", "A coordinator transition failed.", ex));
                RecoverFromFault(now);
            }
            exit = Leave();
        }
        finally
        {
            gate.Release();
            if (exit is { } captured) Complete(captured);
        }
        if (failure != null) ExceptionDispatchInfo.Throw(failure);
    }

    private void Complete(Exit exit)
    {
        foreach (var cts in exit.RetiredOperations)
        {
            try { cts.Cancel(); }
            catch (AggregateException ex) { log.Warn("playback.cancel_failed", "A cancellation callback threw.", ex); }
            finally { cts.Dispose(); }
        }
        PumpEngineCommands();
        foreach (var entry in exit.Logs) Write(entry);
        if (exit.Changed is { } changed) Notify(changed, exit.Sequence);
    }

    private void Notify(PlaybackSnapshot value, long sequence)
    {
        lock (notifyLock)
        {
            if (sequence <= notifiedSequence) return;
            notifiedSequence = sequence;
            try { SnapshotChanged?.Invoke(this, value); }
            catch (Exception ex) { log.Error("playback.snapshot_handler_failed", "A SnapshotChanged handler threw.", ex); }
        }
    }

    private void PumpEngineCommands()
    {
        while (!engineCommands.IsEmpty)
        {
            // One pumping thread at a time; a command enqueued while another thread pumps is picked up by that thread's loop.
            if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return;
            try
            {
                while (engineCommands.TryDequeue(out var command)) Invoke(command);
            }
            finally
            {
                Volatile.Write(ref pumping, 0);
            }
        }
    }

    private void Invoke(EngineCommand command)
    {
        Task task;
        try
        {
            task = command.Kind switch
            {
                CommandKind.Start => engine.StartAsync(command.Source!, command.Volume, command.Token),
                CommandKind.Stop => engine.StopAsync(CancellationToken.None),
                _ => engine.SetVolumeAsync(command.Volume, CancellationToken.None)
            };
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }
        if (task.IsCompletedSuccessfully) command.Done.TrySetResult();
        else _ = ObserveAsync(command, task);
    }

    private async Task ObserveAsync(EngineCommand command, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded or shut down: the engine stopped that session silently (contract).
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested)
        {
            if (command.Kind == CommandKind.Start)
            {
                log.Error("playback.engine_error", $"Engine start failed for session {command.SessionId}.", ex);
                Signal(new EngineSignal(SignalKind.Failed, command.SessionId, default, PlaybackFailureKind.Unknown, null));
            }
            else
            {
                log.Warn("playback.engine_error", $"Engine {command.Kind} failed.", ex);
            }
        }
        catch (Exception)
        {
            // Shutting down: nothing can act on it any more.
        }
        finally
        {
            command.Done.TrySetResult();
        }
    }

    private void OnEngineStateChanged(object? sender, PlaybackEngineStateChangedEventArgs e) =>
        Signal(new EngineSignal(SignalKind.State, e.SessionId, e.State, default, null));

    private void OnEngineFailed(object? sender, PlaybackEngineFailedEventArgs e) =>
        Signal(new EngineSignal(SignalKind.Failed, e.SessionId, default, e.Kind, e.Diagnostic));

    private void OnMetadataChanged(object? sender, EventArgs e) =>
        Signal(new EngineSignal(SignalKind.Metadata, 0, default, default, null));

    private void Signal(EngineSignal signal)
    {
        engineSignals.Enqueue(signal);
        _ = DrainEngineSignalsAsync();
    }

    private async Task DrainEngineSignalsAsync()
    {
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            Exit? exit = null;
            try
            {
                var now = monotonicClock.GetTimestamp();
                while (engineSignals.TryDequeue(out var signal))
                {
                    if (disposed) continue;
                    try
                    {
                        ApplySignal(signal, now);
                    }
                    catch (Exception ex)
                    {
                        // Same fault rule as RunAsync; the remaining signals are still applied.
                        pendingLogs.Add(new(LogLevel.Error, "playback.internal_error", "Engine event handling failed.", ex));
                        RecoverFromFault(now);
                    }
                }
                if (!disposed) exit = Leave();
            }
            finally
            {
                gate.Release();
            }
            if (exit is { } captured) Complete(captured);
        }
        catch (Exception ex)
        {
            log.Error("playback.internal_error", "Engine event handling failed.", ex);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        Exit exit;
        var stop = EngineCommand.Stop();
        try
        {
            disposed = true;
            isActive = false;
            isPlaying = false;
            retiredOperations.Add(operationCts);
            operationGeneration++;
            currentSessionId = 0;
            engineSessionLive = false;
            ClearRetry();
            ClearRecovery();
            status = PlaybackStatus.Disposing;
            engineCommands.Enqueue(stop);
            exit = Leave();
        }
        finally
        {
            gate.Release();
        }
        try { lifetime.Cancel(); }
        catch (AggregateException ex) { log.Warn("playback.cancel_failed", "A cancellation callback threw during shutdown.", ex); }
        Complete(exit);
        await stop.Done.Task.ConfigureAwait(false);
        engine.StateChanged -= OnEngineStateChanged;
        engine.Failed -= OnEngineFailed;
        if (metadata != null) metadata.MetadataChanged -= OnMetadataChanged;
        try
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error("playback.engine_error", "Engine disposal failed.", ex);
        }
        lifetime.Dispose();
        log.Info("playback.disposed", "Playback coordinator disposed; the engine is stopped and released.");
    }

    private enum SignalKind { State, Failed, Metadata }

    private readonly record struct EngineSignal(SignalKind Kind, long SessionId, PlaybackEngineState State, PlaybackFailureKind FailureKind, string? Diagnostic);

    private enum CommandKind { Start, Stop, SetVolume }

    private sealed class EngineCommand
    {
        private EngineCommand(CommandKind kind, long sessionId, StreamSource? source, double volume, CancellationToken token)
        {
            Kind = kind;
            SessionId = sessionId;
            Source = source;
            Volume = volume;
            Token = token;
        }

        public CommandKind Kind { get; }
        public long SessionId { get; }
        public StreamSource? Source { get; }
        public double Volume { get; }
        public CancellationToken Token { get; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static EngineCommand Start(long sessionId, StreamSource source, double volume, CancellationToken token) => new(CommandKind.Start, sessionId, source, volume, token);
        public static EngineCommand Stop() => new(CommandKind.Stop, 0, null, 0, CancellationToken.None);
        public static EngineCommand SetVolume(double volume) => new(CommandKind.SetVolume, 0, null, volume, CancellationToken.None);
    }

    private enum LogLevel { Info, Warn, Error }

    private readonly record struct LogEntry(LogLevel Level, string Event, string Message, Exception? Exception = null);

    private readonly record struct Exit(PlaybackSnapshot? Changed, long Sequence, CancellationTokenSource[] RetiredOperations, LogEntry[] Logs);

    private void Write(LogEntry entry)
    {
        switch (entry.Level)
        {
            case LogLevel.Info: log.Info(entry.Event, entry.Message); break;
            case LogLevel.Warn: log.Warn(entry.Event, entry.Message, entry.Exception); break;
            default: log.Error(entry.Event, entry.Message, entry.Exception); break;
        }
    }
}
