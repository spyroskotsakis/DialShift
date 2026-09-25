using DialShift.Core.Playback;

namespace DialShift.Tests.Fakes;

/// <summary>
/// Manually advanced monotonic clock. One timestamp unit is one <see cref="TimeSpan"/> tick
/// (<see cref="Frequency"/> units per second), so <c>GetElapsedTime(a, b) = TimeSpan.FromTicks(b - a)</c>.
/// It starts at a large non-zero value so code that treats 0 as "unset" is caught, and never moves backwards.
/// </summary>
public sealed class FakeMonotonicClock : IMonotonicClock
{
    public const long Frequency = TimeSpan.TicksPerSecond;
    public const long DefaultStart = 1_000_000 * Frequency;

    private long timestamp;

    public FakeMonotonicClock(long start = DefaultStart) => timestamp = start;

    public long GetTimestamp() => Interlocked.Read(ref timestamp);

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        Interlocked.Add(ref timestamp, by.Ticks);
    }
}
