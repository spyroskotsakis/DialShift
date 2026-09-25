namespace DialShift.Core.Playback;

/// <summary>
/// UI-agnostic playback + schedule coordinator consumed by view models, the tray, and tests (brief 1 §4.1, §5).
/// It replaces the legacy <c>RadioController</c> surface (deleted in <c>4ef9515</c>) one-to-one but is async, never touches a UI dispatcher,
/// and owns retry/fallback/schedule/wake/cancellation policy. The implementation serializes every transition
/// through one async gate that is never held across an await, and validates an operation generation before
/// any delayed work acts.
/// </summary>
/// <remarks>
/// <para><b>Inputs → states (brief 1 §5.2).</b> Unless stated otherwise every input is ignored in
/// <see cref="PlaybackStatus.Disposing"/>.</para>
/// <list type="bullet">
/// <item><b>UserPlay</b> (<see cref="PlayAsync"/>, <see cref="ToggleAsync"/> when inactive, <see cref="NextStationAsync"/>):
/// any state → <see cref="PlaybackStatus.Connecting"/>; cancels in-flight work, resets failures and fallback, holds the
/// current schedule occurrence so the next tick does not override the manual choice.</item>
/// <item><b>UserStop</b> (<see cref="StopAsync"/>, <see cref="ToggleAsync"/> when active): any state →
/// <see cref="PlaybackStatus.ScheduledWaiting"/> if the schedule is on, else <see cref="PlaybackStatus.Stopped"/>;
/// cancels retry, fallback and wake-recovery work; holds the current occurrence. Idempotent.</item>
/// <item><b>ScheduleDue</b> (a new occurrence from <see cref="OnTickAsync"/>, or any current occurrence from the forced
/// <see cref="StartScheduleAsync"/>/<see cref="RefreshScheduleAsync"/>): any state → <see cref="PlaybackStatus.Connecting"/>
/// for the slot's station — including after a user stop (a stop holds only within the current slot). While
/// <see cref="PlaybackStatus.SuspendedBySystem"/> it is folded into the wake recovery's schedule check (exactly one reconnect).</item>
/// <item><b>Engine Playing</b> (current session only): <see cref="PlaybackStatus.Connecting"/>/<see cref="PlaybackStatus.Reconnecting"/>
/// → <see cref="PlaybackStatus.Playing"/>; ignored elsewhere.</item>
/// <item><b>PlaybackError / PlaybackEnded / stall watchdog</b> (current session only): <see cref="PlaybackStatus.Connecting"/>,
/// <see cref="PlaybackStatus.Playing"/>, <see cref="PlaybackStatus.Reconnecting"/> → <see cref="PlaybackStatus.Failed"/> with backoff;
/// ignored in <see cref="PlaybackStatus.Stopped"/>, <see cref="PlaybackStatus.ScheduledWaiting"/>, <see cref="PlaybackStatus.Failed"/>
/// (one failure per attempt) and <see cref="PlaybackStatus.SuspendedBySystem"/>. Stale-session events are always ignored.</item>
/// <item><b>RetryDue</b> (internal, generation-checked): <see cref="PlaybackStatus.Failed"/> → <see cref="PlaybackStatus.Reconnecting"/>
/// (desired station, or the fallback after three consecutive failures); primary re-check after 120 s on a playing fallback:
/// <see cref="PlaybackStatus.Playing"/> → <see cref="PlaybackStatus.Reconnecting"/>. A retry whose generation is stale exits harmlessly.</item>
/// <item><b>WakeDetected</b> (<see cref="NotifyWakeAsync"/>, or a ≥ 15 s gap between two <see cref="OnTickAsync"/> calls measured
/// on the injected <see cref="IMonotonicClock"/>, which must be sleep-inclusive in production, D14): active states →
/// <see cref="PlaybackStatus.SuspendedBySystem"/> → settle 2 s (OQ-7) → schedule check → exactly one reconnect: a new slot
/// opens as <see cref="PlaybackStatus.Connecting"/>, otherwise the desired station as <see cref="PlaybackStatus.Reconnecting"/>
/// (normal retry policy on failure); inactive states → schedule check only (a stop still holds within the same slot); already
/// <see cref="PlaybackStatus.SuspendedBySystem"/> → ignored (idempotent). <b>Wake debounce (D15):</b> in every state, a wake
/// detected within 10 s of the last accepted wake or of the last completed recovery is ignored, so an OS notification and
/// a tick gap for the same wake produce one recovery.</item>
/// <item><b>SettingsChanged</b> (<see cref="NotifySettingsChangedAsync"/>): any state; revalidates stations, fallback and volume.</item>
/// <item><b>Dispose</b>: any state → <see cref="PlaybackStatus.Disposing"/> (terminal); stops the engine, then disposes it exactly
/// once, because the coordinator owns the engine it was given (D17: the composition root must not dispose the engine itself;
/// D23: the engine must be fresh, never started, when the coordinator receives it);
/// no event revives playback.</item>
/// </list>
/// <para><b>Threading (D18).</b> <c>Settings</c> is plain mutable data, so the host mutates it and calls the commands
/// (<see cref="PlayAsync"/>, <see cref="ToggleAsync"/>, <see cref="StopAsync"/>, <see cref="NextStationAsync"/>,
/// <see cref="SetVolumeAsync"/>, <see cref="StartScheduleAsync"/>, <see cref="RefreshScheduleAsync"/>,
/// <see cref="NotifySettingsChangedAsync"/>, <see cref="ForgetStationAsync"/>) and the <see cref="OnTickAsync"/> loop on the
/// UI thread only. Those members run their transition on the caller's synchronization context, so their
/// <c>Settings</c> reads and writes never race the host's edits. <see cref="NotifyWakeAsync"/>, <see cref="Snapshot"/> and
/// <see cref="IAsyncDisposable.DisposeAsync"/> may be called from any thread: they read only scalar settings.
/// <see cref="SnapshotChanged"/> is raised on an arbitrary thread, only when the snapshot value changed, and never with an
/// older snapshot after a newer one. Handlers must return quickly and must not block (marshal with a post, never a
/// synchronous invoke onto the UI thread). The coordinator does not save settings — callers persist <c>Settings</c> after
/// commands exactly as today (volume, last station).</para>
/// <para><b>Ticking.</b> The coordinator owns no timer. The orchestration layer runs a 1 s <c>PeriodicTimer</c> loop on the UI
/// thread that calls <see cref="OnTickAsync"/>; tests call it directly with fake <see cref="IClock"/>/<see cref="IMonotonicClock"/>.</para>
/// </remarks>
public interface IPlaybackCoordinator : IAsyncDisposable
{
    /// <summary>Latest immutable state. Thread-safe to read at any time.</summary>
    PlaybackSnapshot Snapshot { get; }

    /// <summary>Raised with the new snapshot whenever it changes.</summary>
    event EventHandler<PlaybackSnapshot>? SnapshotChanged;

    /// <summary>UserPlay for a station by id (manual selection; sets <c>Settings.LastStationId</c>). Unknown ids are ignored.</summary>
    Task PlayAsync(Guid stationId);

    /// <summary>UserStop when active; otherwise UserPlay of the desired station, else the last station, else the first station.</summary>
    Task ToggleAsync();

    /// <summary>UserStop (the UI's "Pause"): keeps the desired station for a later play.</summary>
    Task StopAsync();

    /// <summary>UserPlay of the station after the desired (else current) one, wrapping; the first station when neither is known.</summary>
    Task NextStationAsync();

    /// <summary>Clamps to 0–100, updates <c>Settings.Volume</c>, forwards <c>volume / 100.0</c> to the engine. 0 mutes.</summary>
    Task SetVolumeAsync(int volume);

    /// <summary>Startup catch-up: forced schedule evaluation that plays the current slot if the schedule is on.</summary>
    Task StartScheduleAsync();

    /// <summary>Forced schedule re-evaluation after schedule edits or toggling "Follow schedule"; replays the current slot when the schedule is on.</summary>
    Task RefreshScheduleAsync();

    /// <summary>1 Hz heartbeat: wake-gap check, schedule check, retry/fallback timers, stall watchdog, stable-playback reset, snapshot refresh.</summary>
    Task OnTickAsync(CancellationToken cancellationToken);

    /// <summary>WakeDetected from the OS power service. Idempotent and rate-limited (10 s debounce, D15); safe to call from any thread and alongside the tick-gap heuristic.</summary>
    Task NotifyWakeAsync();

    /// <summary>
    /// SettingsChanged: stations, URLs, fallback, or volume were edited. Stops playback of a station that no longer exists,
    /// reconnects (as a manual play) when the active desired station's URL changed, and applies a new fallback to the next
    /// failure. Does not force a schedule replay — call <see cref="RefreshScheduleAsync"/> for that.
    /// </summary>
    Task NotifySettingsChangedAsync();

    /// <summary>A station is being deleted: stops playback if it is desired or current and clears both references.</summary>
    Task ForgetStationAsync(Guid stationId);
}
