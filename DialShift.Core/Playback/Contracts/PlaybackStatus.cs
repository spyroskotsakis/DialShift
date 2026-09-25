namespace DialShift.Core.Playback;

/// <summary>Coordinator states (brief 1 §5.1). Per-state event acceptance is documented on <see cref="IPlaybackCoordinator"/>.</summary>
public enum PlaybackStatus
{
    /// <summary>No play intent and the schedule is off (or has no upcoming slot).</summary>
    Stopped,
    /// <summary>No play intent; the schedule is on and will start playback at <see cref="PlaybackSnapshot.Next"/>.</summary>
    ScheduledWaiting,
    /// <summary>First attempt after a user play or a schedule change: engine session opening/buffering.</summary>
    Connecting,
    /// <summary>The engine reported audible playback (primary or fallback — see <see cref="PlaybackSnapshot.IsFallback"/>).</summary>
    Playing,
    /// <summary>A retry, fallback switch, primary re-check or wake-recovery attempt is opening.</summary>
    Reconnecting,
    /// <summary>
    /// The last attempt failed and a retry is scheduled (<see cref="PlaybackSnapshot.RetryInSeconds"/> is set).
    /// Play intent is retained; this is never terminal — the coordinator keeps retrying like today's app.
    /// </summary>
    Failed,
    /// <summary>Wake detected: stale work was cancelled and recovery is waiting for the network to settle.</summary>
    SuspendedBySystem,
    /// <summary>Terminal. Every input is ignored.</summary>
    Disposing
}

/// <summary>
/// Immutable view of the coordinator the UI observes. The UI never owns playback state; it renders this record
/// and sends commands through <see cref="IPlaybackCoordinator"/>.
/// </summary>
/// <param name="Status">State-machine state.</param>
/// <param name="DesiredStationId">Station the user or schedule asked for (kept while playing a fallback).</param>
/// <param name="DesiredStationName">Name of <paramref name="DesiredStationId"/>.</param>
/// <param name="CurrentStationId">Station the engine is actually opening/playing (the fallback while <paramref name="IsFallback"/>).</param>
/// <param name="CurrentStationName">Name of <paramref name="CurrentStationId"/>; the player title and tray tooltip.</param>
/// <param name="IsActive">Play intent: true from play until user stop / station removal. Drives the Play/Pause label.</param>
/// <param name="IsPlaying">True only while the engine reports <see cref="PlaybackEngineState.Playing"/> for the current session.</param>
/// <param name="IsFallback">True while the fallback station is being opened or played instead of the desired one.</param>
/// <param name="StatusText">Status line, e.g. "Live broadcast", "Stream unavailable · retry in 6s".</param>
/// <param name="TrackText">Secondary line: now-playing title, station tag, or guidance text.</param>
/// <param name="RetryInSeconds">Whole seconds until the next automatic attempt while <see cref="PlaybackStatus.Failed"/>; otherwise null.</param>
/// <param name="Next">
/// Upcoming schedule occurrence ("UP NEXT") when the schedule is on, else null. <c>Next.At</c> is a computer-local
/// wall-clock time (never UTC); <c>Next.Entry</c> is a live settings reference and must be treated as read-only.
/// </param>
/// <param name="NextStationName">Name of the station <paramref name="Next"/> will switch to.</param>
/// <param name="Volume">Current volume 0–100 (mirrors <c>Settings.Volume</c>; 0 = muted).</param>
public sealed record PlaybackSnapshot(
    PlaybackStatus Status,
    Guid? DesiredStationId,
    string? DesiredStationName,
    Guid? CurrentStationId,
    string? CurrentStationName,
    bool IsActive,
    bool IsPlaying,
    bool IsFallback,
    string StatusText,
    string TrackText,
    int? RetryInSeconds,
    Occurrence? Next,
    string? NextStationName,
    int Volume)
{
    /// <summary>Snapshot before anything has played, matching today's first-launch texts.</summary>
    public static PlaybackSnapshot Initial(int volume) => new(
        PlaybackStatus.Stopped, null, null, null, null,
        IsActive: false, IsPlaying: false, IsFallback: false,
        StatusText: "Ready when you are",
        TrackText: "Choose a station and make yourself at home.",
        RetryInSeconds: null, Next: null, NextStationName: null, Volume: volume);
}
