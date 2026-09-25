using System.Globalization;
using DialShift.Core;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>
/// <c>Scheduler.Evaluate</c>/<c>Conflicts</c>/<c>TryTime</c> and <c>Occurrence.Key</c>. <see cref="Legacy"/> is the original
/// Program.cs sequence (names, order and state mutations unchanged); CT-SCH-* are acceptance-matrix §7.2.
/// Every <c>now</c> is a <see cref="DateTimeKind.Unspecified"/> wall-clock value, so no check depends on the host zone.
/// </summary>
public static class SchedulerTests
{
    /// <summary>2026-09-14 is a Monday.</summary>
    private static DateTime Mon(int hour, int minute = 0) => new(2026, 9, 14, hour, minute, 0);

    public static void Run()
    {
        Legacy();
        TieBreak();
        KeyFormat();
        EmptyDays();
        WeeklyWrap();
        KindPreserved();
        TryTimeStrictness();
        ConflictRules();
        IgnoresScheduleEnabled();
        DottedTimes();
    }

    private static void Legacy()
    {
        var settings = Settings.Defaults();
        var first = new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday, DayOfWeek.Tuesday] };
        var second = new ScheduleEntry { StationId = settings.Stations[1].Id, Time = "10:00", Days = [DayOfWeek.Monday] };
        settings.Schedule.AddRange([first, second]);
        var monday = Mon(8);
        Check("Exact boundary selects new slot", Scheduler.Evaluate(settings, monday).Current?.Entry.Id == first.Id);
        Check("Next slot is strictly future", Scheduler.Evaluate(settings, monday).Next?.Entry.Id == second.Id);
        Check("Before first slot wraps previous week", Scheduler.Evaluate(settings, monday.AddMinutes(-1)).Current?.At == new DateTime(2026, 9, 8, 8, 0, 0));
        Check("Late wake catches latest slot", Scheduler.Evaluate(settings, monday.AddHours(5)).Current?.Entry.Id == second.Id);
        Check("Midnight continues previous station", Scheduler.Evaluate(settings, monday.Date.AddDays(1)).Current?.Entry.Id == second.Id);
        Check("Next week after final slot", Scheduler.Evaluate(settings, monday.AddDays(5)).Next?.At == monday.AddDays(7));
        second.Enabled = false;
        Check("Disabled slot is ignored", Scheduler.Evaluate(settings, monday.AddHours(5)).Current?.Entry.Id == first.Id);
        second.Enabled = true;
        var candidate = new ScheduleEntry { Time = "08:00", Days = [DayOfWeek.Tuesday] };
        Check("Overlapping day/time conflicts", Scheduler.Conflicts(settings.Schedule, candidate));
        candidate.Days = [DayOfWeek.Friday];
        Check("Different days do not conflict", !Scheduler.Conflicts(settings.Schedule, candidate));
        Check("Editing same entry is allowed", !Scheduler.Conflicts(settings.Schedule, first));
        Check("Invalid time rejected", !Scheduler.TryTime("25:00", out _) && !Scheduler.TryTime("8:00", out _));
        Check("Midnight accepted", Scheduler.TryTime("00:00", out _));
        settings.Stations.RemoveAt(1);
        Check("Deleted station ignored", Scheduler.Evaluate(settings, monday.AddHours(5)).Current?.Entry.Id == first.Id);
        settings.Schedule.Add(new() { StationId = settings.Stations[0].Id, Time = "bad", Days = [DayOfWeek.Monday] });
        Check("Malformed slot ignored", Scheduler.Evaluate(settings, monday.AddHours(5)).Current?.Entry.Id == first.Id);
        var noSlots = Settings.Defaults();
        Check("Empty schedule is idle", Scheduler.Evaluate(noSlots, monday) == (null, null));
        // QA-N2: machine-independent already (pure wall-clock arithmetic, no TimeZoneInfo). Keep unchanged.
        var dst = new Settings { Stations = settings.Stations, Schedule = [new() { StationId = settings.Stations[0].Id, Time = "03:30", Days = [DayOfWeek.Sunday] }] };
        Check("Spring jump catches skipped slot", Scheduler.Evaluate(dst, new DateTime(2026, 3, 29, 4, 0, 0)).Current?.At == new DateTime(2026, 3, 29, 3, 30, 0));
        Check("Repeated hour occurrence has stable key", Scheduler.Evaluate(dst, new DateTime(2026, 10, 25, 3, 40, 0)).Current?.Key == Scheduler.Evaluate(dst, new DateTime(2026, 10, 25, 3, 50, 0)).Current?.Key);
    }

    private static void TieBreak()
    {
        // Two entries at the same instant, added directly (bypassing Conflicts); the larger id is listed first.
        var settings = Settings.Defaults();
        var small = new ScheduleEntry { Id = Guid.Parse("10000000-0000-0000-0000-000000000000"), StationId = settings.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday] };
        var large = new ScheduleEntry { Id = Guid.Parse("20000000-0000-0000-0000-000000000000"), StationId = settings.Stations[1].Id, Time = "08:00", Days = [DayOfWeek.Monday] };
        settings.Schedule.AddRange([large, small]);
        Check("[quirk] CT-SCH-01 same-instant tie: Current picks the smaller Entry.Id", Scheduler.Evaluate(settings, Mon(8)).Current?.Entry == small);
        Check("[quirk] CT-SCH-01 same-instant tie: Next picks the smaller Entry.Id", Scheduler.Evaluate(settings, Mon(7)).Next?.Entry == small);
    }

    private static void KeyFormat()
    {
        var settings = Settings.Defaults();
        var entry = new ScheduleEntry { Id = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"), StationId = settings.Stations[0].Id, Time = "07:05", Days = [DayOfWeek.Monday] };
        settings.Schedule.Add(entry);
        var occurrence = Scheduler.Evaluate(settings, Mon(9)).Current;
        Check("CT-SCH-02 Key is \"{Id}:yyyy-MM-ddTHH:mm\" (invariant culture)", occurrence?.Key == "0f0e0d0c-0b0a-0908-0706-050403020100:2026-09-14T07:05");

        // CF-01: the Key ignores CurrentCulture. A culture whose time separator is '.' must still give ':'. A synthetic
        // culture keeps this host-independent.
        var dotted = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        dotted.DateTimeFormat.TimeSeparator = ".";
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = dotted;
            Check("CT-SCH-02 Key is culture-invariant (CF-01)", occurrence?.Key == "0f0e0d0c-0b0a-0908-0706-050403020100:2026-09-14T07:05");
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static void EmptyDays()
    {
        var settings = Settings.Defaults();
        settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "08:00", Days = [] });
        Check("CT-SCH-03 entry with empty Days is never Current or Next",
            Enumerable.Range(0, 7).All(d => Scheduler.Evaluate(settings, Mon(8).AddDays(d)) == (null, null)));
    }

    private static void WeeklyWrap()
    {
        var settings = Settings.Defaults();
        settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday] });
        var (current, next) = Scheduler.Evaluate(settings, Mon(7, 59));
        Check("CT-SCH-04 weekly slot at Mon 07:59: Current is the previous Monday 08:00", current?.At == Mon(7, 59) - new TimeSpan(6, 23, 59, 0));
        Check("CT-SCH-04 weekly slot at Mon 07:59: Next is today 08:00", next?.At == Mon(8));
    }

    private static void KindPreserved()
    {
        var settings = Settings.Defaults();
        settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday] });
        // Kind is only a tag here; nothing converts, so no host zone is consulted.
        bool KindFollowsNow(DateTimeKind kind)
        {
            var (current, next) = Scheduler.Evaluate(settings, DateTime.SpecifyKind(Mon(10), kind));
            return current?.At.Kind == kind && next?.At.Kind == kind;
        }
        Check("CT-SCH-05 At.Kind equals now.Kind (Unspecified)", KindFollowsNow(DateTimeKind.Unspecified));
        Check("CT-SCH-05 At.Kind equals now.Kind (Local)", KindFollowsNow(DateTimeKind.Local));
        Check("CT-SCH-05 At.Kind equals now.Kind (Utc)", KindFollowsNow(DateTimeKind.Utc));
    }

    private static void TryTimeStrictness()
    {
        string[] rejected = ["08:00:00", " 08:00", "08:00 ", "08:60", "", "24:00", "8:00"];
        Check("CT-SCH-06 TryTime rejects seconds, padding, 08:60, empty, 24:00, 8:00", rejected.All(t => !Scheduler.TryTime(t, out _)));
        Check("CT-SCH-06 TryTime rejects null", !Scheduler.TryTime(null, out _));
        Check("CT-SCH-06 TryTime accepts 23:59", Scheduler.TryTime("23:59", out var t2359) && t2359 == new TimeOnly(23, 59));
    }

    private static void ConflictRules()
    {
        var existing = new ScheduleEntry { Time = "08:00", Days = [DayOfWeek.Monday] };
        List<ScheduleEntry> entries = [existing];
        Check("CT-SCH-07 disabled candidate never conflicts", !Scheduler.Conflicts(entries, new ScheduleEntry { Time = "08:00", Days = [DayOfWeek.Monday], Enabled = false }));
        existing.Enabled = false;
        Check("CT-SCH-07 disabled existing entry is ignored", !Scheduler.Conflicts(entries, new ScheduleEntry { Time = "08:00", Days = [DayOfWeek.Monday] }));
        existing.Enabled = true;
        Check("CT-SCH-07 same day, different time does not conflict", !Scheduler.Conflicts(entries, new ScheduleEntry { Time = "08:01", Days = [DayOfWeek.Monday] }));
    }

    private static void IgnoresScheduleEnabled()
    {
        var settings = Settings.Defaults();
        settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday] });
        settings.ScheduleEnabled = false;
        var (current, next) = Scheduler.Evaluate(settings, Mon(10));
        Check("CT-SCH-08 Evaluate ignores ScheduleEnabled (gating lives in ScheduleSession)", current?.At == Mon(8) && next?.At == Mon(8).AddDays(7));
    }

    /// <summary>
    /// D54: <c>ToString("HH:mm")</c> without a culture writes <c>08.30</c> on the 27 cultures whose time separator is
    /// <c>.</c>. Such slots must parse, fire and conflict like <c>08:30</c>, and <see cref="Scheduler.FormatTime"/> must
    /// write <c>08:30</c> whatever the current culture is.
    /// </summary>
    private static void DottedTimes()
    {
        Check("CT-SCH-09 TryTime accepts the culture/legacy form 08.30 as 08:30", Scheduler.TryTime("08.30", out var dotted) && dotted == new TimeOnly(8, 30));
        Check("CT-SCH-09 TryTime accepts 00.00 and 23.59", Scheduler.TryTime("00.00", out var zero) && zero == TimeOnly.MinValue
            && Scheduler.TryTime("23.59", out var late) && late == new TimeOnly(23, 59));
        string[] rejected = ["8.30", "08.3", "08.60", "24.00", " 08.30", "08.30 ", "08,30", "08.30.00", "08-30", "08:30.00"];
        Check("CT-SCH-09 TryTime rejects 8.30, 08.3, 08.60, 24.00, padding, other separators and seconds", rejected.All(t => !Scheduler.TryTime(t, out _)));

        // Whatever this host's culture data holds, every culture's "HH:mm" text parses back to the same time.
        var sample = new TimeOnly(8, 30);
        var unparsed = CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(c => !Scheduler.TryTime(sample.ToString("HH:mm", c), out var back) || back != sample).Select(c => c.Name).ToList();
        Check($"CT-SCH-09 every culture's HH:mm text parses back (unparsed: {string.Join(", ", unparsed.DefaultIfEmpty("none"))})", unparsed.Count == 0);

        var settings = Settings.Defaults();
        var slot = new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "08.30", Days = [DayOfWeek.Monday] };
        settings.Schedule.Add(slot);
        var (current, next) = Scheduler.Evaluate(settings, Mon(8, 30));
        Check("CT-SCH-09 Evaluate fires a slot stored as 08.30 at 08:30", current?.Entry == slot && current.At == Mon(8, 30) && next?.At == Mon(8, 30).AddDays(7));
        Check("CT-SCH-09 Evaluate: before 08:30 the 08.30 slot is Next", Scheduler.Evaluate(settings, Mon(8, 29)).Next?.At == Mon(8, 30));
        Check("CT-SCH-09 NextFor sees a slot stored as 08.30", Scheduler.NextFor(slot, Mon(8))?.At == Mon(8, 30));

        var colon = new ScheduleEntry { Time = "08:30", Days = [DayOfWeek.Monday, DayOfWeek.Tuesday] };
        Check("CT-SCH-10 Conflicts: an existing 08:30 and a candidate 08.30 on the same day conflict",
            Scheduler.Conflicts([colon], new ScheduleEntry { Time = "08.30", Days = [DayOfWeek.Tuesday] }));
        Check("CT-SCH-10 Conflicts: an existing 08.30 and a candidate 08:30 on the same day conflict",
            Scheduler.Conflicts([new ScheduleEntry { Time = "08.30", Days = [DayOfWeek.Monday] }], new ScheduleEntry { Time = "08:30", Days = [DayOfWeek.Monday] }));
        Check("CT-SCH-10 Conflicts: 08.30 and 08:31 do not conflict",
            !Scheduler.Conflicts([new ScheduleEntry { Time = "08.30", Days = [DayOfWeek.Monday] }], new ScheduleEntry { Time = "08:31", Days = [DayOfWeek.Monday] }));
        Check("CT-SCH-10 Conflicts: a time that does not parse never conflicts (that slot never fires)",
            !Scheduler.Conflicts([new ScheduleEntry { Time = "bad", Days = [DayOfWeek.Monday] }], new ScheduleEntry { Time = "bad", Days = [DayOfWeek.Monday] }));

        Check("CT-SCH-11 FormatTime writes invariant HH:mm (08:30, 00:00; seconds dropped)", Scheduler.FormatTime(sample) == "08:30"
            && Scheduler.FormatTime(TimeOnly.MinValue) == "00:00" && Scheduler.FormatTime(new TimeOnly(23, 59, 59)) == "23:59");
        var synthetic = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        synthetic.DateTimeFormat.TimeSeparator = ".";
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("da-DK"), CultureInfo.GetCultureInfo("fi-FI"), synthetic })
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = culture;
                var name = culture.Name.Length == 0 ? "a synthetic '.'-separator culture" : culture.Name;
                Check($"CT-SCH-11 FormatTime is 08:30 under {name}, and TryTime reads it back", Scheduler.FormatTime(sample) == "08:30"
                    && Scheduler.TryTime(Scheduler.FormatTime(sample), out var back) && back == sample);
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }
    }
}
