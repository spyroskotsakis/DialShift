using System.Diagnostics;

namespace DialShift.Core.Playback;

/// <summary>
/// Monotonic time for elapsed durations: retry backoff, stall watchdog, stable-playback reset, wake settle and debounce,
/// and the tick-gap wake heuristic (brief 1 §4.1a). Wake-gap detection MUST use this, never <see cref="IClock.UtcNow"/>,
/// so wall-clock jumps cannot produce false wake detections.
/// </summary>
/// <remarks>
/// <b>Sleep-inclusive in production (D14).</b> The tick-gap heuristic only sees a sleep if the clock keeps counting
/// while the machine sleeps. The App composition root therefore injects a per-OS sleep-inclusive implementation from
/// the platform lane: on macOS <c>clock_gettime_nsec_np(CLOCK_MONOTONIC)</c> (<c>CLOCK_MONOTONIC_RAW</c> is equally
/// sleep-inclusive), and on Windows a sleep-inclusive source that is still to be verified natively. Core never P/Invokes,
/// so those implementations live in <c>DialShift.App</c>. <see cref="StopwatchMonotonicClock"/> stays the Core default for
/// tests and for contexts that do not need to observe sleep. One clock drives every coordinator timer; a sleep-inclusive
/// clock is safe for the retry, stall and stable timers because a detected wake retires the session and clears those
/// timers before they are evaluated in the same tick.
/// </remarks>
public interface IMonotonicClock
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
}

/// <summary>Default <see cref="IMonotonicClock"/> backed by <see cref="Stopwatch"/>: for tests and contexts that do not need to observe sleep (D14).</summary>
/// <remarks>
/// On macOS, <see cref="Stopwatch"/> reads <c>CLOCK_UPTIME_RAW</c>, which does not advance while the machine sleeps
/// (measured on the dev Mac: Stopwatch equals UPTIME_RAW and trails the sleep-inclusive <c>CLOCK_MONOTONIC</c> by the
/// total sleep time since boot, 548,087 s at the time). With this clock, the tick-gap wake heuristic therefore sees no gap
/// after a real sleep on macOS. Retry, stall and settle timing are unaffected. Production wake detection uses the OS wake
/// notification plus the App-layer sleep-inclusive clock described on <see cref="IMonotonicClock"/> (native call, so it
/// cannot live in Core).
/// </remarks>
public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public static StopwatchMonotonicClock Instance { get; } = new();

    private StopwatchMonotonicClock() { }

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => Stopwatch.GetElapsedTime(startingTimestamp, endingTimestamp);
}
