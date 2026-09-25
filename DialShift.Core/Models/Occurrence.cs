using System.Globalization;

namespace DialShift.Core;

/// <summary>How a <see cref="ScheduleEntry.TimeZone"/> value resolved on this computer.</summary>
public enum ZoneResolution
{
    /// <summary>No zone set (null, empty or whitespace): the slot follows computer-local time.</summary>
    Local,

    /// <summary>The id resolved to a time zone; the slot is converted from that zone to computer-local time.</summary>
    Resolved,

    /// <summary>An id is set but this computer cannot resolve it; the slot falls back to computer-local time (QA-B4, TZ-08).</summary>
    Unknown,
}

/// <summary>One start of a schedule slot, produced by <see cref="Scheduler"/>.</summary>
/// <remarks>
/// <para><b>Invariant (QA-N9):</b> <see cref="At"/> is a computer-local wall-clock time, never UTC. Never call
/// <c>ToLocalTime</c> or <c>ToUniversalTime</c> on it. On the local (zero-conversion) path <c>At.Kind</c> is the
/// <c>Kind</c> of the <c>now</c> passed to <see cref="Scheduler.Evaluate"/>, exactly as before timezones existed; a
/// converted <c>At</c> is always <see cref="DateTimeKind.Unspecified"/>. <c>Kind</c> is only a tag here.</para>
/// </remarks>
public sealed record Occurrence(ScheduleEntry Entry, DateTime At)
{
    private readonly DateTime? zoneWall;

    /// <summary>The zone the slot is defined in; null when it follows computer-local time (no zone, or an unknown one).</summary>
    public TimeZoneInfo? Zone { get; init; }

    /// <summary>How <see cref="ScheduleEntry.TimeZone"/> resolved; <see cref="ZoneResolution.Unknown"/> means "(unknown zone)".</summary>
    public ZoneResolution ZoneResolution { get; init; }

    /// <summary>
    /// The slot's own start in <see cref="Zone"/> (its date and <see cref="ScheduleEntry.Time"/>); equals <see cref="At"/>
    /// on the local path. For a slot inside a spring-forward gap this is the nominal time that never occurs, while
    /// <see cref="At"/> is the first valid instant after the gap.
    /// </summary>
    public DateTime ZoneWall { get => zoneWall ?? At; init => zoneWall = value; }

    /// <summary>
    /// Identity of this start: <c>"{Entry.Id}:yyyy-MM-ddTHH:mm"</c> of <see cref="ZoneWall"/>, suffixed with
    /// <c>"@{Zone.Id}"</c> for a zoned slot. Culture-invariant (CF-01). It names the slot's own wall time, so it is stable
    /// when the computer's zone changes (QA-N8). In-memory only; never persisted.
    /// </summary>
    public string Key => Zone is null
        ? string.Create(CultureInfo.InvariantCulture, $"{Entry.Id}:{ZoneWall:yyyy-MM-dd'T'HH':'mm}")
        : string.Create(CultureInfo.InvariantCulture, $"{Entry.Id}:{ZoneWall:yyyy-MM-dd'T'HH':'mm}@{Zone.Id}");
}
