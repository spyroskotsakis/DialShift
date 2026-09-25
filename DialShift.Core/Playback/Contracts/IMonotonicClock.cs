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
public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public static StopwatchMonotonicClock Instance { get; } = new();

    private StopwatchMonotonicClock() { }

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => Stopwatch.GetElapsedTime(startingTimestamp, endingTimestamp);
}
