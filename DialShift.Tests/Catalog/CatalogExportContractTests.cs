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
/// provider and compared with it and with <c>data/canonical/*.csv</c>.
/// </summary>
/// <remarks>
/// These are the only catalog checks that read the real file. They assert its contract, never its contents: the station
/// count is whatever the JSON says. The repository checks (byte equality with <c>data/output/</c>, the canonical CSVs)
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
        var rows = 0;
        foreach (var file in files)
        {
            var table = ReadCsv(file);
            var header = table[0];
            var ragged = table.Skip(1).Count(r => r.Count != header.Count);
            Check($"CAT-01 {Path.GetFileName(file)} parses as RFC 4180 with every row as wide as its header ({header.Count} columns)",
                ragged == 0 && new[] { "country", "name", "stream_url", "stream_status" }.All(header.Contains));
            int Col(string column) => header.IndexOf(column);
            int country = Col("country"), station = Col("name"), url = Col("stream_url"), status = Col("stream_status");
            foreach (var row in table.Skip(1))
            {
                rows++;
                var stream = row[url].Trim();
                if (row[status].Trim() == "Working" && UrlRule(stream)) expected.Add((row[country].Trim(), row[station].Trim(), stream));
            }
        }
        var actual = entries.Select(e => (e.Country, e.Name, e.StreamUrl)).ToHashSet();
        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        foreach (var m in missing.Take(5)) Console.WriteLine($"  Working row missing from the export: {m}");
        foreach (var x in extra.Take(5)) Console.WriteLine($"  exported but not a Working canonical row: {x}");
        Console.WriteLine($"  canonical: {files.Length} files, {rows} rows, {expected.Count} Working (country, name, stream_url) passing the URL rule");
        Check(name, missing.Count == 0 && extra.Count == 0 && expected.Count == entries.Count);
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
