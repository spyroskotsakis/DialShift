namespace DialShift.Core;

/// <summary>
/// Per-running-session schedule dedup: decides whether the current occurrence is a new one to start.
/// </summary>
/// <remarks>
/// <para>An occurrence is identified by <see cref="Occurrence.Key"/> (the slot's own zone wall time), so the same start
/// never fires twice without <c>force</c>, even across a repeated DST hour or a change of the computer's zone.</para>
/// <para>A different current occurrence fires when it is not older than the last one fired or held. An older one fires
/// only if it became current while the wall clock ran forward: that is a slot edit, a zone edit, or a zone change moving
/// starts backwards (QA-N8), or a slot starting after a backward clock correction (CF-02). When the wall clock itself
/// moved backwards (clock correction, DST fall-back), the older occurrence that becomes current is adopted silently:
/// last week's start is never replayed. <c>At</c> values are compared only when taken in the same computer zone.</para>
/// <para>Pass the same <c>localZone</c> as <see cref="Scheduler.Evaluate"/> (QA-B2).</para>
/// </remarks>
public sealed class ScheduleSession
{
    /// <summary>The occurrence last fired or held, and the computer zone its <c>At</c> was computed in.</summary>
    private Occurrence? last;
    private string? lastZone;

    /// <summary>What the previous check saw: the current occurrence, the wall clock, and the computer zone.</summary>
    private Occurrence? seen;
    private DateTime seenNow;
    private string? seenZone;

    /// <summary>A manual choice holds the current occurrence so the next check does not override it.</summary>
    public void HoldCurrent(Settings settings, DateTime now, TimeZoneInfo? localZone = null)
    {
        var zone = ZoneId(localZone);
        var current = Scheduler.Evaluate(settings, now, localZone).Current;
        // Never move the hold back to an older occurrence of the same clock.
        if (last == null || (current != null && (lastZone != zone || current.At >= last.At))) Record(current, zone);
        Observe(current, now, zone);
    }

    /// <summary>The current occurrence if it is new (or always with <paramref name="force"/>); otherwise null. Inert while the schedule is off.</summary>
    public Occurrence? TakeChange(Settings settings, DateTime now, bool force = false, TimeZoneInfo? localZone = null)
    {
        if (!settings.ScheduleEnabled) return null;
        var zone = ZoneId(localZone);
        var current = Scheduler.Evaluate(settings, now, localZone).Current;
        var fire = current != null && (force || IsNew(current, now, zone));
        if (current == null || fire) Record(current, zone);
        Observe(current, now, zone);
        return fire ? current : null;
    }

    private bool IsNew(Occurrence current, DateTime now, string zone)
    {
        if (last == null) return true;
        if (current.Key == last.Key) return false;
        if (lastZone == zone && current.At >= last.At) return true;
        var clockMovedBack = seenZone == zone && now < seenNow;
        return current.Key != seen?.Key && !clockMovedBack;
    }

    private void Record(Occurrence? occurrence, string zone) => (last, lastZone) = (occurrence, zone);

    private void Observe(Occurrence? current, DateTime now, string zone) => (seen, seenNow, seenZone) = (current, now, zone);

    /// <summary>Identity of the computer zone the <c>At</c> values are computed in; <see cref="TimeZoneInfo.Local"/> when not injected.</summary>
    private static string ZoneId(TimeZoneInfo? localZone) => (localZone ?? TimeZoneInfo.Local).Id;
}
