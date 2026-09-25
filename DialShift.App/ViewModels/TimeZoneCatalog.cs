using System.Globalization;

namespace DialShift.App.ViewModels;

/// <summary>One zone the picker offers: its IANA id, the zone that supplies its rules and names, and its search text.</summary>
/// <param name="SearchText">Lower case, underscores as spaces (see <see cref="TimeZoneCatalog.SearchText"/>); offsets are added per list.</param>
internal sealed record CatalogZone(string Id, TimeZoneInfo Zone, string SearchText);

/// <summary>
/// The IANA zones the slot editor offers and what each one can be found by (brief 2 §4.5, QA-B4, UI-D3).
/// </summary>
/// <remarks>
/// <para>
/// Windows lists one zone per Windows id ("GTB Standard Time"), and <see cref="TimeZoneInfo.TryConvertWindowsIdToIanaId(string, out string?)"/>
/// gives one IANA id for it (Europe/Bucharest). Every other IANA id CLDR maps to that Windows zone comes from the
/// region-aware overload, asked once per ISO region this computer knows (Europe/Athens for GR, Asia/Nicosia for CY). A
/// zone whose id is already IANA (every zone on macOS) is offered under its own id and never expanded, so nothing is
/// added or dropped there. Only ids <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> resolves are kept, and a Windows id
/// is never offered.
/// </para>
/// <para>
/// The search text joins the id and its segments, the zone's display, standard and daylight names, the Windows id and the
/// Windows zone's display name, and the country (ISO code and English name) from the region mapping and, where the OS has
/// it, the tz database's <c>zone.tab</c>. A display name that lists several cities ("Athens, Bucharest") loses the cities
/// that are other entries of the same Windows zone, so "athens" finds Europe/Athens and not also Europe/Bucharest.
/// </para>
/// </remarks>
internal static class TimeZoneCatalog
{
    /// <summary>
    /// Ids CLDR's Windows mapping (what ICU returns) still spells the old way, with the name tzdata uses now. Without it
    /// Windows would offer (and store) Asia/Calcutta where macOS stores Asia/Kolkata, and the two never meet in a conflict
    /// check (QA-N5). A test checks this table against the macOS zone list.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Renamed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Africa/Asmera"] = "Africa/Asmara",
        ["America/Buenos_Aires"] = "America/Argentina/Buenos_Aires",
        ["America/Coral_Harbour"] = "America/Atikokan",
        ["America/Godthab"] = "America/Nuuk",
        ["America/Indianapolis"] = "America/Indiana/Indianapolis",
        ["Asia/Calcutta"] = "Asia/Kolkata",
        ["Asia/Katmandu"] = "Asia/Kathmandu",
        ["Asia/Rangoon"] = "Asia/Yangon",
        ["Asia/Saigon"] = "Asia/Ho_Chi_Minh",
        ["Atlantic/Faeroe"] = "Atlantic/Faroe",
        ["Europe/Kiev"] = "Europe/Kyiv",
        ["Pacific/Enderbury"] = "Pacific/Kanton",
        ["Pacific/Ponape"] = "Pacific/Pohnpei",
        ["Pacific/Truk"] = "Pacific/Chuuk"
    };

    private static readonly Lazy<IReadOnlyList<string>> regions = new(LoadRegions);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> zoneTab = new(LoadZoneTab);
    private static readonly Lazy<IReadOnlyList<CatalogZone>> system = new(() => Load(TimeZoneInfo.GetSystemTimeZones()));

    /// <summary>This computer's zones, built on first use (well under 200 ms) and kept for the life of the process.</summary>
    public static IReadOnlyList<CatalogZone> System => system.Value;

    /// <summary>The ISO region codes of this computer's specific cultures, the regions the Windows mapping is asked for.</summary>
    internal static IReadOnlyList<string> Regions => regions.Value;

    /// <summary>
    /// The entries for <paramref name="zones"/>, one per IANA id (the first zone that yields an id supplies it). A zone with
    /// a Windows id becomes every IANA id mapped to it (<see cref="IanaIds"/>); a zone with an IANA id stays as it is; any
    /// other zone (a Windows id with no IANA mapping) is left out, so no Windows id is ever offered (QA-B4).
    /// </summary>
    public static IReadOnlyList<CatalogZone> Load(IEnumerable<TimeZoneInfo> zones)
    {
        var mappings = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        var entries = new List<CatalogZone>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in zones)
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out _))
                foreach (var id in Mapped(zone.Id).Keys) Add(id, zone);
            else if (zone.HasIanaId) Add(zone.Id, zone);
        }
        return entries;

        IReadOnlyDictionary<string, IReadOnlyList<string>> Mapped(string windowsId) =>
            mappings.TryGetValue(windowsId, out var ids) ? ids : mappings[windowsId] = IanaIds(windowsId);

        void Add(string id, TimeZoneInfo zone)
        {
            if (seen.Add(id)) entries.Add(new CatalogZone(id, zone, SearchText(id, zone, Mapped)));
        }
    }

    /// <summary>
    /// The IANA ids mapped to Windows zone <paramref name="windowsId"/>: its default id first, then each id a region maps it
    /// to, in current tzdata spelling (<see cref="Renamed"/>), keeping only ids this computer resolves. Each id carries the
    /// regions that map to it; the default id carries none, because a region with no mapping of its own also gets it.
    /// Empty for an id that is not a Windows id.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> IanaIds(string windowsId)
    {
        var ids = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var fallback)) return new Dictionary<string, IReadOnlyList<string>>();
        ids[fallback] = [];
        foreach (var region in Regions)
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, region, out var iana) && iana != fallback)
            {
                if (!ids.TryGetValue(iana, out var list)) ids[iana] = list = [];
                list.Add(region);
            }
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (iana, list) in ids)
        {
            var current = Renamed.TryGetValue(iana, out var renamed) && Resolves(renamed) ? renamed : iana;
            if (!Resolves(current)) continue;
            if (result.TryGetValue(current, out var merged)) merged.AddRange(list);
            else result[current] = list;
        }
        return result.ToDictionary(p => p.Key, IReadOnlyList<string> (p) => p.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// What <paramref name="id"/> can be found by, lower case with underscores as spaces: the id and its segments
    /// ("america los angeles"), <paramref name="zone"/>'s names, the Windows id and that zone's display name, and the
    /// country code and name. Cities of the Windows zone's other entries are removed from the names.
    /// </summary>
    internal static string SearchText(string id, TimeZoneInfo zone) => SearchText(id, zone, IanaIds);

    private static string SearchText(string id, TimeZoneInfo zone, Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> ianaIds)
    {
        var city = City(id);
        var names = new List<string> { zone.DisplayName, zone.StandardName, zone.DaylightName };
        var countries = new SortedSet<string>(StringComparer.Ordinal);
        var otherCities = new List<string>();
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
        {
            names.Add(windowsId);
            if (windowsId != zone.Id && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var windowsZone)) names.Add(windowsZone.DisplayName);
            var mapped = ianaIds(windowsId);
            if (mapped.TryGetValue(id, out var mappedRegions)) countries.UnionWith(mappedRegions);
            otherCities.AddRange(mapped.Keys.Select(City).Where(c => !c.Equals(city, StringComparison.OrdinalIgnoreCase)));
        }
        if (zoneTab.Value.TryGetValue(id, out var code)) countries.Add(code);

        var parts = new List<string> { id, id.Replace('/', ' ') };
        parts.AddRange(names.Select(n => WithoutCities(Normalize(n), otherCities)));
        foreach (var country in countries) parts.AddRange([country, CountryName(country)]);
        return Normalize(string.Join(' ', parts));
    }

    /// <summary>Lower case, underscores as spaces: the form search text and typed words are compared in.</summary>
    internal static string Normalize(string text) => text.Replace('_', ' ').ToLowerInvariant();

    private static string City(string id) => Normalize(id[(id.LastIndexOf('/') + 1)..]);

    private static bool Resolves(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    /// <summary>Removes each of <paramref name="cities"/> (lower case) where it stands as whole words in <paramref name="text"/>.</summary>
    private static string WithoutCities(string text, IReadOnlyList<string> cities)
    {
        foreach (var city in cities)
        {
            var at = 0;
            while ((at = text.IndexOf(city, at, StringComparison.Ordinal)) >= 0)
            {
                var end = at + city.Length;
                if ((at == 0 || !char.IsLetter(text[at - 1])) && (end == text.Length || !char.IsLetter(text[end])))
                    text = text.Remove(at, city.Length).Insert(at, " ");
                else at = end;
            }
        }
        return text;
    }

    private static string CountryName(string code)
    {
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return ""; }
    }

    private static IReadOnlyList<string> LoadRegions()
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var code = new RegionInfo(culture.Name).TwoLetterISORegionName;
                if (code.Length == 2 && char.IsAsciiLetterUpper(code[0]) && char.IsAsciiLetterUpper(code[1])) codes.Add(code);
            }
            catch (ArgumentException) { }
        }
        return [.. codes];
    }

    /// <summary>
    /// The country of each id in the tz database's <c>zone.tab</c> (macOS and Linux, under <c>TZDIR</c> or
    /// /usr/share/zoneinfo). Windows has no such file; there the region mapping alone names countries.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadZoneTab()
    {
        var countries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows()) return countries;
        var file = Path.Combine(Environment.GetEnvironmentVariable("TZDIR") is { Length: > 0 } dir ? dir : "/usr/share/zoneinfo", "zone.tab");
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith('#')) continue;
                var fields = line.Split('\t');
                if (fields.Length >= 3 && fields[0].Length == 2) countries.TryAdd(fields[2], fields[0]);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return countries;
    }
}
