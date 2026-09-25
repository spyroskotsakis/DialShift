using System.Globalization;
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
    /// <summary>FM, kHz and band-None values (§3.3 step 3), including digit strings shared by FM and kHz (101.7 / 1017).</summary>
    private static readonly string[] Frequencies = ["101.5", "101.7", "98.4", "1593", "", "88.0", "104.3", "1017", "10", "101.0", "108.5", "149", "Shortwave"];
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
    /// Greek, Turkish I, local-name and city hits, frequency queries of every band (D79; valid and not), and a query nothing matches.</summary>
    public static readonly string?[] Queries =
        [null, "", "  ", "radio", "RADIO", "ra", "nova", "münchen", "munchen", "αθηνα", "ΑΘΉΝΑ", "κοσμος", "istanbul", "İZMİR", "izmir",
         "cherie", "strasse", "local", "novara", "101.5", "1015", "101,5 FM", "101", "10", "1593 kHz", "98", "1", "12345", "101.555", "zzz",
         "FM 101.5", "101.50", "101.7", "1017", "AM 1017", "1017 kHz", "101.", "FM 101", "101.0", "101.00", "108", "108.5", "149", "AM 101.7",
         "UKW 101.5", "fm", "1017 \u212Ahz"];

    /// <summary>Filter combinations for the same checks: none, each field alone, several ANDed, and a value nothing has.</summary>
    public static readonly CatalogFilters[] FilterSets =
        [CatalogFilters.None, new(Country: "DE"), new(City: "München"), new(Type: "Music"), new(Genre: "Pop"), new(Language: "Greek"),
         new(Country: "GR", Type: "Music"), new(Country: "DE", City: "Munchen", Type: "News", Genre: "News", Language: "German"), new(Country: "XX")];

    // ─── Reference implementation of §3.3 (the oracle) ───

    /// <summary>§3.3's frequency-query pattern (D79), written out again from the contract rather than taken from Core.
    /// Groups: 1 the leading band token L, 2 the integer digits I, 3 the separator S, 4 the decimals R, 5 the trailing token T.</summary>
    private static readonly Regex FrequencyQuery = new(@"^(?:(fm|am)\s*)?([0-9]{2,4})(?:([.,])([0-9]{0,2}))?(?:\s*(fm|mhz|am|khz))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>§3.3 step 1 and 2 read literally: the digits D and the query band (None = Any), or null when
    /// <paramref name="text"/> is not a frequency query (no match, or FM and kHz both implied).</summary>
    private static (string Digits, FrequencyBand Band)? ReferenceFrequency(string? text)
    {
        var match = FrequencyQuery.Match((text ?? "").Trim());
        if (text is null || !match.Success) return null;
        // ToLowerInvariant, not OrdinalIgnoreCase: the case-insensitive pattern equates the Kelvin sign U+212A with k, and
        // only lower-casing maps it to k (OrdinalIgnoreCase upper-cases, which leaves U+212A as it is).
        bool Token(int group, params string[] words) => words.Any(w => match.Groups[group].Value.ToLowerInvariant() == w);
        var fm = match.Groups[3].Success || Token(1, "fm") || Token(5, "fm", "mhz");
        var khz = Token(1, "am") || Token(5, "am", "khz");
        if (fm && khz) return null;
        var decimals = match.Groups[4].Value;
        if (decimals.Length == 2 && decimals[1] == '0') decimals = decimals[..1];
        return (match.Groups[2].Value + decimals, fm ? FrequencyBand.Fm : khz ? FrequencyBand.Kilohertz : FrequencyBand.None);
    }

    /// <summary>§3.3 step 3's entry band read literally (Core's <c>BandOf</c> is pinned by its own CAT-07 checks).</summary>
    private static FrequencyBand ReferenceBand(string frequencyFm) =>
        !decimal.TryParse(frequencyFm, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? FrequencyBand.None
        : v >= 64 && v <= 108 ? FrequencyBand.Fm
        : !frequencyFm.Contains('.') && v >= 150 ? FrequencyBand.Kilohertz
        : FrequencyBand.None;

    /// <summary>
    /// Every match of <paramref name="text"/> and <paramref name="filters"/> in §3.3's total order, computed the slow,
    /// obvious way: a tier per entry from the contract's table, then a stable LINQ sort by the contract's keys. Only
    /// <c>Fold</c> comes from Core; the CAT-06 checks pin it separately.
    /// </summary>
    public static List<StationCatalogEntry> Reference(IReadOnlyList<StationCatalogEntry> entries, string? text, CatalogFilters filters)
    {
        var q = StationCatalogQuery.Fold(text ?? "");
        var frequency = ReferenceFrequency(text);

        int? Tier(StationCatalogEntry e)
        {
            if (q.Length == 0) return 0;
            var name = StationCatalogQuery.Fold(e.Name);
            if (name.StartsWith(q, StringComparison.Ordinal)) return 0;
            if (name.Contains(q, StringComparison.Ordinal)) return 1;
            if (StationCatalogQuery.Fold(e.NameLocal).Contains(q, StringComparison.Ordinal) || StationCatalogQuery.Fold(e.City).Contains(q, StringComparison.Ordinal)) return 2;
            var f = new string(e.FrequencyFm.Where(char.IsAsciiDigit).ToArray());
            return frequency is { } fq && f.Length > 0 && f.StartsWith(fq.Digits, StringComparison.Ordinal)
                   && (fq.Band == FrequencyBand.None || fq.Band == ReferenceBand(e.FrequencyFm)) ? 3 : null;
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
