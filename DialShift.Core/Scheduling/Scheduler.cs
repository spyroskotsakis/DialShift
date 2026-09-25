using System.Globalization;

namespace DialShift.Core;

public static class Scheduler
{
    public static bool TryTime(string text, out TimeOnly time) => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    // Local wall-clock time: a repeated DST hour has the same key and does not fire twice.
    // A skipped hour is caught up on the first tick after the clock jump, like wake from sleep.
    public static (Occurrence? Current, Occurrence? Next) Evaluate(Settings settings, DateTime now)
    {
        var ids = settings.Stations.Select(s => s.Id).ToHashSet();
        var occurrences = new List<Occurrence>();
        foreach (var entry in settings.Schedule.Where(e => e.Enabled && ids.Contains(e.StationId)))
        {
            if (!TryTime(entry.Time, out var time)) continue;
            for (var offset = -7; offset <= 7; offset++)
            {
                var date = now.Date.AddDays(offset);
                if (entry.Days.Contains(date.DayOfWeek)) occurrences.Add(new(entry, date.Add(time.ToTimeSpan())));
            }
        }
        return (occurrences.Where(o => o.At <= now).OrderByDescending(o => o.At).ThenBy(o => o.Entry.Id).FirstOrDefault(),
            occurrences.Where(o => o.At > now).OrderBy(o => o.At).ThenBy(o => o.Entry.Id).FirstOrDefault());
    }

    public static bool Conflicts(IEnumerable<ScheduleEntry> entries, ScheduleEntry candidate) => candidate.Enabled &&
        entries.Any(e => e.Id != candidate.Id && e.Enabled && e.Time == candidate.Time && e.Days.Intersect(candidate.Days).Any());
}
