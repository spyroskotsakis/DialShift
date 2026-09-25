using System.Globalization;
using System.Text.Json.Nodes;
using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>
/// Per-slot time zones (brief 2, docs/schedule-timezone-research.md §5.2, §7, §9.2; acceptance-matrix §6 TZ-* and QA-*).
/// Check names carry the §9.2 row ("Row n" = TZ-0n) or the QA id.
/// </summary>
/// <remarks>
/// <para><b>Machine-independent by construction:</b> every call gets an explicit <c>localZone</c>, every <c>now</c> is a
/// fixed <see cref="DateTimeKind.Unspecified"/> wall time (or an explicit other Kind when the Kind itself is under test),
/// and nothing here reads <see cref="TimeZoneInfo.Local"/> or <see cref="DateTime.Now"/>.</para>
/// <para><b>Zone data:</b> ids are IANA and resolve on macOS (tzdata) and on Windows (ICU mapping to the registry, .NET 10).
/// The fixed expectations use rules both data sets agree on. The two historical or irregular cases (Pacific/Apia
/// 2011, America/Santiago 2026) first check the host's data for the transition and SKIP, with the reason printed,
/// when it is missing.</para>
/// </remarks>
public static class TimezoneTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Athens = Find("Europe/Athens");
    private static readonly TimeZoneInfo NewYork = Find("America/New_York");
    private static readonly TimeZoneInfo Kolkata = Find("Asia/Kolkata");
    private static readonly TimeZoneInfo Kathmandu = Find("Asia/Kathmandu");
    private static readonly TimeZoneInfo Chatham = Find("Pacific/Chatham");
    private static readonly TimeZoneInfo Plus02 = TimeZoneInfo.CreateCustomTimeZone("Test/Plus02", TimeSpan.FromHours(2), "UTC+02 (test)", "UTC+02 (test)");

    private const DayOfWeek Mon = DayOfWeek.Monday, Tue = DayOfWeek.Tuesday, Wed = DayOfWeek.Wednesday, Thu = DayOfWeek.Thursday,
        Fri = DayOfWeek.Friday, Sat = DayOfWeek.Saturday, Sun = DayOfWeek.Sunday;

    private static readonly DayOfWeek[] EveryDay = [Mon, Tue, Wed, Thu, Fri, Sat, Sun];

    public static async Task RunAsync()
    {
        // Row 8 first: its log check needs an id this process has never resolved.
        Row8UnknownId();
        QaB1NullZone();
        Row1NullZoneMatchesPhase1();
        Row2ZoneEqualsLocal();
        Row3EastOffset();
        Row4SpringGap();
        Row5DifferingZoneOverlap();
        Row6NonHourOffsets();
        Row7CrossMidnight();
        Rows9And10Conflicts();
        Row11SessionDedupAcrossDst();
        Row15MidnightAndWholeDayGaps();
        QaB2InjectedLocalZone();
        QaB4IdResolution();
        QaN3Persistence();
        QaN4Memo();
        QaN5Normalization();
        QaN8ZoneChanges();
        QaN9Kind();
        NextFor();
        CtSet11NumericTimeZone();
        await CoordinatorFiresZonedSlot();
    }

    // ─── helpers ───

    private static TimeZoneInfo Find(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    /// <summary>A wall-clock value (Kind Unspecified).</summary>
    private static DateTime W(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Unspecified);

    /// <summary>A UTC instant.</summary>
    private static DateTime U(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    /// <summary>The wall time <paramref name="zone"/> shows at <paramref name="utc"/> (Kind Unspecified), as the coordinator derives <c>now</c>.</summary>
    private static DateTime WallIn(TimeZoneInfo zone, DateTime utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Utc, zone), DateTimeKind.Unspecified);

    private static ScheduleEntry Slot(string time, string? zone, params DayOfWeek[] days) => new() { Time = time, TimeZone = zone, Days = [.. days] };

    /// <summary>Default stations, schedule on; entries without a station get the first one.</summary>
    private static Settings With(params ScheduleEntry[] entries)
    {
        var settings = Settings.Defaults();
        settings.ScheduleEnabled = true;
        foreach (var entry in entries)
        {
            if (entry.StationId == Guid.Empty) entry.StationId = settings.Stations[0].Id;
            settings.Schedule.Add(entry);
        }
        return settings;
    }

    /// <summary>
    /// Drives one session from <paramref name="fromUtc"/> to <paramref name="toUtc"/> in <paramref name="step"/>s, with
    /// <c>now</c> = the instant's wall time in <paramref name="local"/> (what the coordinator does every tick). The first
    /// call only primes the session (it takes whatever is current); the later fires are returned with their instants.
    /// </summary>
    private static List<(DateTime Utc, Occurrence Slot)> Drive(Settings settings, TimeZoneInfo local, DateTime fromUtc, DateTime toUtc, TimeSpan step)
    {
        var session = new ScheduleSession();
        session.TakeChange(settings, WallIn(local, fromUtc), localZone: local);
        var fires = new List<(DateTime, Occurrence)>();
        for (var t = fromUtc + step; t <= toUtc; t += step)
            if (session.TakeChange(settings, WallIn(local, t), localZone: local) is { } fired) fires.Add((t, fired));
        return fires;
    }

    private static bool FiresOnceAt(List<(DateTime Utc, Occurrence Slot)> fires, DateTime utc)
    {
        if (fires.Count == 1 && fires[0].Utc == utc) return true;
        Console.WriteLine($"  fires: [{string.Join(", ", fires.Select(f => $"{f.Utc:yyyy-MM-dd HH:mm:ss}Z {f.Slot.Key}"))}], expected one at {utc:yyyy-MM-dd HH:mm:ss}Z");
        return false;
    }

    // ─── Row 8 (TZ-08): unknown id ───

    private static void Row8UnknownId()
    {
        var log = new RecordingAppLog();
        var saved = Scheduler.Log;
        Scheduler.Log = log;
        try
        {
            // A fresh id per run: the resolution cache is process-wide, so "Bad/Zone" may already be cached by another suite.
            var fresh = "Bad/Zone-" + Guid.NewGuid().ToString("N");
            var entry = Slot("08:00", fresh, Mon);
            var settings = With(entry);
            var resolutions = Enumerable.Range(0, 3).Select(_ => Scheduler.TryResolveZone(fresh, out var zone) == ZoneResolution.Unknown && zone is null).ToList();
            Scheduler.Evaluate(settings, W(2026, 9, 14, 9, 0), Athens);
            Scheduler.NextFor(entry, W(2026, 9, 14, 9, 0), Athens);
            Scheduler.Conflicts(settings.Schedule, Slot("08:00", fresh, Mon));
            Scheduler.TryResolveZone(" " + fresh + " ", out _);
            Scheduler.TryResolveZone("Europe/Athens", out _);
            Scheduler.TryResolveZone(null, out _);
            var warnings = log.Entries.Where(e => e.EventName == "schedule.zone_unknown").ToList();
            Check("Row 8 an unknown id resolves Unknown with a null zone, every time", resolutions.All(r => r));
            Check("Row 8 an unknown id logs exactly one schedule.zone_unknown warning, naming the id (repeated TryResolveZone/Evaluate/NextFor/Conflicts, trimmed variant)",
                warnings.Count == 1 && warnings[0].Level == AppLogLevel.Warn && warnings[0].Message.Contains(fresh, StringComparison.Ordinal)
                && log.Entries.Count == 1);

            var longId = "Bad/" + new string('x', 200) + Guid.NewGuid().ToString("N");
            Scheduler.TryResolveZone(longId, out _);
            var longWarning = log.Entries.Last();
            Check("Row 8 an overlong unknown id is logged truncated to 64 characters + \"…\"",
                log.Entries.Count == 2 && longWarning.Message.Contains(longId[..64] + "…", StringComparison.Ordinal) && !longWarning.Message.Contains(longId, StringComparison.Ordinal));
        }
        finally { Scheduler.Log = saved; }

        // Hostile ids: path-like, control characters, made-up Windows ids. None may throw. All but the NUL-suffixed one must be
        // Unknown; how the OS lookup treats an embedded NUL is platform behavior (macOS stops at it and finds Europe/Athens).
        string[] hostile = ["Bad/Zone", "../../../../etc/hosts", "/etc/passwd", "Europe/../Athens", "\u0001", "Bad Standard Time", new string('z', 5000)];
        var outcomes = new List<ZoneResolution>();
        Check("Row 8 hostile ids (path traversal, NUL, control chars, 5000 chars) do not throw",
            NoThrow(() => outcomes.AddRange(hostile.Append("Europe/Athens\0").Select(id => Scheduler.TryResolveZone(id, out _)))));
        var unexpected = hostile.Zip(outcomes).Where(p => p.Second != ZoneResolution.Unknown).Select(p => p.First.Length > 40 ? p.First[..40] + "…" : p.First).ToList();
        if (unexpected.Count > 0) Console.WriteLine("  resolved (expected Unknown): " + string.Join(", ", unexpected));
        Check("Row 8 hostile ids resolve Unknown", unexpected.Count == 0);

        var bad = Slot("08:00", "Bad/Zone", Mon);
        var badSettings = With(bad);
        var local = Slot("08:00", null, Mon);
        var localSettings = With(local);
        local.Id = bad.Id;
        (Occurrence? Current, Occurrence? Next) result = default;
        Check("Row 8 Evaluate with \"Bad/Zone\" does not throw", NoThrow(() => result = Scheduler.Evaluate(badSettings, W(2026, 9, 14, 9, 0), Athens)));
        var reference = Scheduler.Evaluate(localSettings, W(2026, 9, 14, 9, 0), Athens).Current!;
        Check("Row 8 \"Bad/Zone\" falls back to local time: same At and Key as a slot without a zone, ZoneResolution Unknown, Zone null",
            result.Current is { } current && current.At == W(2026, 9, 14, 8, 0) && current.At == reference.At && current.Key == reference.Key
            && current.ZoneResolution == ZoneResolution.Unknown && current.Zone is null && reference.ZoneResolution == ZoneResolution.Local);
        Check("Row 8 NextFor reports the unknown zone too (the UI's \"(unknown zone)\" source)",
            Scheduler.NextFor(bad, W(2026, 9, 14, 9, 0), Athens) is { ZoneResolution: ZoneResolution.Unknown, Zone: null } next && next.At == W(2026, 9, 21, 8, 0));

        using var temp = new TempDirectory("tz-row8");
        var store = new SettingsStore(temp.Combine("saved"));
        store.Save(badSettings);
        var loaded = store.Load();
        Check("Row 8 SettingsStore round-trip of \"Bad/Zone\": no warning, no .unreadable-*, the id is kept verbatim",
            store.Warning is null && Directory.GetFiles(store.DirectoryPath, "*.unreadable-*").Length == 0 && loaded.Schedule.Single().TimeZone == "Bad/Zone");

        var handDirectory = temp.Combine("hand-edited");
        Directory.CreateDirectory(handDirectory);
        var document = System.Text.Json.JsonSerializer.SerializeToNode(With(Slot("08:00", null, Mon)))!.AsObject();
        document["Schedule"]![0]!["TimeZone"] = "Bad/Zone";
        var handStore = new SettingsStore(handDirectory);
        File.WriteAllText(handStore.FilePath, document.ToJsonString());
        var handLoaded = handStore.Load();
        Check("Row 8 a hand-edited \"TimeZone\": \"Bad/Zone\" loads (no recovery path) and evaluates as local time",
            handStore.Warning is null && Directory.GetFiles(handDirectory, "*.unreadable-*").Length == 0
            && handLoaded.Schedule.Single().TimeZone == "Bad/Zone"
            && Scheduler.Evaluate(handLoaded, W(2026, 9, 14, 9, 0), NewYork).Current is { ZoneResolution: ZoneResolution.Unknown } c && c.At == W(2026, 9, 14, 8, 0));
    }

    // ─── QA-B1 ───

    private static void QaB1NullZone()
    {
        string?[] local = [null, "", " ", "\t", " \r\n "];
        Check("QA-B1 ResolveZone(null/\"\"/whitespace) returns null (never TimeZoneInfo.Local)", local.All(id => Scheduler.ResolveZone(id) is null));
        Check("QA-B1 TryResolveZone(null/\"\"/whitespace) is Local with a null zone",
            local.All(id => Scheduler.TryResolveZone(id, out var zone) == ZoneResolution.Local && zone is null));
    }

    // ─── Row 1 (TZ-01): null zone is byte-identical to Phase 1 ───

    /// <summary>Phase 1 <c>Scheduler.Evaluate</c>, re-stated: pure local wall-clock arithmetic on <c>now</c>'s own date.</summary>
    private static ((ScheduleEntry Entry, DateTime At)? Current, (ScheduleEntry Entry, DateTime At)? Next) Phase1(Settings settings, DateTime now)
    {
        var ids = settings.Stations.Select(s => s.Id).ToHashSet();
        var all = new List<(ScheduleEntry Entry, DateTime At)>();
        foreach (var entry in settings.Schedule.Where(e => e.Enabled && ids.Contains(e.StationId)))
        {
            if (!TimeOnly.TryParseExact(entry.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            for (var offset = -7; offset <= 7; offset++)
            {
                var date = now.Date.AddDays(offset);
                if (entry.Days.Contains(date.DayOfWeek)) all.Add((entry, date.Add(time.ToTimeSpan())));
            }
        }
        var current = all.Where(o => o.At <= now).OrderByDescending(o => o.At).ThenBy(o => o.Entry.Id).Select(o => ((ScheduleEntry, DateTime)?)o).FirstOrDefault();
        var next = all.Where(o => o.At > now).OrderBy(o => o.At).ThenBy(o => o.Entry.Id).Select(o => ((ScheduleEntry, DateTime)?)o).FirstOrDefault();
        return (current, next);
    }

    private static string Phase1Key(ScheduleEntry entry, DateTime at) => $"{entry.Id}:{at.ToString("yyyy-MM-dd'T'HH':'mm", CultureInfo.InvariantCulture)}";

    private static bool Identical(Occurrence? actual, (ScheduleEntry Entry, DateTime At)? expected, ZoneResolution resolution) =>
        expected is not { } e
            ? actual is null
            : actual is not null && ReferenceEquals(actual.Entry, e.Entry) && actual.At.Ticks == e.At.Ticks && actual.At.Kind == e.At.Kind
              && actual.Key == Phase1Key(e.Entry, e.At) && actual.Zone is null && actual.ZoneWall.Ticks == actual.At.Ticks
              && actual.ZoneWall.Kind == actual.At.Kind && actual.ZoneResolution == resolution;

    private static void Row1NullZoneMatchesPhase1()
    {
        var settings = Settings.Defaults();
        var s0 = settings.Stations[0].Id;
        var s1 = settings.Stations[1].Id;
        ScheduleEntry[] entries =
        [
            new() { StationId = s0, Time = "08:00", Days = [Mon, Tue] },
            new() { StationId = s1, Time = "10:00", Days = [Mon] },
            new() { StationId = s0, Time = "00:00", Days = [.. EveryDay] },
            new() { StationId = s1, Time = "03:30", Days = [Sun] },            // EU DST days
            new() { StationId = s0, Time = "02:30", Days = [Sun] },            // US DST days
            new() { StationId = s1, Time = "23:59", Days = [Sat] },
            new() { StationId = s0, Time = "12:00", Days = [Wed], Enabled = false },
            new() { StationId = Guid.NewGuid(), Time = "13:00", Days = [Thu] }, // missing station
            new() { StationId = s0, Time = "bad", Days = [Fri] },
        ];
        settings.Schedule.AddRange(entries);

        var nows = new List<DateTime>();
        for (var t = W(2026, 1, 1); t < W(2027, 1, 1); t += new TimeSpan(11, 7, 13)) nows.Add(t);
        nows.AddRange([W(2026, 3, 8, 2, 30), W(2026, 3, 8, 3, 0), W(2026, 3, 29, 3, 30), W(2026, 3, 29, 4, 0), W(2026, 10, 25, 3, 40), W(2026, 11, 1, 1, 30), W(2026, 12, 31, 23, 59, 59)]);

        string?[] spellings = [null, "", "   ", "\t "];
        DateTimeKind[] kinds = [DateTimeKind.Unspecified, DateTimeKind.Local, DateTimeKind.Utc];
        TimeZoneInfo?[] locals = [null, Chatham, Kathmandu];
        var memoBefore = Scheduler.ConversionMemoState;
        var compared = 0;
        string? mismatch = null;
        foreach (var spelling in spellings)
        {
            foreach (var entry in entries) entry.TimeZone = spelling;
            for (var i = 0; i < nows.Count && mismatch is null; i++)
            {
                foreach (var kind in kinds)
                {
                    var now = DateTime.SpecifyKind(nows[i], kind);
                    var local = locals[i % locals.Length];
                    var actual = Scheduler.Evaluate(settings, now, local);
                    var expected = Phase1(settings, now);
                    compared++;
                    if (!Identical(actual.Current, expected.Current, ZoneResolution.Local) || !Identical(actual.Next, expected.Next, ZoneResolution.Local))
                    {
                        mismatch = $"zone '{spelling}', now {now:o} ({kind}), local {local?.Id ?? "null"}: actual {actual.Current?.Key}/{actual.Next?.Key}, expected {expected.Current?.At:o}/{expected.Next?.At:o}";
                        break;
                    }
                }
            }
        }
        if (mismatch is not null) Console.WriteLine("  first mismatch: " + mismatch);
        Check($"Row 1 null/\"\"/whitespace zone: At, At.Kind, Entry and Key byte-identical to Phase 1 over 2026 ({compared} evaluations: 4 spellings × 3 Kinds × {nows.Count} instants, injected localZone ignored)",
            mismatch is null);
        Check("Row 1 / QA-N4 the null path never touches the conversion memo", Scheduler.ConversionMemoState == memoBefore);
    }

    // ─── Row 2 (TZ-02): zone == local ───

    /// <summary>The first valid wall minute at or after <paramref name="wall"/> in <paramref name="zone"/> (independent gap-end oracle).</summary>
    private static DateTime FirstValidWall(TimeZoneInfo zone, DateTime wall)
    {
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(1);
        return wall;
    }

    private static void Row2ZoneEqualsLocal()
    {
        foreach (var (id, zone) in new[] { ("Europe/Athens", Athens), ("America/New_York", NewYork) })
        {
            var stations = Settings.Defaults().Stations;
            ScheduleEntry[] plain =
            [
                new() { StationId = stations[0].Id, Time = "08:00", Days = [Mon, Tue, Wed, Thu, Fri] },
                new() { StationId = stations[1].Id, Time = "03:30", Days = [Sun] },
                new() { StationId = stations[2].Id, Time = "02:30", Days = [Sun] },
                new() { StationId = stations[0].Id, Time = "01:30", Days = [Sun] },
                new() { StationId = stations[1].Id, Time = "00:00", Days = [.. EveryDay] },
            ];
            var zoned = plain.Select(e => new ScheduleEntry { Id = e.Id, StationId = e.StationId, Time = e.Time, Days = e.Days, TimeZone = id }).ToArray();
            var nullSettings = new Settings { Stations = stations, Schedule = [.. plain] };
            var zonedSettings = new Settings { Stations = stations, Schedule = [.. zoned] };

            var nows = new List<DateTime>();
            for (var t = W(2026, 1, 1); t < W(2027, 1, 1); t += new TimeSpan(3, 7, 0)) nows.Add(t);
            foreach (var day in new[] { W(2026, 3, 8), W(2026, 3, 29), W(2026, 10, 25), W(2026, 11, 1) })
                for (var t = day; t < day.AddDays(1); t += TimeSpan.FromMinutes(10)) nows.Add(t);

            string? mismatch = null;
            var gapCases = 0;
            foreach (var now in nows.Where(n => !zone.IsInvalidTime(n)))
            {
                var a = Scheduler.Evaluate(nullSettings, now, zone);
                var b = Scheduler.Evaluate(zonedSettings, now, zone);
                foreach (var (n, z) in new[] { (a.Current, b.Current), (a.Next, b.Next) })
                {
                    if (n is null || z is null) { if (n != z) mismatch ??= $"now {now:o}: one side null"; continue; }
                    var expected = zone.IsInvalidTime(n.At) ? FirstValidWall(zone, n.At) : n.At;
                    if (zone.IsInvalidTime(n.At)) gapCases++;
                    if (z.Entry.Id != n.Entry.Id || z.At != expected || z.ZoneWall != n.At || z.ZoneResolution != ZoneResolution.Resolved)
                        mismatch ??= $"now {now:o}: null-zone {n.Entry.Time} {n.At:o}, zoned {z.Entry.Time} {z.At:o} (expected {expected:o})";
                }
            }
            if (mismatch is not null) Console.WriteLine("  first mismatch: " + mismatch);
            Check($"Row 2 zone == local ({id}): same slot and fire time as no zone at {nows.Count} instants over 2026 incl. both DST days (gap slots: first valid instant, {gapCases} cases)",
                mismatch is null && gapCases > 0);
        }
    }

    // ─── Row 3 (TZ-03): east offset ───

    private static void Row3EastOffset()
    {
        var settings = With(Slot("08:00", "Europe/Athens", Mon));
        Check("Row 3 08:00 Europe/Athens, computer UTC: fires 06:00 in winter (Mon 2026-01-12)",
            Scheduler.Evaluate(settings, W(2026, 1, 12, 5, 59, 59), Utc).Next?.At == W(2026, 1, 12, 6, 0)
            && Scheduler.Evaluate(settings, W(2026, 1, 12, 6, 0), Utc).Current?.At == W(2026, 1, 12, 6, 0));
        Check("Row 3 08:00 Europe/Athens, computer UTC: fires 05:00 in summer (Mon 2026-07-13)",
            Scheduler.Evaluate(settings, W(2026, 7, 13, 4, 59, 59), Utc).Next?.At == W(2026, 7, 13, 5, 0)
            && Scheduler.Evaluate(settings, W(2026, 7, 13, 5, 0), Utc).Current?.At == W(2026, 7, 13, 5, 0));
        var current = Scheduler.Evaluate(settings, W(2026, 7, 13, 5, 0), Utc).Current!;
        Check("Row 3 the occurrence carries its zone and zone wall time (Mon 08:00 Europe/Athens)",
            current.Zone?.Id == Athens.Id && current.ZoneWall == W(2026, 7, 13, 8, 0) && current.ZoneResolution == ZoneResolution.Resolved
            && current.Key == $"{current.Entry.Id}:2026-07-13T08:00@{Athens.Id}");
    }

    // ─── Row 4 (TZ-04) + QA-N7: spring gap ───

    private static void Row4SpringGap()
    {
        // Athens 2026-03-29: 03:00 EET → 04:00 EEST (01:00Z). 03:30 never happens.
        var athens = With(Slot("03:30", "Europe/Athens", Sun));
        Check("Row 4 precondition: Athens 2026-03-29 03:30 is in the spring gap", Athens.IsInvalidTime(W(2026, 3, 29, 3, 30)));
        var next = Scheduler.Evaluate(athens, W(2026, 3, 29, 0, 59, 59), Utc).Next;
        Check("Row 4 Athens 03:30 in the gap, computer UTC: first valid instant 01:00 (= 04:00 EEST), ZoneWall stays 03:30",
            next?.At == W(2026, 3, 29, 1, 0) && next.ZoneWall == W(2026, 3, 29, 3, 30));
        Check("Row 4 Athens gap slot on an Athens computer: At = 04:00 local (the first valid wall time)",
            Scheduler.Evaluate(athens, W(2026, 3, 29, 4, 0), Athens).Current?.At == W(2026, 3, 29, 4, 0));
        Check("Row 4 Athens gap slot fires once, at 01:00Z (UTC computer, minute ticks)",
            FiresOnceAt(Drive(athens, Utc, U(2026, 3, 28, 23, 0), U(2026, 3, 29, 3, 0), TimeSpan.FromMinutes(1)), U(2026, 3, 29, 1, 0)));
        var today = With(Slot("03:30", null, Sun));
        Check("Row 4 same instant as today: a local 03:30 slot on an Athens computer also first fires at 01:00Z",
            FiresOnceAt(Drive(today, Athens, U(2026, 3, 28, 23, 0), U(2026, 3, 29, 3, 0), TimeSpan.FromMinutes(1)), U(2026, 3, 29, 1, 0)));

        // New York 2026-03-08: 02:00 EST → 03:00 EDT (07:00Z). 02:30 never happens.
        var newYork = With(Slot("02:30", "America/New_York", Sun));
        Check("Row 4 precondition: New York 2026-03-08 02:30 is in the spring gap", NewYork.IsInvalidTime(W(2026, 3, 8, 2, 30)));
        Check("Row 4 New York 02:30 in the gap: 07:00 on a UTC computer, 09:00 on an Athens computer, 03:00 on a New York computer",
            Scheduler.NextFor(newYork.Schedule[0], W(2026, 3, 8, 0, 0), Utc)?.At == W(2026, 3, 8, 7, 0)
            && Scheduler.NextFor(newYork.Schedule[0], W(2026, 3, 8, 0, 0), Athens)?.At == W(2026, 3, 8, 9, 0)
            && Scheduler.NextFor(newYork.Schedule[0], W(2026, 3, 8, 0, 0), NewYork)?.At == W(2026, 3, 8, 3, 0));
        Check("Row 4 New York gap slot fires once, at 07:00Z (UTC computer, minute ticks)",
            FiresOnceAt(Drive(newYork, Utc, U(2026, 3, 8, 5, 0), U(2026, 3, 8, 9, 0), TimeSpan.FromMinutes(1)), U(2026, 3, 8, 7, 0)));

        var direct = Scheduler.ToComputerLocal(W(2026, 3, 8, 2, 30), NewYork, NewYork);
        Check("QA-N7 gap → first valid instant (03:00 EDT), not gap-shifted (03:30 EDT)", direct == W(2026, 3, 8, 3, 0) && direct != W(2026, 3, 8, 3, 30));
        Check("QA-N7 a gap slot on the zone's own computer is not delayed: At 03:00 (New York), 04:00 (Athens)",
            Scheduler.ToComputerLocal(W(2026, 3, 8, 2, 0), NewYork, NewYork) == W(2026, 3, 8, 3, 0)
            && Scheduler.ToComputerLocal(W(2026, 3, 29, 3, 59), Athens, Athens) == W(2026, 3, 29, 4, 0));
    }

    // ─── Row 5 (TZ-05, QA-B3): differing-zone fall overlap ───

    private static void Row5DifferingZoneOverlap()
    {
        // Athens 2026-10-25: 04:00 EEST → 03:00 EET, so 03:30 happens at 00:30Z (EEST) and at 01:30Z (EET).
        // New York is still on EDT (−4) that night: 00:30Z = Sat 20:30, 01:30Z = Sat 21:30.
        var wall = W(2026, 10, 25, 3, 30);
        var settings = With(Slot("03:30", "Europe/Athens", Sun));
        var plain = TimeZoneInfo.ConvertTime(wall, Athens, NewYork);
        var current = Scheduler.Evaluate(settings, W(2026, 10, 24, 22, 0), NewYork).Current;
        Check("Row 5 precondition: Athens 2026-10-25 03:30 is ambiguous", Athens.IsAmbiguousTime(wall));
        Check("Row 5 (QA-B3) 03:30 Athens on a New York computer resolves to the earlier daylight instant: Sat 20:30 (00:30Z)",
            current?.At == W(2026, 10, 24, 20, 30) && current.ZoneWall == wall && current.At.DayOfWeek == Sat && current.ZoneWall.DayOfWeek == Sun);
        Check("Row 5 (QA-B3) and that is NOT what plain ConvertTime gives (standard offset: 21:30)",
            plain == W(2026, 10, 24, 21, 30) && current?.At != plain);
        Check("Row 5 (QA-B3) ToComputerLocal gives the same earlier instant; NextFor lists it before 20:30",
            Scheduler.ToComputerLocal(wall, Athens, NewYork) == W(2026, 10, 24, 20, 30)
            && Scheduler.NextFor(settings.Schedule[0], W(2026, 10, 24, 20, 29), NewYork)?.At == W(2026, 10, 24, 20, 30));
    }

    // ─── Row 6 (TZ-06): non-whole-hour offsets ───

    private static void Row6NonHourOffsets()
    {
        DateTime? At(string zone, DateTime now, TimeZoneInfo local, params DayOfWeek[] days) => Scheduler.NextFor(Slot("08:00", zone, days), now, local)?.At;
        Check("Row 6 Mon 08:00 Asia/Kolkata (+05:30) on a UTC computer: Mon 02:30", At("Asia/Kolkata", W(2026, 9, 14), Utc, Mon) == W(2026, 9, 14, 2, 30));
        Check("Row 6 Mon 08:00 Asia/Kathmandu (+05:45) on a UTC computer: Mon 02:15", At("Asia/Kathmandu", W(2026, 9, 14), Utc, Mon) == W(2026, 9, 14, 2, 15));
        Check("Row 6 Mon 08:00 Asia/Kathmandu on a Kolkata computer: Mon 07:45", At("Asia/Kathmandu", W(2026, 9, 14), Kolkata, Mon) == W(2026, 9, 14, 7, 45));
        Check("Row 6 Mon 08:00 Pacific/Chatham (+12:45 standard, Sep) on a UTC computer: Sun 19:15 the day before",
            At("Pacific/Chatham", W(2026, 9, 12), Utc, Mon) == W(2026, 9, 13, 19, 15));
        Check("Row 6 Mon 08:00 Pacific/Chatham (+13:45 daylight, Jan) on a UTC computer: Sun 18:15 the day before",
            At("Pacific/Chatham", W(2026, 1, 10), Utc, Mon) == W(2026, 1, 11, 18, 15));
        Check("Row 6 Mon 00:00 UTC on a Kolkata computer: Mon 05:30; on a Chatham computer (Sep): Mon 12:45",
            Scheduler.NextFor(Slot("00:00", "UTC", Mon), W(2026, 9, 13), Kolkata)?.At == W(2026, 9, 14, 5, 30)
            && Scheduler.NextFor(Slot("00:00", "UTC", Mon), W(2026, 9, 13), Chatham)?.At == W(2026, 9, 14, 12, 45));
        var settings = With(Slot("08:00", "Asia/Kolkata", Mon));
        Check("Row 6 Current/Next around the converted Kolkata start: 1 s before it is Next, at it it is Current",
            Scheduler.Evaluate(settings, W(2026, 9, 14, 2, 29, 59), Utc).Next?.At == W(2026, 9, 14, 2, 30)
            && Scheduler.Evaluate(settings, W(2026, 9, 14, 2, 30), Utc).Current?.At == W(2026, 9, 14, 2, 30)
            && Scheduler.Evaluate(settings, W(2026, 9, 14, 2, 29, 59), Utc).Current?.At == W(2026, 9, 7, 2, 30));
    }

    // ─── Row 7 (TZ-07): cross-midnight ───

    private static void Row7CrossMidnight()
    {
        var monday = Slot("23:00", "UTC", Mon);
        var settings = With(monday);
        var current = Scheduler.Evaluate(settings, W(2026, 9, 15, 1, 0), Plus02).Current;
        Check("Row 7 Mon 23:00 UTC on a +02 computer lands Tue 01:00 local",
            current?.At == W(2026, 9, 15, 1, 0) && current.At.DayOfWeek == Tue);
        Check("Row 7 its ZoneWall is Monday 23:00 (the slot's own day)", current?.ZoneWall == W(2026, 9, 14, 23, 0) && current.ZoneWall.DayOfWeek == Mon);
        Check("Row 7 at Tue 00:59 local it is not yet current (Next = Tue 01:00)",
            Scheduler.Evaluate(settings, W(2026, 9, 15, 0, 59), Plus02) is var (c, n) && c?.At == W(2026, 9, 8, 1, 0) && n?.At == W(2026, 9, 15, 1, 0));
        var tuesday = With(Slot("23:00", "UTC", Tue));
        var (tc, tn) = Scheduler.Evaluate(tuesday, W(2026, 9, 15, 1, 0), Plus02);
        Check("Row 7 grouping is by the zone's day: a Tue 23:00 UTC slot is not current at Tue 01:00 local (it fires Wed 01:00)",
            tc?.At == W(2026, 9, 9, 1, 0) && tn?.At == W(2026, 9, 16, 1, 0) && tn.At.DayOfWeek == Wed && tn.ZoneWall.DayOfWeek == Tue);
    }

    // ─── Rows 9 and 10 (TZ-09/TZ-10) + QA-N5: conflicts ───

    private static void Rows9And10Conflicts()
    {
        bool Conflict(string? existing, string? candidate, DayOfWeek[]? days = null, string time = "08:00") =>
            Scheduler.Conflicts([Slot("08:00", existing, Mon, Tue)], Slot(time, candidate, days ?? [Mon]));

        Check("Row 9 same zone and time on an overlapping day is rejected (Europe/Athens)", Conflict("Europe/Athens", "Europe/Athens"));
        Check("Row 9 same zone after trimming is rejected (\" Europe/Athens \\t\")", Conflict("Europe/Athens", " Europe/Athens \t"));
        Check("Row 9 null vs \" \" (both local) is rejected; also \"\" vs null and \" \" vs \"\\t\"",
            Conflict(null, " ") && Conflict("", null) && Conflict(" ", "\t"));
        Check("Row 9 unknown id vs local (both fall back to local) is rejected", Conflict(null, "Bad/Zone") && Conflict("Bad/Zone", null));
        if (Scheduler.ResolveZone("europe/athens") is null)
            Skip("Row 9 case-variant id is rejected (\"europe/athens\" vs \"Europe/Athens\")", "\"europe/athens\" does not resolve on this OS, so it falls back to local time");
        else
            Check("Row 9 case-variant id is rejected (\"europe/athens\" vs \"Europe/Athens\")", Conflict("Europe/Athens", "europe/athens"));

        Check("Row 10 different zone, same time is allowed (UTC vs local)", !Conflict(null, "UTC") && !Conflict("UTC", null));
        Check("Row 10 different zone, same time is allowed (Europe/Athens vs America/New_York)", !Conflict("Europe/Athens", "America/New_York"));
        Check("Row 10 same zone, different day is allowed", !Conflict("Europe/Athens", "Europe/Athens", [Fri]));
        Check("Row 10 same zone, different time is allowed", !Conflict("Europe/Athens", "Europe/Athens", time: "08:01"));
        var existing = Slot("08:00", "Europe/Athens", Mon);
        var edited = Slot("08:00", "Europe/Athens", Mon, Wed);
        edited.Id = existing.Id;
        Check("Row 9 editing the same entry (same Id) is allowed", !Scheduler.Conflicts([existing], edited));
        Check("Row 9 a disabled existing slot in the same zone does not conflict",
            !Scheduler.Conflicts([new ScheduleEntry { Time = "08:00", TimeZone = "Europe/Athens", Days = [Mon], Enabled = false }], Slot("08:00", "Europe/Athens", Mon)));
        Check("QA-N5 documented approximation: ids sharing an instant (Europe/Athens vs Europe/Helsinki, Asia/Kolkata vs Asia/Calcutta) are not reported",
            !Conflict("Europe/Athens", "Europe/Helsinki") && !Conflict("Asia/Kolkata", "Asia/Calcutta"));
    }

    // ─── Row 11 (TZ-11): session dedup across DST ───

    private static void Row11SessionDedupAcrossDst()
    {
        var minute = TimeSpan.FromMinutes(1);
        // Athens computer, Athens slot, fall-back night: local 03:30 comes twice (00:30Z and 01:30Z).
        var athens = With(Slot("03:30", "Europe/Athens", Sun));
        var fires = Drive(athens, Athens, U(2026, 10, 24, 23, 0), U(2026, 10, 25, 2, 30), minute);
        Check("Row 11 Athens slot on an Athens computer across fall-back: fires once, at the first 03:30 (00:30Z)",
            FiresOnceAt(fires, U(2026, 10, 25, 0, 30)) && fires[0].Slot.ZoneWall == W(2026, 10, 25, 3, 30));
        Check("Row 11 Athens slot on a New York computer (differing zone) across the Athens fall-back: fires once, at 00:30Z",
            FiresOnceAt(Drive(athens, NewYork, U(2026, 10, 24, 23, 0), U(2026, 10, 25, 2, 30), minute), U(2026, 10, 25, 0, 30)));
        // New York computer, daily 05:30 UTC slot (= 01:30 EDT) on the New York fall-back night: local 01:30 comes twice.
        var utcSlot = With(Slot("05:30", "UTC", [.. EveryDay]));
        Check("Row 11 UTC slot on a New York computer across the local fall-back (local 01:30 twice): fires once, at 05:30Z",
            FiresOnceAt(Drive(utcSlot, NewYork, U(2026, 11, 1, 4, 0), U(2026, 11, 1, 8, 0), minute), U(2026, 11, 1, 5, 30)));
        var localSlot = With(Slot("01:30", null, [.. EveryDay]));
        Check("Row 11 contrast: a local 01:30 slot on a New York computer fires once, at the first 01:30 (05:30Z)",
            FiresOnceAt(Drive(localSlot, NewYork, U(2026, 11, 1, 4, 0), U(2026, 11, 1, 8, 0), minute), U(2026, 11, 1, 5, 30)));
        // Spring: a UTC slot at 01:30Z daily on an Athens computer across the local gap (local jumps 03:00 → 04:00 at 01:00Z).
        var spring = With(Slot("01:30", "UTC", [.. EveryDay]));
        Check("Row 11 UTC slot on an Athens computer across the local spring gap: fires once, at 01:30Z (04:30 EEST)",
            FiresOnceAt(Drive(spring, Athens, U(2026, 3, 28, 23, 0), U(2026, 3, 29, 3, 0), minute), U(2026, 3, 29, 1, 30)));
    }

    // ─── Row 15 (TZ-15) + QA-N7: midnight and whole-day gaps ───

    private static void Row15MidnightAndWholeDayGaps()
    {
        // America/Santiago 2026-09-06 (Sunday): 00:00 −04 → 01:00 −03 at 04:00Z.
        if (!TryFind("America/Santiago", out var santiago) || !santiago.IsInvalidTime(W(2026, 9, 6, 0, 30))
            || WallIn(santiago, U(2026, 9, 6, 4, 0)) != W(2026, 9, 6, 1, 0))
        {
            Skip("Row 15 Santiago midnight gap (2026-09-06 00:00→01:00)", "this OS's America/Santiago data has no 00:00→01:00 gap on 2026-09-06");
        }
        else
        {
            var sunday = Slot("00:30", "America/Santiago", Sun);
            var atMidnight = Slot("00:00", "America/Santiago", Sun);
            var saturday = Slot("00:30", "America/Santiago", Sat);
            var next = Scheduler.NextFor(sunday, W(2026, 9, 6, 0, 0), Utc);
            Check("Row 15 Santiago Sun 00:30 (in the midnight gap), UTC computer: first valid instant 04:00Z = Sun 01:00 −03",
                next?.At == W(2026, 9, 6, 4, 0) && next.ZoneWall == W(2026, 9, 6, 0, 30) && WallIn(santiago, U(2026, 9, 6, 4, 0)).DayOfWeek == Sun);
            Check("Row 15 Santiago Sun 00:00 (the gap's first minute) also fires at 04:00Z, not a day early or late",
                Scheduler.NextFor(atMidnight, W(2026, 9, 6, 0, 0), Utc)?.At == W(2026, 9, 6, 4, 0));
            Check("Row 15 Santiago: a Saturday 00:30 slot is untouched by the Sunday gap (Sat 04:30Z)",
                Scheduler.NextFor(saturday, W(2026, 9, 5, 0, 0), Utc)?.At == W(2026, 9, 5, 4, 30));
            Check("Row 15 Santiago gap slot fires once, at 04:00Z (UTC computer, minute ticks)",
                FiresOnceAt(Drive(With(sunday), Utc, U(2026, 9, 6, 2, 0), U(2026, 9, 6, 6, 0), TimeSpan.FromMinutes(1)), U(2026, 9, 6, 4, 0)));
        }

        // Pacific/Apia skipped Friday 2011-12-30: 23:59:59 −10 on Thu 29 → 00:00 +14 on Sat 31 (10:00Z on the 30th).
        if (!TryFind("Pacific/Apia", out var apia) || WallIn(apia, U(2011, 12, 30, 9, 59, 59)) != W(2011, 12, 29, 23, 59, 59)
            || WallIn(apia, U(2011, 12, 30, 10, 0)) != W(2011, 12, 31, 0, 0))
        {
            Skip("Row 15 Apia whole-day gap (2011-12-30)", "this OS's Pacific/Apia data lacks the 2011 date-line change");
        }
        else
        {
            var friday = Slot("10:00", "Pacific/Apia", Fri);
            var settings = With(friday);
            var current = Scheduler.Evaluate(settings, W(2011, 12, 30, 10, 0), Utc).Current;
            Check("Row 15 Apia Fri 2011-12-30 10:00 (a day that never happened) fires at the first valid instant, 10:00Z = Sat 31 00:00 +14",
                current?.At == W(2011, 12, 30, 10, 0) && current.ZoneWall == W(2011, 12, 30, 10, 0) && current.ZoneWall.DayOfWeek == Fri
                && WallIn(apia, U(2011, 12, 30, 10, 0)).DayOfWeek == Sat);
            Check("Row 15 Apia: one second before, it is Next (and last week's Friday is Current)",
                Scheduler.Evaluate(settings, W(2011, 12, 30, 9, 59, 59), Utc) is var (c, n) && n?.At == W(2011, 12, 30, 10, 0) && c?.ZoneWall == W(2011, 12, 23, 10, 0));
            Check("Row 15 Apia: any time on the skipped Friday fires at the same first valid instant (00:00, 23:59)",
                Scheduler.NextFor(Slot("00:00", "Pacific/Apia", Fri), W(2011, 12, 29), Utc)?.At == W(2011, 12, 30, 10, 0)
                && Scheduler.NextFor(Slot("23:59", "Pacific/Apia", Fri), W(2011, 12, 29), Utc)?.At == W(2011, 12, 30, 10, 0));
            Check("Row 15 Apia: neighbors are exact (Thu 23:00 −10 = 09:00Z; Sat 00:30 +14 = 10:30Z)",
                Scheduler.NextFor(Slot("23:00", "Pacific/Apia", Thu), W(2011, 12, 29), Utc)?.At == W(2011, 12, 30, 9, 0)
                && Scheduler.NextFor(Slot("00:30", "Pacific/Apia", Sat), W(2011, 12, 29), Utc)?.At == W(2011, 12, 30, 10, 30));
            Check("Row 15 Apia whole-day gap slot fires once, at 10:00Z (UTC computer, minute ticks)",
                FiresOnceAt(Drive(settings, Utc, U(2011, 12, 30, 8, 0), U(2011, 12, 30, 12, 0), TimeSpan.FromMinutes(1)), U(2011, 12, 30, 10, 0)));
        }
    }

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try { zone = Find(id); return true; }
        catch (TimeZoneNotFoundException) { zone = Utc; return false; }
    }

    // ─── QA-B2: injected localZone ───

    private static void QaB2InjectedLocalZone()
    {
        // One instant, 2026-09-14 06:00Z; slot Mon 08:00 Athens = 05:00Z.
        var settings = With(Slot("08:00", "Europe/Athens", Mon));
        var instant = U(2026, 9, 14, 6, 0);
        (TimeZoneInfo Local, DateTime Expected)[] cases =
            [(Utc, W(2026, 9, 14, 5, 0)), (Kolkata, W(2026, 9, 14, 10, 30)), (NewYork, W(2026, 9, 14, 1, 0)), (Chatham, W(2026, 9, 14, 17, 45))];
        Check("QA-B2 the injected localZone decides the local fire time for one instant (UTC 05:00, Kolkata 10:30, New York 01:00, Chatham 17:45)",
            cases.All(c => Scheduler.Evaluate(settings, WallIn(c.Local, instant), c.Local).Current?.At == c.Expected));

        var now = W(2026, 9, 14, 9, 0);
        (Occurrence? Current, Occurrence? Next) local = default, utc = default;
        Check("QA-B2 now with Kind=Local and a non-host localZone does not throw (Evaluate)",
            NoThrow(() => local = Scheduler.Evaluate(settings, DateTime.SpecifyKind(now, DateTimeKind.Local), Athens)));
        Check("QA-B2 now with Kind=Utc and a non-UTC localZone does not throw (Evaluate)",
            NoThrow(() => utc = Scheduler.Evaluate(settings, DateTime.SpecifyKind(now, DateTimeKind.Utc), Athens)));
        var unspecified = Scheduler.Evaluate(settings, now, Athens);
        Check("QA-B2 now's Kind is ignored for conversion: Local/Utc/Unspecified give the same zoned At",
            local.Current?.At == W(2026, 9, 14, 8, 0) && local.Current?.At == unspecified.Current?.At && utc.Current?.At == unspecified.Current?.At
            && local.Next?.At == unspecified.Next?.At && utc.Next?.At == unspecified.Next?.At);
        Check("QA-B2 ScheduleSession.TakeChange/HoldCurrent and NextFor/ToComputerLocal accept Kind=Local with a non-host localZone",
            NoThrow(() =>
            {
                var session = new ScheduleSession();
                session.HoldCurrent(settings, DateTime.SpecifyKind(now, DateTimeKind.Local), NewYork);
                session.TakeChange(settings, DateTime.SpecifyKind(now, DateTimeKind.Local), localZone: NewYork);
                Scheduler.NextFor(settings.Schedule[0], DateTime.SpecifyKind(now, DateTimeKind.Local), Kolkata);
                Scheduler.ToComputerLocal(DateTime.SpecifyKind(now, DateTimeKind.Local), Athens, NewYork);
            }));
    }

    // ─── QA-B4: id resolution ───

    private static void QaB4IdResolution()
    {
        var winter = U(2026, 1, 15, 12, 0);
        var summer = U(2026, 7, 15, 12, 0);
        (string Id, TimeSpan Winter, TimeSpan Summer)[] ianas =
        [
            ("Europe/Athens", TimeSpan.FromHours(2), TimeSpan.FromHours(3)), ("America/New_York", TimeSpan.FromHours(-5), TimeSpan.FromHours(-4)),
            ("Asia/Kolkata", new TimeSpan(5, 30, 0), new TimeSpan(5, 30, 0)), ("UTC", TimeSpan.Zero, TimeSpan.Zero),
        ];
        Check($"QA-B4 IANA ids resolve on {(OperatingSystem.IsWindows() ? "Windows" : "this OS")} with the right offsets (Europe/Athens, America/New_York, Asia/Kolkata, UTC)",
            ianas.All(z => Scheduler.TryResolveZone(z.Id, out var zone) == ZoneResolution.Resolved && zone!.GetUtcOffset(winter) == z.Winter && zone.GetUtcOffset(summer) == z.Summer));
        Check("QA-B4 the IANA→Windows fallback id exists: Europe/Athens → GTB Standard Time",
            TimeZoneInfo.TryConvertIanaIdToWindowsId("Europe/Athens", out var windowsId) && windowsId == "GTB Standard Time");
        var fallback = Scheduler.ResolveZone("GTB Standard Time");
        Check($"QA-B4 the Windows-id fallback resolves on {(OperatingSystem.IsWindows() ? "Windows" : "this OS")} to Athens' rules (GTB Standard Time)",
            fallback is not null && fallback.GetUtcOffset(winter) == TimeSpan.FromHours(2) && fallback.GetUtcOffset(summer) == TimeSpan.FromHours(3)
            && fallback.IsInvalidTime(W(2026, 3, 29, 3, 30)) && fallback.IsAmbiguousTime(W(2026, 10, 25, 3, 30)));
        Check("QA-B4 a slot stored with an IANA id converts identically to one stored with its Windows id",
            Scheduler.NextFor(Slot("08:00", "Europe/Athens", Mon), W(2026, 9, 13), Utc)?.At == W(2026, 9, 14, 5, 0)
            && Scheduler.NextFor(Slot("08:00", "GTB Standard Time", Mon), W(2026, 9, 13), Utc)?.At == W(2026, 9, 14, 5, 0));
    }

    // ─── QA-N3: persistence ───

    private static void QaN3Persistence()
    {
        using var temp = new TempDirectory("tz-qan3");
        var settings = With(Slot("08:00", null, Mon), Slot("09:00", "Europe/Athens", Tue), Slot("10:00", "Asia/Kathmandu", Wed));
        var store = new SettingsStore(temp.Path);
        store.Save(settings);
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        var schedule = json["Schedule"]!.AsArray();
        Check("QA-N3 Version stays 1 when zones are saved", json["Version"]!.GetValue<int>() == 1);
        Check("QA-N3 a null TimeZone is not written (a zone-less slot saves exactly as before)", !schedule[0]!.AsObject().ContainsKey("TimeZone"));
        Check("QA-N3 a set TimeZone is written verbatim", schedule[1]!["TimeZone"]!.GetValue<string>() == "Europe/Athens" && schedule[2]!["TimeZone"]!.GetValue<string>() == "Asia/Kathmandu");
        var loaded = store.Load();
        Check("QA-N3 zones round-trip through SettingsStore (null stays null, ids unchanged, no warning)",
            store.Warning is null && loaded.Version == 1 && loaded.Schedule.Select(e => e.TimeZone).SequenceEqual([null, "Europe/Athens", "Asia/Kathmandu"]));

        var legacy = new SettingsStore(temp.Combine("legacy"));
        Directory.CreateDirectory(legacy.DirectoryPath);
        var old = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        foreach (var entry in old["Schedule"]!.AsArray()) entry!.AsObject().Remove("TimeZone");
        File.WriteAllText(legacy.FilePath, old.ToJsonString());
        Check("QA-N3 a pre-timezone file (no TimeZone properties) loads with every TimeZone null",
            legacy.Load().Schedule.All(e => e.TimeZone is null) && legacy.Warning is null);
    }

    // ─── QA-N4: conversion memo ───

    private static void QaN4Memo()
    {
        // Far-future days no other suite uses, so the memo's day is ours.
        var day = W(2031, 1, 6);
        var settings = With(Slot("08:00", "Europe/Athens", [.. EveryDay]));
        var first = Scheduler.Evaluate(settings, day.AddHours(10), Utc);
        var state = Scheduler.ConversionMemoState;
        Check("QA-N4 a zoned evaluation memoizes its conversions for the local day (15 candidates)", state.Day == day && state.Count == 15);
        var again = new[] { 10, 12, 18, 21 }.Select(h => Scheduler.Evaluate(settings, day.AddHours(h), Utc)).ToList();
        Check("QA-N4 memo stable within the day: repeated evaluations add nothing and give identical results",
            Scheduler.ConversionMemoState == state && again[0].Current == first.Current && again[0].Next == first.Next
            && again.All(r => r.Current is { } c && c.At == Scheduler.ToComputerLocal(c.ZoneWall, Athens, Utc)));
        var nextDay = Scheduler.Evaluate(settings, day.AddDays(1).AddMinutes(1), Utc);
        var after = Scheduler.ConversionMemoState;
        Check("QA-N4 memo dropped at local midnight: new day, fresh 15 entries (not 16 accumulated)", after.Day == day.AddDays(1) && after.Count == 15);
        Check("QA-N4 results after the day change are still exact", nextDay.Current?.At == W(2031, 1, 6, 6, 0) && nextDay.Next?.At == W(2031, 1, 7, 6, 0));
        var otherLocal = Scheduler.Evaluate(settings, day.AddDays(1).AddHours(1), NewYork);
        Check("QA-N4 memo is keyed by the local zone too: the same day on a New York computer adds its own 15 and converts correctly",
            Scheduler.ConversionMemoState == (day.AddDays(1), 30) && otherLocal.Current?.At == W(2031, 1, 7, 1, 0));
        var memo = Scheduler.ConversionMemoState;
        Scheduler.Evaluate(With(Slot("08:00", null, [.. EveryDay]), Slot("09:00", "  ", Mon), Slot("10:00", "Bad/Zone", Tue)), day.AddDays(5), Utc);
        Check("QA-N4 the null path (no zone, whitespace, unknown id) does not use the memo, even on a new day", Scheduler.ConversionMemoState == memo);
    }

    // ─── QA-N5: normalization ───

    private static void QaN5Normalization()
    {
        string[] spellings = ["Europe/Athens", " Europe/Athens", "Europe/Athens ", "\tEurope/Athens\r\n"];
        Check("QA-N5 surrounding whitespace is trimmed before lookup (all spellings resolve to Europe/Athens)",
            spellings.All(s => Scheduler.TryResolveZone(s, out var zone) == ZoneResolution.Resolved && string.Equals(zone!.Id, "Europe/Athens", StringComparison.OrdinalIgnoreCase)));
        var at = spellings.Select(s => Scheduler.NextFor(Slot("08:00", s, Mon), W(2026, 9, 13), Utc)).ToList();
        Check("QA-N5 trimmed spellings give the same fire time and zone (not a silent fallback to local)",
            at.All(o => o?.At == W(2026, 9, 14, 5, 0) && o.ZoneResolution == ZoneResolution.Resolved && o.Zone?.Id == at[0]!.Zone?.Id));
        Check("QA-N5 an inner space is not trimmed away (\"Europe/ Athens\" is Unknown)", Scheduler.TryResolveZone("Europe/ Athens", out _) == ZoneResolution.Unknown);
    }

    // ─── QA-N8: zone edits and OS zone changes ───

    private static void QaN8ZoneChanges()
    {
        // Zone edit: slot Mon 10:00 UTC fired at 10:00Z; at 10:30Z it is re-zoned to Athens, whose Mon 10:00 was 07:00Z.
        var slot = Slot("10:00", "UTC", Mon);
        var settings = With(slot);
        var session = new ScheduleSession();
        var firedUtc = session.TakeChange(settings, W(2026, 9, 14, 10, 0), localZone: Utc);
        slot.TimeZone = "Europe/Athens";
        var afterEdit = session.TakeChange(settings, W(2026, 9, 14, 10, 30), localZone: Utc);
        Check("QA-N8 a zone edit fires the genuinely current slot (Mon 10:00 Athens = 07:00 local), not suppressed as older",
            firedUtc?.At == W(2026, 9, 14, 10, 0) && afterEdit?.At == W(2026, 9, 14, 7, 0) && afterEdit.Zone?.Id == Athens.Id);
        Check("QA-N8 the edited slot fires once", session.TakeChange(settings, W(2026, 9, 14, 10, 31), localZone: Utc) is null);

        // OS zone change: an Athens slot fired on a UTC computer; the computer then moves to Athens, then New York.
        var zoned = With(Slot("08:00", "Europe/Athens", Mon));
        var os = new ScheduleSession();
        var fired = os.TakeChange(zoned, W(2026, 9, 14, 5, 0), localZone: Utc);
        var inAthens = os.TakeChange(zoned, WallIn(Athens, U(2026, 9, 14, 5, 10)), localZone: Athens);
        var inNewYork = os.TakeChange(zoned, WallIn(NewYork, U(2026, 9, 14, 5, 20)), localZone: NewYork);
        Check("QA-N8 an OS zone change does not refire the same zoned start (UTC → Athens → New York computer)",
            fired?.At == W(2026, 9, 14, 5, 0) && inAthens is null && inNewYork is null);
        Check("QA-N8 the same start keeps one Key across computer zones (Key names the slot's zone wall time)",
            Scheduler.Evaluate(zoned, W(2026, 9, 14, 5, 0), Utc).Current?.Key == Scheduler.Evaluate(zoned, W(2026, 9, 14, 8, 0), Athens).Current?.Key
            && fired?.Key == $"{zoned.Schedule[0].Id}:2026-09-14T08:00@{Athens.Id}");
        Check("QA-N8 after the zone change, the next start still fires (Mon 2026-09-21 08:00 Athens on the New York computer = 01:00)",
            os.TakeChange(zoned, W(2026, 9, 21, 1, 0), localZone: NewYork)?.At == W(2026, 9, 21, 1, 0));
    }

    // ─── QA-N9: Kind ───

    private static void QaN9Kind()
    {
        var settings = With(Slot("08:00", "Europe/Athens", Mon));
        DateTimeKind[] kinds = [DateTimeKind.Unspecified, DateTimeKind.Local, DateTimeKind.Utc];
        Check("QA-N9 a zoned At is Kind Unspecified for every now.Kind (Current, Next, ZoneWall)",
            kinds.All(k => Scheduler.Evaluate(settings, DateTime.SpecifyKind(W(2026, 9, 14, 9, 0), k), Utc) is var (c, n)
                && c?.At.Kind == DateTimeKind.Unspecified && n?.At.Kind == DateTimeKind.Unspecified && c.ZoneWall.Kind == DateTimeKind.Unspecified));
        Check("QA-N9 NextFor and ToComputerLocal return Kind Unspecified",
            kinds.All(k => Scheduler.NextFor(settings.Schedule[0], DateTime.SpecifyKind(W(2026, 9, 14, 9, 0), k), Utc)?.At.Kind == DateTimeKind.Unspecified
                && Scheduler.ToComputerLocal(DateTime.SpecifyKind(W(2026, 9, 14, 8, 0), k), Athens, NewYork).Kind == DateTimeKind.Unspecified));
        Check("QA-N9 a zoned At is a local wall time, never UTC (Athens slot on a New York computer: 01:00, not 05:00)",
            Scheduler.Evaluate(settings, W(2026, 9, 14, 2, 0), NewYork).Current?.At == W(2026, 9, 14, 1, 0));

        var dotted = (CultureInfo)CultureInfo.GetCultureInfo("th-TH").Clone();
        dotted.DateTimeFormat.TimeSeparator = ".";
        var saved = CultureInfo.CurrentCulture;
        string? key;
        try
        {
            CultureInfo.CurrentCulture = dotted;
            key = Scheduler.Evaluate(settings, W(2026, 9, 14, 9, 0), Utc).Current?.Key;
        }
        finally { CultureInfo.CurrentCulture = saved; }
        Check("CF-01 a zoned Key is culture-invariant too (th-TH Buddhist calendar with a '.' time separator)",
            key == $"{settings.Schedule[0].Id}:2026-09-14T08:00@{Athens.Id}");
    }

    // ─── NextFor: per-row next local fire time (QA-N6) ───

    private static void NextFor()
    {
        var entry = Slot("08:00", "Europe/Athens", Mon);
        Check("NextFor Mon 08:00 Athens on a UTC computer at Mon 04:00: today 05:00", Scheduler.NextFor(entry, W(2026, 9, 14, 4, 0), Utc)?.At == W(2026, 9, 14, 5, 0));
        Check("NextFor is strictly after now: at exactly 05:00 it is next week's", Scheduler.NextFor(entry, W(2026, 9, 14, 5, 0), Utc)?.At == W(2026, 9, 21, 5, 0));
        entry.Enabled = false;
        Check("NextFor a disabled entry still reports its next local fire time (row display)", Scheduler.NextFor(entry, W(2026, 9, 14, 4, 0), Utc)?.At == W(2026, 9, 14, 5, 0));
        entry.Enabled = true;
        entry.StationId = Guid.NewGuid();
        Check("NextFor ignores whether the station exists", Scheduler.NextFor(entry, W(2026, 9, 14, 4, 0), Utc)?.At == W(2026, 9, 14, 5, 0));
        var crossMidnight = Scheduler.NextFor(Slot("23:00", "UTC", Mon), W(2026, 9, 14, 12, 0), Plus02);
        Check("NextFor cross-midnight: Mon 23:00 UTC on a +02 computer is Tue 01:00 local (ZoneWall Monday)",
            crossMidnight?.At == W(2026, 9, 15, 1, 0) && crossMidnight.ZoneWall.DayOfWeek == Mon);
        Check("NextFor a local slot matches Evaluate's Next for the same single slot",
            Scheduler.NextFor(Slot("08:00", null, Mon, Thu), W(2026, 9, 14, 9, 0), Kolkata)?.At == W(2026, 9, 17, 8, 0));
        Check("NextFor Mon 08:00 Kolkata on an Athens computer (Sep, EEST): Mon 05:30", Scheduler.NextFor(Slot("08:00", "Asia/Kolkata", Mon), W(2026, 9, 14), Athens)?.At == W(2026, 9, 14, 5, 30));
        Check("NextFor is null for no days or an unparsable time",
            Scheduler.NextFor(Slot("08:00", "Europe/Athens"), W(2026, 9, 14), Utc) is null && Scheduler.NextFor(Slot("25:00", "Europe/Athens", Mon), W(2026, 9, 14), Utc) is null);
        var next = Scheduler.NextFor(Slot("08:00", "Europe/Athens", Mon, Wed, Fri), W(2026, 9, 14, 6, 0), Utc);
        Check("NextFor picks the soonest of several days (Mon 05:00Z passed → Wed 05:00)", next?.At == W(2026, 9, 16, 5, 0) && next.ZoneWall == W(2026, 9, 16, 8, 0));
    }

    // ─── CT-SET-11: numeric TimeZone (documented hazard, pinned) ───

    private static void CtSet11NumericTimeZone()
    {
        using var temp = new TempDirectory("tz-ctset11");
        var document = System.Text.Json.JsonSerializer.SerializeToNode(With(Slot("08:00", null, Mon)))!.AsObject();
        document["Schedule"]![0]!["TimeZone"] = 123;
        var store = new SettingsStore(temp.Path);
        File.WriteAllText(store.FilePath, document.ToJsonString());
        var original = File.ReadAllText(store.FilePath);
        var loaded = store.Load();
        var backups = Directory.GetFiles(temp.Path, "settings.json.unreadable-*");
        Check("CT-SET-11 [hazard] \"TimeZone\": 123 resets to defaults with a .unreadable-* copy (QA-N3 note; pinned, not fixed)",
            backups.Length == 1 && File.ReadAllText(backups[0]) == original && loaded.Schedule.Count == 0 && loaded.Stations.Count == 3
            && store.Warning is not null && store.Warning.Contains(backups[0], StringComparison.Ordinal));
    }

    // ─── Coordinator integration ───

    private static async Task CoordinatorFiresZonedSlot()
    {
        // New York computer (EDT, −4). Slot X: Mon 08:00 Europe/Athens → Bravo (= 05:00Z = Mon 01:00 local).
        // Slot Y: Sun 12:00 local → Alpha, so the first tick has something older to catch up on.
        await using var rig = CoordinatorRig.Create(schedule: true, slots: false, zone: NewYork, localNow: W(2026, 9, 14, 0, 59, 0));
        var zoned = new ScheduleEntry { StationId = rig.B.Id, Time = "08:00", TimeZone = "Europe/Athens", Days = [Mon] };
        rig.Settings.Schedule.AddRange([new ScheduleEntry { StationId = rig.A.Id, Time = "12:00", Days = [Sun] }, zoned]);
        await rig.Step(1);
        Check("Coordinator (New York computer): the first tick catches up the local Sunday slot (Alpha)",
            rig.StartCount == 1 && rig.LastStart.Source.DisplayName == "Alpha");
        Check("Coordinator Snapshot.Next is the Athens slot at Mon 01:00 local, with its zone",
            rig.S.Next is { } next && next.Entry == zoned && next.At == W(2026, 9, 14, 1, 0) && next.Zone?.Id == Athens.Id && rig.S.NextStationName == "Bravo");
        rig.Clock.UtcNow = new DateTimeOffset(2026, 9, 14, 4, 59, 58, TimeSpan.Zero);
        await rig.Step(1);
        Check("Coordinator: nothing new at 00:59:59 local (04:59:59Z)", rig.StartCount == 1);
        await rig.Step(1);
        Check("Coordinator fires the zoned slot at exactly 01:00:00 local (05:00:00Z = 08:00 Athens)",
            rig.StartCount == 2 && rig.LastStart.Source.DisplayName == "Bravo" && rig.Clock.UtcNow == new DateTimeOffset(2026, 9, 14, 5, 0, 0, TimeSpan.Zero));
        var fired = rig.Log.Entries.Where(e => e.EventName == "schedule.fired").ToList();
        Check("Coordinator schedule.fired names the slot's zone (\"08:00 Europe/Athens\") and station",
            fired.Count == 2 && fired[1].Message.Contains("08:00 " + Athens.Id, StringComparison.Ordinal) && fired[1].Message.Contains("Bravo", StringComparison.Ordinal)
            && !fired[0].Message.Contains('/', StringComparison.Ordinal));
        rig.Playing();
        await rig.Step(60);
        Check("Coordinator: the zoned slot fires once", rig.StartCount == 2 && rig.LogCount("schedule.fired") == 2);
    }
}
