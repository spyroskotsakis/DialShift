using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using DialShift.Core.Playback;

namespace DialShift.Core;

/// <summary>
/// Pure schedule math: slot parsing, per-slot time zones, occurrence evaluation and conflict hints
/// (brief 2 §4–§7, docs/schedule-timezone-research.md).
/// </summary>
/// <remarks>
/// <para><b>Two paths.</b> A slot without a zone (or with an id this computer cannot resolve) takes the zero-conversion
/// path: its <c>Time</c>/<c>Days</c> are computer-local wall-clock values, exactly as before timezones existed, and
/// <see cref="TimeZoneInfo.Local"/> is never consulted (QA-B1). A zoned slot is laid out on its own zone's calendar
/// (day of week and the ±7-day window are anchored in that zone) and each start is converted to computer-local wall
/// time with <see cref="ToComputerLocal"/>. Everything downstream compares only computer-local <see cref="Occurrence.At"/>.</para>
/// <para><b>Conversions (QA-B2).</b> Wall-clock values are normalized to <see cref="DateTimeKind.Unspecified"/> and only
/// the 3-argument <see cref="TimeZoneInfo.ConvertTime(DateTime, TimeZoneInfo, TimeZoneInfo)"/> overload is used, so an
/// injected <c>localZone</c> is honored and the host zone never leaks in.</para>
/// <para><b>Zone data is a snapshot (QA-N4, D12).</b> Resolved zones are cached for the process lifetime, and
/// <see cref="TimeZoneInfo"/> instances capture the OS tzdata when created: an OS zone or tzdata change needs a restart.</para>
/// </remarks>
public static class Scheduler
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new(StringComparer.Ordinal);
    private static ConversionMemo memo = new(DateTime.MinValue);

    /// <summary>Upper bound on memoized conversions per local day; beyond it conversions are computed, not stored.</summary>
    private const int MemoCapacity = 4096;

    /// <summary>
    /// Receives <c>schedule.zone_unknown</c> once per unresolvable id. Core has no logger of its own; the composition
    /// root sets this at startup. Defaults to discarding.
    /// </summary>
    public static IAppLog Log { get; set; } = NullAppLog.Instance;

    /// <summary>The canonical stored and displayed slot time: 24-hour <c>HH:mm</c> with a literal colon (D54).</summary>
    private const string CanonicalTimeFormat = "HH':'mm";

    /// <summary>
    /// Formats accepted by <see cref="TryTime"/>: the canonical <c>HH:mm</c>, and <c>HH.mm</c>. The second is what a
    /// culture-dependent <c>ToString("HH:mm")</c> writes on every culture whose time separator is <c>.</c> (27 cultures in
    /// .NET 10 ICU data, among them da-DK, fi-FI, sv-FI, id-ID and en-DK; no culture produces any other separator), so
    /// settings saved that way by earlier builds still play (D54).
    /// </summary>
    private static readonly string[] AcceptedTimeFormats = [CanonicalTimeFormat, "HH'.'mm"];

    /// <summary>
    /// Parses a stored or typed slot time: exactly two-digit hours and minutes separated by <c>:</c> or <c>.</c>, parsed
    /// culture-invariantly, with no surrounding whitespace, seconds or 24:00 (CT-SCH-06). Null or anything else is false,
    /// and such a slot never fires.
    /// </summary>
    public static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, AcceptedTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>
    /// The one canonical text for a slot time, <c>HH:mm</c> in the invariant culture whatever the current culture is.
    /// Code that writes <see cref="ScheduleEntry.Time"/> formats with this, never with its own format string (D54).
    /// </summary>
    public static string FormatTime(TimeOnly time) => time.ToString(CanonicalTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves a stored zone id. Null, empty and whitespace are <see cref="ZoneResolution.Local"/> (<paramref name="zone"/>
    /// null, the zero-conversion path, QA-B1). Otherwise the trimmed id is looked up IANA-first, with a
    /// <see cref="TimeZoneInfo.TryConvertIanaIdToWindowsId(string, out string?)"/> fallback (§4.5/§6).
    /// An id that does not resolve is <see cref="ZoneResolution.Unknown"/> with a null <paramref name="zone"/>: the slot
    /// falls back to local time, the UI shows "(unknown zone)", and <see cref="Log"/> gets one warning per id. Never throws.
    /// </summary>
    /// <remarks>Results are cached per trimmed id; compare zones by <see cref="TimeZoneInfo.Id"/>, not by reference.</remarks>
    public static ZoneResolution TryResolveZone(string? id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id)) return ZoneResolution.Local;
        var key = id.Trim();
        if (!Zones.TryGetValue(key, out zone))
        {
            var found = Find(key);
            if (!Zones.TryAdd(key, found)) zone = Zones[key];
            else if ((zone = found) is null)
                Log.Warn("schedule.zone_unknown", $"Time zone '{Truncate(key)}' is not available on this computer; its slots use local time.");
        }
        return zone is null ? ZoneResolution.Unknown : ZoneResolution.Resolved;
    }

    /// <summary>The zone a stored id resolves to, or null for local time (no zone set, or an unknown id; see <see cref="TryResolveZone"/>).</summary>
    public static TimeZoneInfo? ResolveZone(string? id)
    {
        TryResolveZone(id, out var zone);
        return zone;
    }

    /// <summary>
    /// Converts a slot's wall-clock start in <paramref name="zone"/> to computer-local wall time in <paramref name="local"/>.
    /// The result's <c>Kind</c> is <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Fall-back overlap (QA-B3):</b> an ambiguous time fires once, at the earlier (daylight) instant,
    /// <c>zoneWall − GetAmbiguousTimeOffsets(zoneWall).Max()</c>. (<c>ConvertTime</c> alone would pick the later, standard one.)</para>
    /// <para><b>Spring-forward gap (QA-N7):</b> a time that never occurs fires at the first valid instant after the gap,
    /// the same instant the local path fires at today. Shifting by the gap length instead would fire later than today.
    /// This holds for gaps at midnight (the start can land on the next calendar day) and for whole-day gaps.</para>
    /// </remarks>
    public static DateTime ToComputerLocal(DateTime zoneWall, TimeZoneInfo zone, TimeZoneInfo local) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(ToUtc(zoneWall, zone), TimeZoneInfo.Utc, local), DateTimeKind.Unspecified);

    /// <summary>
    /// The latest start at or before <paramref name="now"/> (<c>Current</c>) and the earliest start after it (<c>Next</c>)
    /// over enabled slots whose station exists. Ties go to the smaller <see cref="ScheduleEntry.Id"/>.
    /// </summary>
    /// <param name="now">Computer-local wall-clock time. Its <c>Kind</c> is ignored for conversion (QA-B2).</param>
    /// <param name="localZone">The computer's zone, used only for zoned slots; <see cref="TimeZoneInfo.Local"/> when null.</param>
    /// <remarks>
    /// A slot without a zone is local wall-clock arithmetic, byte-identical to before timezones (including
    /// <c>At.Kind</c>, which follows <paramref name="now"/>): a repeated DST hour has the same key and does not fire twice;
    /// a skipped hour is caught up on the first tick after the clock jump, like wake from sleep.
    /// </remarks>
    public static (Occurrence? Current, Occurrence? Next) Evaluate(Settings settings, DateTime now, TimeZoneInfo? localZone = null)
    {
        var ids = settings.Stations.Select(s => s.Id).ToHashSet();
        var occurrences = new List<Occurrence>();
        var clock = new LocalClock(now, localZone);
        foreach (var entry in settings.Schedule.Where(e => e.Enabled && ids.Contains(e.StationId)))
            AddStarts(entry, ref clock, occurrences);
        return (occurrences.Where(o => o.At <= now).OrderByDescending(o => o.At).ThenBy(o => o.Entry.Id).FirstOrDefault(),
            occurrences.Where(o => o.At > now).OrderBy(o => o.At).ThenBy(o => o.Entry.Id).FirstOrDefault());
    }

    /// <summary>
    /// The next start of one slot after <paramref name="now"/>, in computer-local time, for a schedule row's
    /// "zone + next local fire time" (QA-N6). Ignores <see cref="ScheduleEntry.Enabled"/> and whether the station exists;
    /// null when the time does not parse or no day is selected.
    /// </summary>
    public static Occurrence? NextFor(ScheduleEntry entry, DateTime now, TimeZoneInfo? localZone = null)
    {
        var occurrences = new List<Occurrence>();
        var clock = new LocalClock(now, localZone);
        AddStarts(entry, ref clock, occurrences);
        return occurrences.Where(o => o.At > now).MinBy(o => o.At);
    }

    /// <summary>
    /// Whether an enabled <paramref name="candidate"/> starts at the same time, on an overlapping day, in the same zone as
    /// another enabled entry. Editing the same entry (same <see cref="ScheduleEntry.Id"/>) is allowed.
    /// </summary>
    /// <remarks>
    /// Times are compared as parsed by <see cref="TryTime"/>, so <c>08:30</c> and <c>08.30</c> are the same time (D54). A
    /// time that does not parse never conflicts, because that slot never fires.
    /// Zones are normalized (whitespace means local, ids are trimmed), resolved, and compared by resolved
    /// <see cref="TimeZoneInfo.Id"/>, with local for no zone or an unknown one (QA-N5). This is a hint, not an exact
    /// "same instant" rule: distinct ids that share an instant (Europe/Athens and Europe/Helsinki, Asia/Kolkata and
    /// Asia/Calcutta) or that coincide only part of the year (UTC and Europe/London), and a zoned slot that happens to
    /// convert onto a local one, are not reported.
    /// </remarks>
    public static bool Conflicts(IEnumerable<ScheduleEntry> entries, ScheduleEntry candidate)
    {
        if (!candidate.Enabled || !TryTime(candidate.Time, out var time)) return false;
        var zone = ZoneKey(candidate.TimeZone);
        return entries.Any(e => e.Id != candidate.Id && e.Enabled && TryTime(e.Time, out var other) && other == time && e.Days.Intersect(candidate.Days).Any()
            && string.Equals(ZoneKey(e.TimeZone), zone, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Memoized conversions for the current local day: <c>(Day, Count)</c>. Test hook for QA-N4 invalidation.</summary>
    internal static (DateTime Day, int Count) ConversionMemoState
    {
        get
        {
            var current = Volatile.Read(ref memo);
            return (current.Day, current.Count);
        }
    }

    private static string ZoneKey(string? id) => ResolveZone(string.IsNullOrWhiteSpace(id) ? "" : id.Trim())?.Id ?? "";

    private static void AddStarts(ScheduleEntry entry, ref LocalClock clock, List<Occurrence> occurrences)
    {
        if (!TryTime(entry.Time, out var time)) return;
        var resolution = TryResolveZone(entry.TimeZone, out var zone);
        if (zone is null)
        {
            // Zero-conversion path: unchanged since before timezones (QA-B1). `now` keeps its Kind here.
            for (var offset = -7; offset <= 7; offset++)
            {
                var date = clock.Now.Date.AddDays(offset);
                if (entry.Days.Contains(date.DayOfWeek)) occurrences.Add(new(entry, date.Add(time.ToTimeSpan())) { ZoneResolution = resolution });
            }
            return;
        }
        var nowInZone = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(clock.Utc, TimeZoneInfo.Utc, zone), DateTimeKind.Unspecified);
        for (var offset = -7; offset <= 7; offset++)
        {
            var zoneDate = nowInZone.Date.AddDays(offset);
            if (!entry.Days.Contains(zoneDate.DayOfWeek)) continue;
            var zoneWall = zoneDate.Add(time.ToTimeSpan());
            occurrences.Add(new(entry, ConvertMemoized(zoneWall, zone, clock.Zone, clock.Wall.Date)) { Zone = zone, ZoneWall = zoneWall, ZoneResolution = resolution });
        }
    }

    /// <summary>
    /// QA-N4: the per-candidate DST checks are the real cost of zoned slots (up to 15 per slot per Evaluate, which runs
    /// every tick plus every UP NEXT read). Conversions are memoized per (zone, local zone, zone wall time) for the current
    /// local day and dropped when the day changes. Zones are keyed by instance, which is exact even for custom zones that
    /// share an id; resolved zones are cached, so the instances are stable.
    /// </summary>
    private static DateTime ConvertMemoized(DateTime zoneWall, TimeZoneInfo zone, TimeZoneInfo local, DateTime day)
    {
        var current = Volatile.Read(ref memo);
        if (current.Day != day)
        {
            var fresh = new ConversionMemo(day);
            current = Interlocked.CompareExchange(ref memo, fresh, current) == current ? fresh : Volatile.Read(ref memo);
        }
        var key = new MemoKey(zone, local, zoneWall);
        if (current.Map.TryGetValue(key, out var at)) return at;
        at = ToComputerLocal(zoneWall, zone, local);
        if (current.Day == day && current.Count < MemoCapacity && current.Map.TryAdd(key, at)) Interlocked.Increment(ref current.Count);
        return at;
    }

    /// <summary>
    /// The instant (Kind Utc) a wall time in <paramref name="zone"/> names: the earliest instant that shows it (a repeated
    /// time is its earlier, daylight instant, QA-B3), or the first valid instant after the gap when none does (QA-N7).
    /// </summary>
    /// <remarks>
    /// <see cref="TimeZoneInfo.IsInvalidTime"/> and <see cref="TimeZoneInfo.IsAmbiguousTime"/> model only daylight-saving
    /// rules; they miss the gaps and overlaps of a base-offset change (Pacific/Apia skipped 2011-12-30 entirely, yet
    /// <c>IsInvalidTime</c> reports it valid). So, past the DST overlap case, each offset in force within a day of the wall
    /// time is tried and kept only if converting the resulting instant back gives the same wall time.
    /// </remarks>
    private static DateTime ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(wall)) return DateTime.SpecifyKind(wall - zone.GetAmbiguousTimeOffsets(wall).Max(), DateTimeKind.Utc);
        var nominal = DateTime.SpecifyKind(wall, DateTimeKind.Utc);
        DateTime? earliest = null;
        foreach (var offset in (ReadOnlySpan<TimeSpan>)[zone.GetUtcOffset(nominal.AddDays(-1)), zone.GetUtcOffset(nominal), zone.GetUtcOffset(nominal.AddDays(1))])
        {
            var instant = nominal - offset;
            if ((earliest is null || instant < earliest) && WallAt(instant, zone) == wall) earliest = instant;
        }
        return earliest ?? GapEnd(wall, zone);
    }

    private static DateTime WallAt(DateTime instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Utc, zone);

    /// <summary>
    /// The transition instant ending the spring-forward gap that contains <paramref name="wall"/>: the first instant whose
    /// wall time in <paramref name="zone"/> is later than <paramref name="wall"/>. UTC offsets stay within ±14 h, so
    /// <c>wall − 18 h</c> is before the gap and <c>wall + 18 h</c> after it; a binary search on whole seconds finds it exactly.
    /// </summary>
    private static DateTime GapEnd(DateTime wall, TimeZoneInfo zone)
    {
        var before = DateTime.SpecifyKind(wall.AddHours(-18), DateTimeKind.Utc);
        var seconds = 36L * 3600;
        long low = 0, high = seconds;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (WallAt(before.AddSeconds(middle), zone) > wall) high = middle;
            else low = middle;
        }
        return before.AddSeconds(high);
    }

    private static TimeZoneInfo? Find(string id)
    {
        if (TryFind(id) is { } zone) return zone;
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) ? TryFind(windowsId) : null;

        static TimeZoneInfo? TryFind(string candidate)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return null; }
        }
    }

    private static string Truncate(string id) => id.Length <= 64 ? id : id[..64] + "…";

    /// <summary>The <c>now</c> of one evaluation, with its instant computed at most once and only for zoned slots (QA-B1).</summary>
    private struct LocalClock(DateTime now, TimeZoneInfo? localZone)
    {
        private DateTime? utc;
        private TimeZoneInfo? zone = localZone;

        public DateTime Now { get; } = now;

        public DateTime Wall { get; } = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);

        public TimeZoneInfo Zone => zone ??= TimeZoneInfo.Local;

        public DateTime Utc => utc ??= ToUtc(Wall, Zone);
    }

    private sealed class ConversionMemo(DateTime day)
    {
        public int Count;

        public DateTime Day { get; } = day;

        public ConcurrentDictionary<MemoKey, DateTime> Map { get; } = new();
    }

    private readonly struct MemoKey(TimeZoneInfo zone, TimeZoneInfo local, DateTime zoneWall) : IEquatable<MemoKey>
    {
        private readonly TimeZoneInfo zone = zone;
        private readonly TimeZoneInfo local = local;
        private readonly DateTime zoneWall = zoneWall;

        public bool Equals(MemoKey other) => ReferenceEquals(zone, other.zone) && ReferenceEquals(local, other.local) && zoneWall == other.zoneWall;

        public override bool Equals(object? obj) => obj is MemoKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(zone), RuntimeHelpers.GetHashCode(local), zoneWall);
    }
}
