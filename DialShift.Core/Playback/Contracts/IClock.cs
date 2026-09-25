namespace DialShift.Core.Playback;

/// <summary>
/// Wall-clock time for schedules and persisted timestamps (brief 1 §4.1a). NTP, timezone and DST changes
/// are expected here. Never use it to measure elapsed time or detect sleep/wake — use <see cref="IMonotonicClock"/>.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production <see cref="IClock"/> backed by <see cref="DateTimeOffset.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock() { }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
