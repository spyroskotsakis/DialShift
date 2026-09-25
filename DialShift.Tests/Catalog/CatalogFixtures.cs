using System.Text.RegularExpressions;
using DialShift.Core.Catalog;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// Small deterministic in-code catalogs for the <c>Catalog</c> suite, and an independent reference implementation of
/// docs/catalog-contracts.md §3.3 that the query engine's heap selection is compared against. Nothing here reads
/// <c>data/output/app-catalog.json</c>: the checks of the checked-in file are CAT-01/02/04's, not the query rows'.
/// </summary>
internal static class CatalogFixtures
{
    /// <summary>One entry; every field not given keeps the record's default (""/null/false). The stream URL is derived
    /// from the name when not given, so two entries with the same name and no URL share one.</summary>
    public static StationCatalogEntry E(string name, string country = "DE", string? url = null, int? votes = null,
        string nameLocal = "", string city = "", string frequency = "", string type = "", string genre = "",
        string language = "", string countryLabel = "", string region = "", string notes = "", string tag = "") => new()
    {
        Name = name,
        NameLocal = nameLocal,
        Country = country,
        CountryLabel = countryLabel,
        City = city,
        Region = region,
        FrequencyFm = frequency,
        Type = type,
        Genre = genre,
        Language = language,
        StreamUrl = url ?? "https://stream.example.org/" + Uri.EscapeDataString(name),
        Votes = votes,
        Notes = notes,
        Tag = tag,
    };

    public static StationCatalogIndex Index(params StationCatalogEntry[] entries) => new(entries);

    public static CatalogSearchResult Search(StationCatalogIndex index, string? text, CatalogFilters? filters = null, int cap = StationCatalogQuery.DefaultCap) =>
        StationCatalogQuery.Search(index, text, filters ?? CatalogFilters.None, cap);

    /// <summary>The result's names in rank order.</summary>
    public static string[] Names(CatalogSearchResult result) => result.Items.Select(e => e.Name).ToArray();

    /// <summary>
    /// <see cref="Check"/> that <paramref name="actual"/> is exactly <paramref name="expected"/> (order included). On a
    /// mismatch both sequences are printed first, so the failure report carries expected vs actual.
    /// </summary>
    public static void CheckSequence<T>(string name, IEnumerable<T> actual, IEnumerable<T> expected)
    {
        var a = actual.ToList();
        var e = expected.ToList();
        var same = a.SequenceEqual(e);
        if (!same) Console.WriteLine($"  expected: [{string.Join(" | ", e)}]\n  actual:   [{string.Join(" | ", a)}]");
        Check(name, same);
    }

    /// <summary>For each (query, expected names) pair, the search's names equal the expectation; every mismatch is printed.</summary>
    public static void CheckQueries(string name, StationCatalogIndex index, CatalogFilters? filters, params (string? Query, string[] Names)[] cases)
    {
        var ok = true;
        foreach (var (query, expected) in cases)
        {
            var result = Search(index, query, filters);
            var actual = Names(result);
            if (actual.SequenceEqual(expected) && result.TotalCount == expected.Length) continue;
            ok = false;
            Console.WriteLine($"  query {Show(query)}: expected [{string.Join(" | ", expected)}] total {expected.Length}; actual [{string.Join(" | ", actual)}] total {result.TotalCount}");
        }
        Check(name, ok);
    }

    /// <summary>A query as printed in a failure: quoted, with control and surrogate characters escaped.</summary>
    public static string Show(string? text) =>
        text is null ? "null" : "\"" + string.Concat(text.Select(c => char.IsControl(c) || char.IsSurrogate(c) ? $"\\u{(int)c:X4}" : c.ToString())) + "\"";

    // ─── A generated catalog for the reference and determinism checks ───

    private static readonly string[] Prefixes = ["Radio", "Ράδιο", "Antenne", "Chérie", "Nova", "İstanbul", "Straße", "Hit", "Κόσμος", "Fréquence", "Ökowelle", "radio"];
    private static readonly string[] Suffixes = ["FM", "Eins", "Plus", "101.5", "Αθήνα", "Classic", "Live", "", "München", "Nova"];
    private static readonly string[] Cities = ["München", "Munchen", "Αθήνα", "Paris", "İzmir", "Köln", "", "Lyon", "Novara"];
    private static readonly (string Code, string Label)[] Countries = [("DE", "Germany"), ("GR", "Greece"), ("FR", "France"), ("TR", ""), ("Internet", "Internet (collections)")];
    private static readonly string[] Frequencies = ["101.5", "101.7", "98.4", "1593", "", "88.0", "104.3", "1017", "10"];
    private static readonly string[] Types = ["Music", "News", "", "Talk"];
    private static readonly string[] Genres = ["Pop", "Schlager", "Jazz", "", "News"];
    private static readonly string[] Languages = ["German", "Greek", "French", "Turkish", ""];
    private static readonly int?[] Votes = [null, 0, 1, 5, 5, 42, 100, 1000];

    /// <summary>
    /// <paramref name="count"/> entries from a seeded <see cref="Random"/> (the seeded algorithm is fixed across
    /// platforms). Names, votes and frequencies repeat on purpose, so every tie-break is exercised; stream URLs are
    /// unique, so no two entries tie on every key and the order does not fall back to the catalog position.
    /// </summary>
    public static List<StationCatalogEntry> Generated(int count, int seed)
    {
        var random = new Random(seed);
        T Pick<T>(T[] from) => from[random.Next(from.Length)];
        var entries = new List<StationCatalogEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var (code, label) = Pick(Countries);
            entries.Add(E($"{Pick(Prefixes)} {Pick(Suffixes)}".Trim(), code, url: $"https://s{random.Next(100_000)}-{i}.example.org/live",
                votes: Pick(Votes), nameLocal: random.Next(4) == 0 ? Pick(Prefixes) + " local" : "", city: Pick(Cities),
                frequency: Pick(Frequencies), type: Pick(Types), genre: Pick(Genres), language: Pick(Languages), countryLabel: label));
        }
        return entries;
    }

    /// <summary>Seeded Fisher–Yates shuffle of a copy.</summary>
    public static List<StationCatalogEntry> Shuffled(IReadOnlyList<StationCatalogEntry> entries, int seed)
    {
        var random = new Random(seed);
        var copy = entries.ToList();
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    /// <summary>Text queries for the reference and determinism checks: empty, name prefixes and substrings, diacritics,
    /// Greek, Turkish I, local-name and city hits, frequency queries (valid and not), and a query nothing matches.</summary>
    public static readonly string?[] Queries =
        [null, "", "  ", "radio", "RADIO", "ra", "nova", "münchen", "munchen", "αθηνα", "ΑΘΉΝΑ", "κοσμος", "istanbul", "İZMİR", "izmir",
         "cherie", "strasse", "local", "novara", "101.5", "1015", "101,5 FM", "101", "10", "1593 kHz", "98", "1", "12345", "101.555", "zzz"];

    /// <summary>Filter combinations for the same checks: none, each field alone, several ANDed, and a value nothing has.</summary>
    public static readonly CatalogFilters[] FilterSets =
        [CatalogFilters.None, new(Country: "DE"), new(City: "München"), new(Type: "Music"), new(Genre: "Pop"), new(Language: "Greek"),
         new(Country: "GR", Type: "Music"), new(Country: "DE", City: "Munchen", Type: "News", Genre: "News", Language: "German"), new(Country: "XX")];

    // ─── Reference implementation of §3.3 (the oracle) ───

    /// <summary>§3.3's frequency-query pattern, written out again from the contract rather than taken from Core.</summary>
    private static readonly Regex FrequencyQuery = new(@"^([0-9]{2,4})(?:[.,]([0-9]{0,2}))?(?:\s*(?:fm|mhz|khz))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every match of <paramref name="text"/> and <paramref name="filters"/> in §3.3's total order, computed the slow,
    /// obvious way: a tier per entry from the contract's table, then a stable LINQ sort by the contract's keys. Only
    /// <c>Fold</c> comes from Core; the CAT-06 checks pin it separately.
    /// </summary>
    public static List<StationCatalogEntry> Reference(IReadOnlyList<StationCatalogEntry> entries, string? text, CatalogFilters filters)
    {
        var q = StationCatalogQuery.Fold(text ?? "");
        var match = FrequencyQuery.Match((text ?? "").Trim());
        var digits = text is not null && match.Success ? match.Groups[1].Value + match.Groups[2].Value : null;

        int? Tier(StationCatalogEntry e)
        {
            if (q.Length == 0) return 0;
            var name = StationCatalogQuery.Fold(e.Name);
            if (name.StartsWith(q, StringComparison.Ordinal)) return 0;
            if (name.Contains(q, StringComparison.Ordinal)) return 1;
            if (StationCatalogQuery.Fold(e.NameLocal).Contains(q, StringComparison.Ordinal) || StationCatalogQuery.Fold(e.City).Contains(q, StringComparison.Ordinal)) return 2;
            var f = new string(e.FrequencyFm.Where(char.IsAsciiDigit).ToArray());
            return digits is not null && f.Length > 0 && f.StartsWith(digits, StringComparison.Ordinal) ? 3 : null;
        }

        static bool Is(string? filter, string value) => filter is null || string.Equals(filter, value, StringComparison.Ordinal);

        return entries
            .Select((e, position) => (e, position, tier: Tier(e)))
            .Where(x => x.tier is not null && Is(filters.Country, x.e.Country) && Is(filters.City, x.e.City) && Is(filters.Type, x.e.Type)
                        && Is(filters.Genre, x.e.Genre) && Is(filters.Language, x.e.Language))
            .OrderBy(x => x.tier)
            .ThenByDescending(x => x.e.Votes ?? 0)
            .ThenBy(x => StationCatalogQuery.Fold(x.e.Name), StringComparer.Ordinal)
            .ThenBy(x => x.e.Name, StringComparer.Ordinal)
            .ThenBy(x => x.e.Country, StringComparer.Ordinal)
            .ThenBy(x => x.e.StreamUrl, StringComparer.Ordinal)
            .ThenBy(x => x.position)
            .Select(x => x.e)
            .ToList();
    }
}
