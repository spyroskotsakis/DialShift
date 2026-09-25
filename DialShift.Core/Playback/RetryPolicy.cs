namespace DialShift.Core.Playback;

/// <summary>
/// Pure retry/fallback rules for <see cref="PlaybackCoordinator"/>, characterized from the legacy
/// <c>RadioController</c> (docs/acceptance-matrix.md §5.1). No state and no clocks: the coordinator measures elapsed
/// time on <see cref="IMonotonicClock"/> and asks these functions what to do.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Consecutive failures after which a configured fallback station is tried.</summary>
    public const int FallbackAfterFailures = 3;

    /// <summary>While a fallback station plays, the desired (primary) station is re-tried this long after the fallback reached Playing.</summary>
    public static readonly TimeSpan PrimaryRecheckInterval = TimeSpan.FromSeconds(120);

    /// <summary>Continuous Playing on the primary station for this long resets the failure count. Never applies to the fallback.</summary>
    public static readonly TimeSpan StablePlaybackReset = TimeSpan.FromSeconds(60);

    /// <summary>An active session that spends strictly more than this outside engine Playing is failed as <see cref="PlaybackFailureKind.Stalled"/>. Also the connect timeout.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Backoff before the next attempt after the <paramref name="consecutiveFailures"/>-th consecutive failure: 3 s, 6 s, then 30 s forever (never gives up).</summary>
    public static TimeSpan DelayAfterFailure(int consecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveFailures, 1);
        return TimeSpan.FromSeconds(consecutiveFailures < 3 ? consecutiveFailures * 3 : 30);
    }

    /// <summary>Whole seconds shown in "retry in Ns": <c>ceil(delay − elapsed)</c>, never negative.</summary>
    public static int SecondsRemaining(TimeSpan delay, TimeSpan elapsed) =>
        (int)Math.Max(0, Math.Ceiling((delay - elapsed).TotalSeconds));

    /// <summary>
    /// Station for the attempt that follows a due retry. The fallback is used when there have been at least
    /// <see cref="FallbackAfterFailures"/> consecutive failures, a fallback is configured, exists, differs from
    /// <paramref name="desired"/>, and is not the station currently being retried. Otherwise the desired station.
    /// This alternates exactly like today: a failing primary goes to the fallback, a failing fallback (or a due primary
    /// re-check while the fallback plays) goes back to the primary.
    /// </summary>
    public static (Station Station, bool IsFallback) SelectRetryTarget(Settings settings, Station desired, int consecutiveFailures, bool onFallback)
    {
        var backup = settings.Stations.FirstOrDefault(s => s.Id == settings.FallbackStationId && s.Id != desired.Id);
        var useFallback = consecutiveFailures >= FallbackAfterFailures && backup != null && !onFallback;
        return (useFallback ? backup! : desired, useFallback);
    }
}
