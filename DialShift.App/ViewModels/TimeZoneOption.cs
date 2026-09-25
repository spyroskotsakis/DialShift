using System.Globalization;
using DialShift.Core;

namespace DialShift.App.ViewModels;

/// <summary>One entry of the slot editor's time-zone picker.</summary>
public sealed class TimeZoneOption
{
    private readonly string search;

    private TimeZoneOption(string? id, string name, string detail, ZoneResolution resolution, string label, string search, TimeSpan offset = default)
    {
        Id = id;
        UtcOffset = offset;
        Name = name;
        Detail = detail;
        Resolution = resolution;
        Label = label;
        this.search = search;
    }

    /// <summary>The value saved in <see cref="ScheduleEntry.TimeZone"/>: an IANA id, a stored id kept as is, or null for local time.</summary>
    public string? Id { get; }

    /// <summary>List text, left: "Local time" or the zone id.</summary>
    public string Name { get; }

    /// <summary>List text, right: the current UTC offset, "this computer's zone" or "unknown zone".</summary>
    public string Detail { get; }

    public ZoneResolution Resolution { get; }

    /// <summary>The offset in force when the list was built (zero for local time and unknown zones); orders the list.</summary>
    internal TimeSpan UtcOffset { get; }

    public bool IsUnknown => Resolution == ZoneResolution.Unknown;

    /// <summary>What the picker's text box shows for the chosen entry, for example "Europe/Athens (UTC+03:00)".</summary>
    public string Label { get; }

    /// <summary>The picker shows <see cref="Label"/> for the chosen item and filters on it.</summary>
    public override string ToString() => Label;

    /// <summary>
    /// Whether every whitespace-separated word of <paramref name="query"/> occurs (case-insensitive) in the zone's search
    /// text (<see cref="TimeZoneCatalog.SearchText"/>: the id and its segments, the zone's names, the Windows id and
    /// display name, the country code and name) or in the offset written as "UTC+03:00" or "UTC+3". Underscores count as
    /// spaces, so "new york" finds America/New_York, and "pacific us" finds America/Los_Angeles on every OS.
    /// </summary>
    public bool Matches(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => search.Contains(TimeZoneCatalog.Normalize(word), StringComparison.Ordinal));

    internal static TimeZoneOption Local() =>
        new(null, TimeZoneChoices.LocalLabel, "this computer's zone", ZoneResolution.Local, TimeZoneChoices.LocalLabel, "local time computer");

    /// <summary>A zone from the picker's catalog, under its IANA id.</summary>
    internal static TimeZoneOption System(CatalogZone entry, DateTime utcNow)
    {
        var offset = entry.Zone.GetUtcOffset(utcNow);
        var text = TimeZoneChoices.Offset(offset);
        return new(entry.Id, entry.Id, text, ZoneResolution.Resolved, $"{entry.Id} ({text})",
            SearchText(entry.SearchText, text, ShortOffset(offset)), offset);
    }

    /// <summary>
    /// A stored id that is not in the list (another spelling or alias, a Windows id, or an id this computer cannot
    /// resolve). It is shown as it is stored, trimmed, and saving without choosing another entry keeps it unchanged.
    /// </summary>
    internal static TimeZoneOption Stored(string storedId, DateTime utcNow)
    {
        var name = storedId.Trim();
        if (Scheduler.TryResolveZone(name, out var zone) != ZoneResolution.Resolved || zone is null)
            return new(storedId, name, TimeZoneChoices.UnknownZoneText, ZoneResolution.Unknown, TimeZoneChoices.UnknownZone(name),
                SearchText(name, TimeZoneChoices.UnknownZoneText));
        var system = System(new CatalogZone(name, zone, TimeZoneCatalog.SearchText(name, zone)), utcNow);
        return new(storedId, name, system.Detail, ZoneResolution.Resolved, system.Label, system.search, system.UtcOffset);
    }

    private static string ShortOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{abs.Hours}:{abs.Minutes:00}");
    }

    private static string SearchText(params string[] parts) => TimeZoneCatalog.Normalize(string.Join(' ', parts));
}

/// <summary>
/// Builds the slot editor's time-zone list (brief 2 §4.5, §6, QA-B4). Only IANA ids are offered (<see cref="TimeZoneCatalog"/>):
/// on macOS the system zones as they are; on Windows every IANA id mapped to each Windows zone, so Europe/Athens and
/// Europe/Bucharest are both there. Nothing here compares with <c>TimeZoneInfo.Local.Id</c>.
/// </summary>
public static class TimeZoneChoices
{
    public const string LocalLabel = "Local time";
    public const string UnknownZoneText = "unknown zone";

    /// <summary>"Europe/Foo (unknown zone)": a stored id this computer cannot resolve (the slot runs on local time).</summary>
    public static string UnknownZone(string trimmedId) => $"{trimmedId} ({UnknownZoneText})";

    /// <summary>"UTC+03:00", "UTC-05:00", "UTC+05:45".</summary>
    public static string Offset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "UTC-" : "UTC+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The picker list for this computer's zones (the catalog is built once per process). See
    /// <see cref="Build(IEnumerable{TimeZoneInfo}, DateTimeOffset, string?, out TimeZoneOption)"/>.
    /// </summary>
    public static IReadOnlyList<TimeZoneOption> Build(DateTimeOffset now, string? storedId, out TimeZoneOption selected) =>
        Build(TimeZoneCatalog.System, now, storedId, out selected);

    /// <summary>
    /// "Local time" first, then the stored id when it is not in the list (see <see cref="TimeZoneOption.Stored"/>), then
    /// the zones under their IANA ids (<see cref="TimeZoneCatalog.Load"/>), one entry per id, ordered by current UTC offset
    /// and then id.
    /// </summary>
    /// <param name="systemZones">The zones to offer; a seam for tests (for example zones with Windows ids).</param>
    /// <param name="now">Offsets are the ones in force at this instant, so a zone in daylight time shows its summer offset.</param>
    /// <param name="storedId">The slot's current <see cref="ScheduleEntry.TimeZone"/>; null, empty or whitespace is local time.</param>
    /// <param name="selected">The entry for <paramref name="storedId"/>: "Local time", a list entry with the same (trimmed) id, or the added stored entry.</param>
    public static IReadOnlyList<TimeZoneOption> Build(IEnumerable<TimeZoneInfo> systemZones, DateTimeOffset now, string? storedId, out TimeZoneOption selected) =>
        Build(TimeZoneCatalog.Load(systemZones), now, storedId, out selected);

    private static IReadOnlyList<TimeZoneOption> Build(IReadOnlyList<CatalogZone> catalog, DateTimeOffset now, string? storedId, out TimeZoneOption selected)
    {
        var utcNow = now.UtcDateTime;
        var local = TimeZoneOption.Local();
        var zones = catalog.ToDictionary(z => z.Id, z => TimeZoneOption.System(z, utcNow), StringComparer.Ordinal);
        var sorted = zones.Values
            .OrderBy(o => o.UtcOffset)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.Ordinal);

        var options = new List<TimeZoneOption> { local };
        selected = local;
        if (!string.IsNullOrWhiteSpace(storedId))
        {
            if (zones.TryGetValue(storedId.Trim(), out var listed)) selected = listed;
            else options.Add(selected = TimeZoneOption.Stored(storedId, utcNow));
        }
        options.AddRange(sorted);
        return options;
    }
}
