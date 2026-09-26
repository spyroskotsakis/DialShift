using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DialShift.App.Services;
using DialShift.Core;
using DialShift.Core.Catalog;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// The checked-in catalog as the app sees it (docs/catalog-contracts.md §2, §4.4, §8): CAT-02's test-output half (the
/// Content item copies <c>app-catalog.json</c> next to the test binary through the project reference), CAT-04's "the
/// default location in the test process loads the real file", and CAT-01's export contract, read independently of the
/// provider and compared with it and with <c>data/canonical/*.csv</c>, including the D84 shape of <c>language</c> (a
/// normalized list of single names), D88's rule 9 on <c>frequency_fm</c>, the D86/D90 one spelling per place among the
/// city values, and CAT-08's Language filter over the real entries.
/// </summary>
/// <remarks>
/// These are the only catalog checks that read the real file. They assert its contract, never its contents: the station
/// count is whatever the JSON says, and the language checks are shape checks that hold no language name (D84, CAT-17: the
/// names and their mapping are <c>data/languages.yaml</c>'s and the pipeline self-test's). The city checks are patterns
/// too (fold-equal values, stream-detail tokens), plus spelling-variant sets of place names that must not co-occur: they
/// name no station and pick no winner, which spelling stays is <c>data/countries/*.yaml</c>'s (D90). The repository checks (byte equality with <c>data/output/</c>, the canonical CSVs)
/// SKIP with a reason when the repository root (the folder with <c>DialShift.slnx</c>) is not above the test binary.
/// </remarks>
internal static class CatalogExportContractTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>§2.1: exactly these keys, in this order.</summary>
    private static readonly string[] Keys =
        ["name", "name_local", "country", "country_label", "city", "region", "frequency_fm", "type", "genre", "language",
         "internet_only", "stream_url", "codec", "bitrate", "votes", "notes", "logo", "tag"];

    private const string CollectionCountry = "Internet";
    private const string CollectionLabel = "Internet (collections)";
    private const int MaxUrlLength = 2_048;

    /// <summary>A generous ceiling on the distinct language names (D84: 42 at e7e86d7, the spec lane's simulation 43, from
    /// 170 raw combinations before). A value far above it means the lists are no longer split or mapped.</summary>
    private const int MaxLanguageNames = 60;

    /// <summary>One language name (§2.1, §2.3 rule 7): non-white-space runs joined by single spaces (so trimmed, no doubled
    /// or other white space), and no ',' or ';'.</summary>
    private static readonly Regex LanguageName = new(@"^[^\s,;]+(?: [^\s,;]+)*$", RegexOptions.CultureInvariant);

    /// <summary>The provenance label of a raw radio-browser tag note in the CSVs (<c>common.RB_TAGS_LABEL</c>), and the app's label (D86).</summary>
    private const string RawTagsLabel = "tags:";
    private const string TagsLabel = "Tags: ";

    public static async Task RunAsync()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, CatalogProvider.FileName);
        Check("CAT-02 the test output folder holds app-catalog.json (the App's Content item flows through the project reference)", File.Exists(copied));
        var root = RepositoryRoot();
        var source = root is null ? null : Path.Combine(root, "data", "output", CatalogProvider.FileName);
        if (source is null || !File.Exists(source))
            Skip("CAT-02 the test output's app-catalog.json is byte-identical to data/output/app-catalog.json", "no repository root with data/output above the test binary");
        else
            Check("CAT-02 the test output's app-catalog.json is byte-identical to data/output/app-catalog.json",
                File.ReadAllBytes(copied).AsSpan().SequenceEqual(File.ReadAllBytes(source)));

        // CAT-04: the default location (unset override, the test binary's folder) loads the real file.
        var location = CatalogProvider.ResolveLocation(_ => null, AppContext.BaseDirectory);
        var log = new RecordingAppLog();
        var result = await new CatalogProvider(location, log).GetCatalogAsync().WaitAsync(Bound);
        if (result.State != CatalogLoadState.Loaded)
            Console.WriteLine($"  actual: {result.State} \"{result.Message}\"; log: {string.Join(" | ", log.Entries.Select(e => e.EventName + ": " + e.Message))}");
        Check("CAT-04 the default location (DIALSHIFT_CATALOG_PATH unset) resolves to the test binary's app-catalog.json (AppFolder) and loads: Loaded, " +
              "one catalog.loaded naming [app folder], no warning",
            location == new CatalogLocation(copied, CatalogLocationSource.AppFolder, null) && result.State == CatalogLoadState.Loaded
            && log.Entries.Count == 1 && log.Entries[0].EventName == "catalog.loaded" && log.Entries[0].Message.Contains(" [app folder] in ", StringComparison.Ordinal));

        var bytes = File.ReadAllBytes(copied);
        FileLayout(bytes);
        using var document = JsonDocument.Parse(bytes);
        var stations = Document(document.RootElement, result);
        Entries(stations, result.Catalog.Entries);
        Order(result.Catalog.Entries);
        Languages(result.Catalog.Entries);
        TagNotes(result.Catalog.Entries);
        Frequencies(result.Catalog.Entries);
        Cities(result.Catalog.Entries);
        LanguageFilter(result.Catalog.Entries);
        Canonical(root, result.Catalog.Entries);
    }

    /// <summary>The folder holding DialShift.slnx above the test binary, or null.</summary>
    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DialShift.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>§2.1 "File": UTF-8 without BOM, \n line ends, a header line, one station per line, then ]} and a final \n.</summary>
    private static void FileLayout(byte[] bytes)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        var lines = text.Split('\n');
        var header = new Regex(@"^\{""schema_version"":1,""generated_utc"":""\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z"",""stations"":\[$");
        var body = lines.Skip(1).SkipLast(2).ToList();
        Check("CAT-01 the file is UTF-8 without BOM, \\n line ends only, header line up to \"stations\":[, one {…} station per line joined by \",\\n\", " +
              "then ]} and a final \\n",
            !bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) && !text.Contains('\r') && lines.Length >= 4 && header.IsMatch(lines[0])
            && lines[^2] == "]}" && lines[^1] == "" && body.Count > 0
            && body.Take(body.Count - 1).All(l => l.StartsWith('{') && l.EndsWith("},", StringComparison.Ordinal))
            && body[^1].StartsWith('{') && body[^1].EndsWith('}'));
    }

    /// <summary>The root object and the provider's view of it; returns the stations array.</summary>
    private static List<JsonElement> Document(JsonElement root, CatalogLoadResult result)
    {
        var version = root.TryGetProperty("schema_version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : (int?)null;
        var generated = root.TryGetProperty("generated_utc", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        var stations = root.TryGetProperty("stations", out var s) && s.ValueKind == JsonValueKind.Array ? s.EnumerateArray().ToList() : [];
        Check("CAT-01 the root holds exactly schema_version (the integer 1), generated_utc (yyyy-MM-ddTHH:mm:ssZ) and stations (1..10,000 entries), in that order",
            root.EnumerateObject().Select(p => p.Name).SequenceEqual(["schema_version", "generated_utc", "stations"]) && version == 1
            && generated is not null && Regex.IsMatch(generated, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
            && stations.Count is >= 1 and <= CatalogProvider.MaxEntries);
        var expectedUtc = generated is null ? (DateTimeOffset?)null
            : DateTimeOffset.ParseExact(generated, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        Console.WriteLine($"  app-catalog.json: {stations.Count} stations, generated {generated}");
        Check($"CAT-01 the provider loads every one of the JSON's {stations.Count} stations (none skipped) with GeneratedUtc = generated_utc",
            result.State == CatalogLoadState.Loaded && result.Catalog.Entries.Count == stations.Count && result.GeneratedUtc == expectedUtc);
        return stations;
    }

    /// <summary>§2.1 keys and JSON types, §2.2's URL rule, and field-for-field equality with the provider's entries.</summary>
    private static void Entries(List<JsonElement> stations, IReadOnlyList<StationCatalogEntry> loaded)
    {
        var problems = new List<string>();
        void Problem(int i, string what)
        {
            if (problems.Count < 10) Console.WriteLine($"  stations[{i}]: {what}");
            problems.Add(what);
        }

        var shapeOk = true;
        var mapped = new List<StationCatalogEntry>(stations.Count);
        for (var i = 0; i < stations.Count; i++)
        {
            var e = stations[i];
            if (e.ValueKind != JsonValueKind.Object || !e.EnumerateObject().Select(p => p.Name).SequenceEqual(Keys))
            {
                Problem(i, "not an object with exactly the 18 keys in order");
                shapeOk = false;
                continue;
            }
            foreach (var key in Keys.Except(["internet_only", "bitrate", "votes"]))
                if (e.GetProperty(key).ValueKind != JsonValueKind.String) Problem(i, key + " is not a string");
            if (e.GetProperty("internet_only").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Problem(i, "internet_only is not a boolean");
            int? Integer(string key, int min)
            {
                var value = e.GetProperty(key);
                if (value.ValueKind == JsonValueKind.Null) return null;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min) return n;
                Problem(i, $"{key} is not null or an integer >= {min}");
                return null;
            }
            string S(string key) => e.GetProperty(key).ValueKind == JsonValueKind.String ? e.GetProperty(key).GetString()! : "";
            mapped.Add(new StationCatalogEntry
            {
                Name = S("name"), NameLocal = S("name_local"), Country = S("country"), CountryLabel = S("country_label"), City = S("city"),
                Region = S("region"), FrequencyFm = S("frequency_fm"), Type = S("type"), Genre = S("genre"), Language = S("language"),
                InternetOnly = e.GetProperty("internet_only").ValueKind == JsonValueKind.True, StreamUrl = S("stream_url"), Codec = S("codec"),
                Bitrate = Integer("bitrate", 1), Votes = Integer("votes", 0), Notes = S("notes"), Logo = S("logo"), Tag = S("tag"),
            });
        }
        Check("CAT-01 every station has exactly the 18 §2.1 keys in order, with the §2.1 JSON types (strings; internet_only boolean; " +
              "bitrate null or ≥ 1; votes null or ≥ 0)", shapeOk && problems.Count == 0);

        problems.Clear();
        for (var i = 0; i < mapped.Count; i++)
        {
            var e = mapped[i];
            if (e.Name.Length == 0 || e.Country.Length == 0) Problem(i, "empty name or country");
            if (!UrlRule(e.StreamUrl)) Problem(i, $"stream_url fails the URL rule: {e.StreamUrl}");
            if (e.Logo.Length > 0 && !SettingsStore.ValidUrl(e.Logo)) Problem(i, $"logo is not \"\" or an http(s) URL: {e.Logo}");
            if (e.Tag.Length == 0) Problem(i, "empty tag");
            if (e.City == "—" || e.Region == "(unlisted)") Problem(i, "a placeholder city or region");
            if (e.CountryLabel.Length == 0) Problem(i, "empty country_label");
            foreach (var (key, value) in new[] { ("name", e.Name), ("country", e.Country), ("stream_url", e.StreamUrl), ("notes", e.Notes), ("tag", e.Tag) })
                if (value != value.Trim()) Problem(i, key + " is not trimmed");
        }
        Check("CAT-01 every entry: non-empty name and country, trimmed strings, stream_url passes the URL rule (SettingsStore.ValidUrl, ≤ 2,048 " +
              "characters, no white space or control character), logo \"\" or http(s), tag non-empty, no placeholder city/region, a country label",
            problems.Count == 0);
        var mislabelled = mapped.Where(e => (e.Country == CollectionCountry) != (e.CountryLabel == CollectionLabel)).ToList();
        foreach (var e in mislabelled.Take(5)) Console.WriteLine($"  {e.Name}: country {e.Country} with label {e.CountryLabel}");
        Check("CAT-01 collections are labelled: country Internet exactly when country_label is \"Internet (collections)\", and the file has some",
            mislabelled.Count == 0 && mapped.Any(e => e.Country == CollectionCountry));

        var duplicates = mapped.GroupBy(e => (e.Name, e.Country, e.StreamUrl)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var d in duplicates.Take(10)) Console.WriteLine($"  duplicate: {d}");
        Check("CAT-01 no duplicate (name, country, stream_url)", duplicates.Count == 0);

        var differing = mapped.Zip(loaded).Select((p, i) => (p, i)).Where(x => x.p.First != x.p.Second).ToList();
        foreach (var ((json, app), i) in differing.Take(5)) Console.WriteLine($"  stations[{i}]\n  json: {json}\n  app:  {app}");
        Check("CAT-01 the provider keeps every field exactly as the file writes it (field-for-field, in file order): the export needs no app-side repair",
            mapped.Count == loaded.Count && differing.Count == 0);
    }

    /// <summary>§2.2's URL rule: SettingsStore.ValidUrl plus BHV-52's length limit, no white space or control character.</summary>
    private static bool UrlRule(string url) =>
        url.Length is > 0 and <= MaxUrlLength && !url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) && SettingsStore.ValidUrl(url);

    /// <summary>Compares by Unicode code point (Python's str order), not UTF-16 code unit.</summary>
    private static int CompareCodePoints(string a, string b)
    {
        var x = a.EnumerateRunes();
        var y = b.EnumerateRunes();
        while (true)
        {
            bool hasX = x.MoveNext(), hasY = y.MoveNext();
            if (!hasX || !hasY) return hasX == hasY ? 0 : hasX ? 1 : -1;
            var c = x.Current.Value.CompareTo(y.Current.Value);
            if (c != 0) return c;
        }
    }

    /// <summary>§2.2: country ascending, votes descending (null as 0), name, stream_url; strings by code point.</summary>
    private static void Order(IReadOnlyList<StationCatalogEntry> entries)
    {
        static int Compare(StationCatalogEntry a, StationCatalogEntry b)
        {
            var c = CompareCodePoints(a.Country, b.Country);
            if (c == 0) c = (b.Votes ?? 0).CompareTo(a.Votes ?? 0);
            if (c == 0) c = CompareCodePoints(a.Name, b.Name);
            if (c == 0) c = CompareCodePoints(a.StreamUrl, b.StreamUrl);
            return c;
        }
        var outOfOrder = Enumerable.Range(1, Math.Max(0, entries.Count - 1)).Where(i => Compare(entries[i - 1], entries[i]) > 0).ToList();
        foreach (var i in outOfOrder.Take(5)) Console.WriteLine($"  out of order at {i}: {entries[i - 1].Name} ({entries[i - 1].Country}) before {entries[i].Name} ({entries[i].Country})");
        Check("CAT-01 order: country ascending, then votes descending (null as 0), then name, then stream_url, strings by Unicode code point",
            outOfOrder.Count == 0);
    }

    /// <summary>
    /// D86, §2.1 <c>notes</c> and §2.3 rule 8, mirrored over the checked-in file: no note is a raw radio-browser tag list
    /// (<c>tags: …</c>), and every formatted one reads <c>Tags: a, b</c>: tags joined by exactly <c>", "</c>, each trimmed,
    /// single-spaced, free of <c>,</c> and <c>;</c>, with a letter or digit, and no tag repeated ignoring case.
    /// </summary>
    private static void TagNotes(IReadOnlyList<StationCatalogEntry> entries)
    {
        var raw = entries.Where(e => e.Notes.StartsWith(RawTagsLabel, StringComparison.Ordinal)).ToList();
        foreach (var e in raw.Take(5)) Console.WriteLine($"  raw tag note: {e.Name} ({e.Country}): {CatalogFixtures.Show(e.Notes)}");
        Check($"CAT-01 D86 rule 8: no note starts with the raw radio-browser label \"{RawTagsLabel}\" (actual {raw.Count})", raw.Count == 0);

        var formatted = entries.Where(e => e.Notes.StartsWith(TagsLabel.TrimEnd(), StringComparison.Ordinal)).ToList();
        var bad = formatted.Where(e => !WellFormed(e.Notes)).ToList();
        foreach (var e in bad.Take(5)) Console.WriteLine($"  tag note: {e.Name} ({e.Country}): {CatalogFixtures.Show(e.Notes)}");
        Check($"CAT-01 D86 all {formatted.Count} tag notes read \"{TagsLabel}a, b\": tags joined by \", \", trimmed, single-spaced, free of ',' and ';', " +
              "each with a letter or digit, none repeated ignoring case",
            formatted.Count > 0 && bad.Count == 0);

        static bool WellFormed(string notes)
        {
            if (!notes.StartsWith(TagsLabel, StringComparison.Ordinal)) return false;
            var tags = notes[TagsLabel.Length..].Split(", ");
            return tags.All(t => t.Length > 0 && t == t.Trim() && !t.Contains("  ", StringComparison.Ordinal) && t.IndexOfAny([',', ';']) < 0
                                 && t.Any(char.IsLetterOrDigit))
                && tags.Select(t => t.Normalize(NormalizationForm.FormC)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Length;
        }
    }

    /// <summary>A number as §2.3 rule 9 reads one (the pipeline's <c>_FREQUENCY_NUMBER</c>): ASCII digits with at most one '.'.</summary>
    private static readonly Regex FrequencyNumber = new(@"^(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z", RegexOptions.CultureInvariant);

    /// <summary>The shape of a band word (D88, <c>data/frequency-bands.yaml</c>): one word of letters only, at most 16 characters.
    /// The words themselves are the YAML's; the check names none.</summary>
    private static readonly Regex BandWordShape = new(@"^\p{L}{1,16}\z", RegexOptions.CultureInvariant);

    /// <summary>A band word is the exception (1 entry at <c>02400f5</c>): more than this many entries means values the
    /// FM/kHz rules should cover are being listed as words instead.</summary>
    private const int MaxBandWordEntries = 5;

    /// <summary>
    /// D88, §2.1 <c>frequency_fm</c> and §2.3 rule 9, mirrored over the checked-in file: every value is <c>""</c>; an FM value
    /// written with a '.' from 64 to 108; a kHz integer (no '.') from 150 to 30,000; or a band word (<see cref="BandWordShape"/>),
    /// which at most <see cref="MaxBandWordEntries"/> entries use. Core's <c>BandOf</c> agrees on each class: FM, kHz, and
    /// neither for a band word, which the app shows as written.
    /// </summary>
    private static void Frequencies(IReadOnlyList<StationCatalogEntry> entries)
    {
        int fm = 0, khz = 0, empty = 0;
        var highestKhz = 0m;
        var bandWords = new List<string>();
        var bad = new List<StationCatalogEntry>();
        foreach (var e in entries)
        {
            var value = e.FrequencyFm;
            var number = FrequencyNumber.IsMatch(value)
                && decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n : (decimal?)null;
            var band = StationCatalogQuery.BandOf(value);
            if (value.Length == 0) empty++;
            else if (number is >= 64 and <= 108 && value.Contains('.') && band == FrequencyBand.Fm) fm++;
            else if (number is >= 150 and <= 30_000 && !value.Contains('.') && band == FrequencyBand.Kilohertz)
            {
                khz++;
                highestKhz = Math.Max(highestKhz, number.Value);
            }
            else if (number is null && BandWordShape.IsMatch(value) && band == FrequencyBand.None) bandWords.Add(value);
            else bad.Add(e);
        }
        Console.WriteLine($"  frequency_fm: {fm} FM, {khz} kHz (highest {highestKhz}), {bandWords.Count} band word ({string.Join(", ", bandWords.Distinct())}), {empty} empty");
        foreach (var e in bad.Take(10)) Console.WriteLine($"  frequency_fm {CatalogFixtures.Show(e.FrequencyFm)}: {e.Name} ({e.Country})");
        Check($"CAT-01 D88 rule 9: every frequency_fm is \"\", an FM value with a '.' (64–108), a kHz integer (150–30,000) or a band word " +
              $"(letters only, ≤ 16 characters), and Core's BandOf agrees (FM, kHz, none) (actual {bad.Count} other)",
            bad.Count == 0 && fm > 0 && khz > 0);
        Check($"CAT-01 D88 band words are the exception: at most {MaxBandWordEntries} entries use one (actual {bandWords.Count})",
            bandWords.Count <= MaxBandWordEntries);
    }

    /// <summary>
    /// Spelling-variant sets of one place name each (D90's German states: the English, German and abbreviated spellings the
    /// radio-browser data mixed before the YAML aliases gave each one spelling). Which spelling the file keeps is the YAML's
    /// choice (the one with the most rows); the check only asks that one set never shows two of its spellings.
    /// </summary>
    private static readonly string[][] PlaceSpellingVariants =
    [
        ["Bavaria", "Bayern"], ["North Rhine-Westphalia", "Nordrhein-Westfalen", "Nrw"], ["Lower Saxony", "Niedersachsen"],
        ["Saxony", "Sachsen"], ["Saxony-Anhalt", "Sachsen-Anhalt"], ["Rhineland-Palatinate", "Rheinland-Pfalz"], ["Hesse", "Hessen"]
    ];

    /// <summary>A stream detail (a bitrate, a codec or a stream format) inside a city value (D90).</summary>
    private static readonly Regex StreamDetail = new(@"\b(\d+\s*)?kbits?\b|\baac\b|\bmp3\b|\bhls\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// D86 and D90, over the checked-in file: one spelling per place in the City filter. Within a country no two distinct
    /// city values fold alike (a case or accent variant); each spelling-variant set of <see cref="PlaceSpellingVariants"/>
    /// shows at most one spelling among the city values, compared by <c>Fold</c>, so a case variant of the kept spelling
    /// also counts as a second one; and no city value carries a stream detail (<see cref="StreamDetail"/>).
    /// </summary>
    private static void Cities(IReadOnlyList<StationCatalogEntry> entries)
    {
        var cities = entries.Where(e => e.City.Length > 0).Select(e => (e.Country, e.City)).Distinct().ToList();

        var folded = cities.GroupBy(c => (c.Country, Fold: StationCatalogQuery.Fold(c.City))).Where(g => g.Count() > 1).ToList();
        foreach (var g in folded.Take(10)) Console.WriteLine($"  {g.Key.Country} cities folding to {CatalogFixtures.Show(g.Key.Fold)}: {string.Join(" | ", g.Select(c => CatalogFixtures.Show(c.City)))}");
        Check($"CAT-01 D86 D90 within each country no two distinct city values have the same Fold ({cities.Count} distinct (country, city) values; actual {folded.Count} clashes)",
            cities.Count > 0 && folded.Count == 0);

        var values = cities.Select(c => c.City).Distinct(StringComparer.Ordinal).ToList();
        var mixed = new List<string>();
        var kept = new List<string>();
        foreach (var set in PlaceSpellingVariants)
        {
            var spellings = set.Select(StationCatalogQuery.Fold).ToHashSet(StringComparer.Ordinal);
            var found = values.Where(v => spellings.Contains(StationCatalogQuery.Fold(v))).ToList();
            if (found.Count > 1) mixed.Add(string.Join(" / ", found.Select(CatalogFixtures.Show)));
            else if (found.Count == 1) kept.Add(found[0]);
        }
        Console.WriteLine($"  spelling-variant sets: kept {string.Join(", ", kept)}; mixed {(mixed.Count == 0 ? "none" : string.Join("; ", mixed))}");
        Check($"CAT-01 D90 each of the {PlaceSpellingVariants.Length} spelling-variant sets appears under at most one spelling among the city values " +
              $"({kept.Count} appear, each once; actual mixed {mixed.Count})",
            mixed.Count == 0 && kept.Count > 0);

        var details = values.Where(v => StreamDetail.IsMatch(v)).ToList();
        foreach (var v in details.Take(10)) Console.WriteLine($"  city with a stream detail: {CatalogFixtures.Show(v)}");
        Check($"CAT-01 D90 no city value carries a stream detail (a bitrate, a codec or HLS: {StreamDetail}) (actual {details.Count})", details.Count == 0);
    }

    /// <summary>
    /// §2.1 <c>language</c> and §2.3 rule 7 (D84), by shape only: <c>""</c> or names joined by exactly <c>", "</c>, each
    /// name one <see cref="LanguageName"/>, none twice in an entry; across the file no two distinct names fold alike (a
    /// case or accent variant the table missed); and the list of distinct names stays small.
    /// </summary>
    private static void Languages(IReadOnlyList<StationCatalogEntry> entries)
    {
        var bad = new List<string>();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var lists = 0;
        foreach (var e in entries)
        {
            if (e.Language.Length == 0) continue;
            var parts = e.Language.Split(CatalogFixtures.LanguageSeparator);
            if (parts.Length > 1) lists++;
            if (!parts.All(LanguageName.IsMatch) || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length) bad.Add(e.Language);
            foreach (var part in parts) names[part] = names.GetValueOrDefault(part) + 1;
        }
        foreach (var value in bad.Distinct().Take(10)) Console.WriteLine($"  language {CatalogFixtures.Show(value)}");
        Check("CAT-01 language (D84): every value is \"\" or names joined by exactly \", \"; each name non-empty, trimmed, single-spaced, " +
              "free of ',' and ';', and no name twice in one entry", bad.Count == 0);

        var variants = names.Keys.GroupBy(StationCatalogQuery.Fold, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        foreach (var g in variants.Take(10)) Console.WriteLine($"  names folding to {CatalogFixtures.Show(g.Key)}: {string.Join(" | ", g.Select(CatalogFixtures.Show))}");
        Check("CAT-01 language (D84): no two distinct names in the file have the same Fold (no case or accent duplicate the table missed)", variants.Count == 0);

        Console.WriteLine($"  language: {names.Count} distinct names, {lists} entries with two or more, {entries.Count(e => e.Language.Length == 0)} without");
        Check($"CAT-01 language (D84): the distinct names are few (actual {names.Count}, at most {MaxLanguageNames}) and none contains \", \"",
            names.Count is > 0 and <= MaxLanguageNames && !names.Keys.Any(n => n.Contains(CatalogFixtures.LanguageSeparator, StringComparison.Ordinal)));
    }

    /// <summary>CAT-08 over the real entries (D84): the Language list holds single names in the §3.3 order, exactly the
    /// file's distinct names, and each one, as the filter, returns exactly the entries whose list names it.</summary>
    private static void LanguageFilter(IReadOnlyList<StationCatalogEntry> entries)
    {
        var values = StationCatalogQuery.AvailableValues(entries, CatalogField.Language);
        var names = entries.Where(e => e.Language.Length > 0).SelectMany(e => e.Language.Split(CatalogFixtures.LanguageSeparator)).ToHashSet(StringComparer.Ordinal);
        static int Compare(CatalogFilterValue a, CatalogFilterValue b)
        {
            var c = string.CompareOrdinal(StationCatalogQuery.Fold(a.Label), StationCatalogQuery.Fold(b.Label));
            if (c == 0) c = string.CompareOrdinal(a.Label, b.Label);
            return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
        }
        var unordered = Enumerable.Range(1, Math.Max(0, values.Count - 1)).Where(i => Compare(values[i - 1], values[i]) >= 0).ToList();
        foreach (var i in unordered.Take(5)) Console.WriteLine($"  not strictly ordered: {CatalogFixtures.Show(values[i - 1].Value)} before {CatalogFixtures.Show(values[i].Value)}");
        var joined = values.Where(v => v.Value.Length == 0 || v.Value.IndexOfAny([',', ';']) >= 0 || v.Label != v.Value).ToList();
        foreach (var v in joined.Take(5)) Console.WriteLine($"  value {CatalogFixtures.Show(v.Value)} label {CatalogFixtures.Show(v.Label)}");
        Check($"CAT-08 the real catalog's Language list (D84): {values.Count} single names, none empty or containing ',' or ';', Label = Value, " +
              "strictly ordered by Fold(Label), Label, Value, and exactly the file's distinct names",
            values.Count > 0 && joined.Count == 0 && unordered.Count == 0 && names.SetEquals(values.Select(v => v.Value)) && names.Count == values.Count);

        var index = new StationCatalogIndex(entries);
        var wrong = new List<string>();
        foreach (var v in values)
        {
            var expected = entries.Where(e => e.Language.Split(CatalogFixtures.LanguageSeparator).Contains(v.Value, StringComparer.Ordinal)).ToHashSet();
            var result = StationCatalogQuery.Search(index, null, new CatalogFilters(Language: v.Value), entries.Count);
            if (expected.Count == 0 || result.TotalCount != expected.Count || !expected.SetEquals(result.Items))
                wrong.Add($"{CatalogFixtures.Show(v.Value)}: expected {expected.Count}, actual {result.TotalCount}");
        }
        foreach (var w in wrong.Take(10)) Console.WriteLine("  " + w);
        Check("CAT-08 over the real catalog (D84): each Language value, as the filter, returns at least one entry and exactly the entries whose language lists that name",
            wrong.Count == 0);
    }

    /// <summary>§2.3 rule 5: the exported (country, name, stream_url) set equals the Working canonical rows that pass the URL rule.</summary>
    private static void Canonical(string? root, IReadOnlyList<StationCatalogEntry> entries)
    {
        const string name = "CAT-01 the (country, name, stream_url) set equals the Working rows of data/canonical/*.csv whose stream URL passes the URL rule";
        var folder = root is null ? null : Path.Combine(root, "data", "canonical");
        var files = folder is not null && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.csv").Order(StringComparer.Ordinal).ToArray() : [];
        if (files.Length == 0)
        {
            Skip(name, "no repository root with data/canonical/*.csv above the test binary");
            return;
        }
        var expected = new HashSet<(string, string, string)>();
        var rawLanguages = new Dictionary<(string, string, string), List<string>>();
        var rows = 0;
        foreach (var file in files)
        {
            var table = ReadCsv(file);
            var header = table[0];
            var ragged = table.Skip(1).Count(r => r.Count != header.Count);
            Check($"CAT-01 {Path.GetFileName(file)} parses as RFC 4180 with every row as wide as its header ({header.Count} columns)",
                ragged == 0 && new[] { "country", "name", "stream_url", "stream_status", "language" }.All(header.Contains));
            int Col(string column) => header.IndexOf(column);
            int country = Col("country"), station = Col("name"), url = Col("stream_url"), status = Col("stream_status"), language = Col("language");
            foreach (var row in table.Skip(1))
            {
                rows++;
                var stream = row[url].Trim();
                if (row[status].Trim() != "Working" || !UrlRule(stream)) continue;
                var key = (row[country].Trim(), row[station].Trim(), stream);
                expected.Add(key);
                if (!rawLanguages.TryGetValue(key, out var raw)) rawLanguages[key] = raw = [];
                raw.Add(row[language].Trim());
            }
        }
        var actual = entries.Select(e => (e.Country, e.Name, e.StreamUrl)).ToHashSet();
        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        foreach (var m in missing.Take(5)) Console.WriteLine($"  Working row missing from the export: {m}");
        foreach (var x in extra.Take(5)) Console.WriteLine($"  exported but not a Working canonical row: {x}");
        Console.WriteLine($"  canonical: {files.Length} files, {rows} rows, {expected.Count} Working (country, name, stream_url) passing the URL rule");
        Check(name, missing.Count == 0 && extra.Count == 0 && expected.Count == entries.Count);

        // D84: the export maps and drops language tokens, but never invents one for a row whose raw language is empty.
        var invented = entries.Where(e => e.Language.Length > 0 && rawLanguages.TryGetValue((e.Country, e.Name, e.StreamUrl), out var raw)
                                          && raw.All(r => r.Length == 0)).ToList();
        foreach (var e in invented.Take(5)) Console.WriteLine($"  {e.Name} ({e.Country}): language {CatalogFixtures.Show(e.Language)} from an empty raw language");
        Check("CAT-01 language (D84): an entry's language is \"\" whenever its canonical row's raw language is empty (the export invents nothing)",
            invented.Count == 0);
    }

    /// <summary>A small RFC 4180 reader: comma-separated, double-quoted fields with "" escapes and embedded line breaks,
    /// CRLF or LF records. <see cref="File.ReadAllText(string)"/> drops the BOM.</summary>
    private static List<List<string>> ReadCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }
}
