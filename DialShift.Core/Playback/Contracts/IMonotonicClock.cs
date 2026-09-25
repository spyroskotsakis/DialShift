using System.Diagnostics;

namespace DialShift.Core.Playback;

/// <summary>
/// Monotonic time for elapsed durations: retry backoff, stall watchdog, stable-playback reset, and the
/// tick-gap wake heuristic (brief 1 §4.1a). Wake-gap detection MUST use this, never <see cref="IClock.UtcNow"/>,
/// so wall-clock jumps cannot produce false wake detections.
/// </summary>
public interface IMonotonicClock
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
}

/// <summary>Production <see cref="IMonotonicClock"/> backed by <see cref="Stopwatch"/>.</summary>
/// <remarks>
/// On macOS, <see cref="Stopwatch"/> reads <c>CLOCK_UPTIME_RAW</c>, which does not advance while the machine sleeps
/// (measured on the dev Mac: Stopwatch equals UPTIME_RAW and trails the sleep-inclusive <c>CLOCK_MONOTONIC</c> by the
/// total sleep time since boot). With this clock, the tick-gap wake heuristic therefore sees no gap after a real sleep on
/// macOS. Retry, stall and settle timing are unaffected. Wake detection on macOS needs the OS wake notification, or an
/// App-layer sleep-inclusive clock (native call, so it cannot live in Core).
/// </remarks>
public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public static StopwatchMonotonicClock Instance { get; } = new();

    private StopwatchMonotonicClock() { }

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => Stopwatch.GetElapsedTime(startingTimestamp, endingTimestamp);
}
