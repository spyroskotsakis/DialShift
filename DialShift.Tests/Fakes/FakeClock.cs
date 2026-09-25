using DialShift.Core.Playback;

namespace DialShift.Tests.Fakes;

/// <summary>Settable wall clock. Values are kept at offset zero; it may jump in either direction (NTP, manual change).</summary>
public sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    private readonly Lock gate = new();
    private DateTimeOffset now = RequireUtc(utcNow);

    public static FakeClock AtUtc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));

    public DateTimeOffset UtcNow
    {
        get { lock (gate) return now; }
        set { lock (gate) now = RequireUtc(value); }
    }

    public void Advance(TimeSpan by)
    {
        lock (gate) now += by;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("FakeClock.UtcNow must have a zero offset.", nameof(value));
}
