using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DialShift.App.Services;
using DialShift.Core.Catalog;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// <see cref="CatalogProvider"/> against fixture files (docs/catalog-contracts.md §2.1, §4.2; D60, D74): the fixture half
/// of CAT-01 (every §2.1 field as the app maps it, unknown keys, a BOM), CAT-04 (every degraded input ends Unavailable
/// with the provider's exact reason and one <c>catalog.unavailable</c> line, never an exception; a FIFO or device refused
/// before opening (D81); load once; a cancelled caller; a failing log that never changes the result), and CAT-05 (<see cref="CatalogProvider.ResolveLocation"/>). Fixtures are written to one
/// <see cref="TempDirectory"/>; nothing reads the process environment or the host's culture.
/// </summary>
internal static class CatalogProviderTests
{
    /// <summary>Bounds every wait on a load; a load of these fixtures takes milliseconds, this only catches a hang.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private const string NotJson = "the file is not valid catalog JSON.";
    private const string NoUsable = "the file has no usable station entry.";
    private const string NoStations = "the file has no stations array.";
    private const string SchemaMissing = "schema_version is missing (expected 1).";
    private const string MustBeAbsolute = "DIALSHIFT_CATALOG_PATH must be an absolute path.";
    private const string NotValidPath = "DIALSHIFT_CATALOG_PATH is not a valid path.";
    private const string NotRegular = "the path is not a regular file.";
    private const string DefaultHeader = "\"schema_version\":1,\"generated_utc\":\"2026-09-25T12:00:00Z\"";

    public static async Task RunAsync()
    {
        using var dir = new TempDirectory("catalog-provider");
        await FieldMappingAsync(dir);
        await GeneratedUtcAsync(dir);
        await EntriesSkippedAsync(dir);
        await NoncharactersAsync(dir);
        await DegradedContentAsync(dir);
        await DegradedPathsAsync(dir);
        await NonRegularFilesAsync(dir);
        await EntryLimitAsync(dir);
        await FileSizeLimitAsync(dir);
        await UnreadableAsync(dir);
        await LoadOnceAndCancellationAsync(dir);
        await FailingLogAsync(dir);
        ResolveLocation(dir);
        await OverrideLoadsAsync(dir);
    }

    // ─── Fixture helpers ───

    /// <summary>A JSON string literal (quotes and escapes included).</summary>
    private static string J(string value) => JsonSerializer.Serialize(value);

    /// <summary>One compact station object with the three required keys, plus <paramref name="extra"/> (",\"key\":value…").</summary>
    private static string Station(string name, string country = "DE", string? url = null, string extra = "") =>
        $"{{\"name\":{J(name)},\"country\":{J(country)},\"stream_url\":{J(url ?? "https://" + Slug(name) + ".example.org/live")}{extra}}}";

    private static string Slug(string name) => new(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>A document in the pipeline's layout: the header line, one station per line, then <c>]}</c>.</summary>
    private static string Document(IEnumerable<string> stations, string header = DefaultHeader) =>
        "{" + header + ",\"stations\":[\n" + string.Join(",\n", stations) + "\n]}\n";

    private static int fixtures;

    /// <summary>Writes <paramref name="content"/> as UTF-8 (optionally with a BOM) to a new file in <paramref name="dir"/>.</summary>
    private static string Write(TempDirectory dir, string content, bool bom = false)
    {
        var path = dir.Combine($"fixture-{Interlocked.Increment(ref fixtures)}.json");
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes);
        return path;
    }

    private static CatalogLocation At(string path) => new(path, CatalogLocationSource.Override, null);

    private static async Task<(CatalogLoadResult Result, RecordingAppLog Log, CatalogProvider Provider)> LoadAsync(CatalogLocation location)
    {
        var log = new RecordingAppLog();
        var provider = new CatalogProvider(location, log);
        var result = await provider.GetCatalogAsync().WaitAsync(Bound);
        return (result, log, provider);
    }

    private static string Describe(RecordingAppLog log) =>
        string.Join(" | ", log.Entries.Select(e => $"{e.Level} {e.EventName}: {e.Message}{(e.Exception is null ? "" : " [" + e.Exception.GetType().Name + "]")}"));

    private static string Describe(CatalogLoadResult result) =>
        $"{result.State} \"{result.Message}\" ({result.Catalog.Entries.Count} entries, generated {result.GeneratedUtc?.ToString("O") ?? "null"})";

    /// <summary>
    /// The load at <paramref name="location"/> is Unavailable with exactly <paramref name="reason"/> and the empty index;
    /// the log holds exactly one <c>catalog.unavailable</c> warning worded as the provider words it (carrying an exception
    /// of <paramref name="exception"/> when given, none otherwise), no <c>catalog.loaded</c>, and <paramref name="skipped"/>
    /// &gt; 0 exactly when a <c>catalog.entries_skipped</c> line is expected. A second call returns the same result
    /// object without logging again. The load never faults: a fault would propagate out of the await and fail the suite.
    /// </summary>
    private static async Task CheckUnavailableAsync(string name, CatalogLocation location, string reason, Type? exception = null, bool skipped = false)
    {
        var (result, log, provider) = await LoadAsync(location);
        CheckUnavailable(name, location, reason, result, await provider.GetCatalogAsync().WaitAsync(Bound), log, exception, skipped);
    }

    /// <summary><see cref="CheckUnavailableAsync"/> on a load already awaited: <paramref name="result"/> is the first call's
    /// result and <paramref name="again"/> a second call's.</summary>
    private static void CheckUnavailable(string name, CatalogLocation location, string reason, CatalogLoadResult result, CatalogLoadResult again,
        RecordingAppLog log, Type? exception = null, bool skipped = false)
    {
        var warnings = log.Entries.Where(e => e.EventName == "catalog.unavailable").ToList();
        var line = $"Station catalog unavailable: {reason.TrimEnd('.')} ({location.Path ?? CatalogProvider.PathOverrideVariable}).";
        var ok = result.State == CatalogLoadState.Unavailable && result.Message == reason
                 && ReferenceEquals(result.Catalog, StationCatalogIndex.Empty) && result.GeneratedUtc is null && ReferenceEquals(result, again)
                 && warnings.Count == 1 && warnings[0].Level == AppLogLevel.Warn && warnings[0].Message == line
                 && (exception is null ? warnings[0].Exception is null : exception.IsInstanceOfType(warnings[0].Exception))
                 && !log.HasEvent("catalog.loaded") && log.Entries.Count(e => e.EventName == "catalog.entries_skipped") == (skipped ? 1 : 0);
        if (!ok)
        {
            Console.WriteLine($"  expected: Unavailable \"{reason}\"; one Warn catalog.unavailable \"{line}\"{(exception is null ? "" : " with " + exception.Name)}");
            Console.WriteLine($"  actual:   {Describe(result)}; log: {Describe(log)}; second call same result: {ReferenceEquals(result, again)}");
        }
        Check(name, ok);
    }

    // ─── CAT-01 (fixture half): every §2.1 field as the app maps it ───

    /// <summary>Eight stations covering every §2.1 key and every "App tolerates" rule, with unknown keys at both levels.</summary>
    private const string MappingFixture =
        "{\"publisher\":{\"tool\":\"future\",\"list\":[1,2,{\"x\":null}]},\"schema_version\":1,\"generated_utc\":\"2026-09-25T12:00:00Z\",\"stations\":[\n" +
        // Every key, padded with white space, raw UTF-8, and unknown keys before, between and after the known ones.
        "{\"future_object\":{\"nested\":[1,{\"x\":null}],\"flag\":true},\"name\":\"  Alpha FM  \",\"name_local\":\" Άλφα \",\"country\":\" DE \"," +
        "\"country_label\":\" Germany \",\"city\":\" Ismaning (Munich) \",\"region\":\" Bavaria \",\"frequency_fm\":\" 101.5 \",\"type\":\" Music \"," +
        "\"genre\":\" Pop / Schlager \",\"language\":\" German \",\"internet_only\":false,\"future_between\":\"x\"," +
        "\"stream_url\":\"  https://alpha.example.org/live.mp3  \",\"codec\":\" MP3 \",\"bitrate\":128,\"votes\":4210,\"notes\":\" Chart hits, 24/7 \"," +
        "\"logo\":\" https://logo.example.org/alpha.png \",\"tag\":\" Music · Pop / Schlager \",\"future_array\":[1,2,3],\"future_number\":1.5,\"future_null\":null},\n" +
        // Every optional key null.
        "{\"name\":\"Beta\",\"name_local\":null,\"country\":\"GR\",\"country_label\":null,\"city\":null,\"region\":null,\"frequency_fm\":null,\"type\":null," +
        "\"genre\":null,\"language\":null,\"internet_only\":null,\"stream_url\":\"http://beta.example.org:8000/stream\",\"codec\":null,\"bitrate\":null," +
        "\"votes\":null,\"notes\":null,\"logo\":null,\"tag\":null},\n" +
        // Only the three required keys.
        "{\"name\":\"Gamma\",\"country\":\"FR\",\"stream_url\":\"https://gamma.example.org/live\"},\n" +
        // A collection entry.
        "{\"name\":\"Delta Chill\",\"name_local\":\"\",\"country\":\"Internet\",\"country_label\":\"Internet (collections)\",\"city\":\"\",\"region\":\"\"," +
        "\"frequency_fm\":\"\",\"type\":\"Music\",\"genre\":\"Delta · Chillout\",\"language\":\"Instrumental\",\"internet_only\":true," +
        "\"stream_url\":\"https://delta.example.org/chill\",\"codec\":\"AAC\",\"bitrate\":64,\"votes\":0,\"notes\":\"\",\"logo\":\"\",\"tag\":\"Music · Delta · Chillout\"},\n" +
        // Bounds: bitrate 0, votes 0, a non-http logo.
        "{\"name\":\"Echo\",\"country\":\"DE\",\"stream_url\":\"https://echo.example.org/live\",\"bitrate\":0,\"votes\":0,\"logo\":\"ftp://logo.example.org/echo.png\"},\n" +
        // Bounds: negative bitrate and votes, a relative logo.
        "{\"name\":\"Foxtrot\",\"country\":\"DE\",\"stream_url\":\"https://foxtrot.example.org/live\",\"bitrate\":-5,\"votes\":-1,\"logo\":\"/logos/foxtrot.png\"},\n" +
        // An escaped name, a javascript: logo, and an upper-case scheme kept as written.
        "{\"name\":\"Golf \\u00e9\\u00df \\\"quoted\\\"\",\"country\":\"FR\",\"stream_url\":\"HTTPS://Golf.Example.org/Live\",\"logo\":\"javascript:alert(1)\",\"votes\":2147483647},\n" +
        // A logo without a host.
        "{\"name\":\"Hotel\",\"country\":\"GR\",\"stream_url\":\"https://hotel.example.org/live\",\"logo\":\"http://\",\"tag\":\"\"}\n" +
        "],\"trailer\":{\"note\":\"ignored\"}}\n";

    private static readonly StationCatalogEntry[] MappingExpected =
    [
        new()
        {
            Name = "Alpha FM", NameLocal = "Άλφα", Country = "DE", CountryLabel = "Germany", City = "Ismaning (Munich)", Region = "Bavaria",
            FrequencyFm = "101.5", Type = "Music", Genre = "Pop / Schlager", Language = "German", InternetOnly = false,
            StreamUrl = "https://alpha.example.org/live.mp3", Codec = "MP3", Bitrate = 128, Votes = 4210, Notes = "Chart hits, 24/7",
            Logo = "https://logo.example.org/alpha.png", Tag = "Music · Pop / Schlager",
        },
        new() { Name = "Beta", Country = "GR", StreamUrl = "http://beta.example.org:8000/stream" },
        new() { Name = "Gamma", Country = "FR", StreamUrl = "https://gamma.example.org/live" },
        new()
        {
            Name = "Delta Chill", Country = "Internet", CountryLabel = "Internet (collections)", Type = "Music", Genre = "Delta · Chillout",
            Language = "Instrumental", InternetOnly = true, StreamUrl = "https://delta.example.org/chill", Codec = "AAC", Bitrate = 64, Votes = 0,
            Tag = "Music · Delta · Chillout",
        },
        new() { Name = "Echo", Country = "DE", StreamUrl = "https://echo.example.org/live", Bitrate = null, Votes = 0, Logo = "" },
        new() { Name = "Foxtrot", Country = "DE", StreamUrl = "https://foxtrot.example.org/live", Bitrate = null, Votes = null, Logo = "" },
        new() { Name = "Golf éß \"quoted\"", Country = "FR", StreamUrl = "HTTPS://Golf.Example.org/Live", Logo = "", Votes = int.MaxValue },
        new() { Name = "Hotel", Country = "GR", StreamUrl = "https://hotel.example.org/live", Logo = "", Tag = "" },
    ];

    private static bool SameEntries(IReadOnlyList<StationCatalogEntry> actual, IReadOnlyList<StationCatalogEntry> expected)
    {
        var same = actual.Count == expected.Count && actual.Zip(expected).All(p => p.First == p.Second);
        if (!same)
        {
            Console.WriteLine($"  expected {expected.Count} entries, actual {actual.Count}");
            foreach (var (a, e) in actual.Zip(expected).Where(p => p.First != p.Second))
                Console.WriteLine($"  expected: {e}\n  actual:   {a}");
        }
        return same;
    }

    private static async Task FieldMappingAsync(TempDirectory dir)
    {
        var path = Write(dir, MappingFixture, bom: true);
        var (result, log, _) = await LoadAsync(At(path));
        var loaded = log.Entries.Where(e => e.EventName == "catalog.loaded").ToList();
        var loadedLine = new Regex($@"^Loaded 8 stations \(generated 2026-09-25T12:00:00Z\) from {Regex.Escape(path)} \[DIALSHIFT_CATALOG_PATH\] in \d+ ms\.$");
        Check("CAT-01 a BOM-prefixed fixture with unknown keys at both levels (objects, arrays, numbers, null) loads: Loaded, Message null, 8 entries",
            result.State == CatalogLoadState.Loaded && result.Message is null && result.Catalog.Entries.Count == 8);
        Check("CAT-01 every §2.1 key maps per the \"App tolerates\" column: strings trimmed, null/missing → \"\" / false / null, bitrate ≤ 0 → null, " +
              "votes < 0 → null (0 kept), a logo that is not an http(s) URL with a host → \"\", tag kept, stream URL kept as written; file order kept",
            SameEntries(result.Catalog.Entries, MappingExpected));
        Check("CAT-01 a collection entry keeps country Internet, country_label \"Internet (collections)\" and internet_only true",
            result.Catalog.Entries.Single(e => e.Country == "Internet") is { CountryLabel: "Internet (collections)", InternetOnly: true });
        Check("CAT-01 GeneratedUtc is generated_utc as a UTC DateTimeOffset (2026-09-25T12:00:00Z, offset zero)",
            result.GeneratedUtc == new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero) && result.GeneratedUtc?.Offset == TimeSpan.Zero);
        if (loaded.Count != 1 || !loadedLine.IsMatch(loaded[0].Message)) Console.WriteLine("  log: " + Describe(log));
        Check("CAT-01 one Info catalog.loaded \"Loaded 8 stations (generated 2026-09-25T12:00:00Z) from <path> [DIALSHIFT_CATALOG_PATH] in N ms.\" and no warning",
            loaded.Count == 1 && loaded[0].Level == AppLogLevel.Info && loadedLine.IsMatch(loaded[0].Message)
            && log.Entries.Count == 1);

        // The same document without a BOM, and re-written indented with CRLF line ends, maps to the same entries.
        var plain = await LoadAsync(At(Write(dir, MappingFixture)));
        var indented = System.Text.Json.Nodes.JsonNode.Parse(MappingFixture)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\r\n");
        var pretty = await LoadAsync(At(Write(dir, indented, bom: true)));
        Check("CAT-01 the BOM is optional: without it, and indented with CRLF line ends, the same fixture maps to the same entries",
            SameEntries(plain.Result.Catalog.Entries, MappingExpected) && SameEntries(pretty.Result.Catalog.Entries, MappingExpected));
    }

    private static async Task GeneratedUtcAsync(TempDirectory dir)
    {
        var station = Station("Kilo");
        var exact = await LoadAsync(At(Write(dir, Document([station], "\"schema_version\":1,\"generated_utc\":\"2026-12-31T23:59:59Z\""))));
        Check("CAT-01 generated_utc 2026-12-31T23:59:59Z parses exactly (no local-zone shift)",
            exact.Result.GeneratedUtc == new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero));

        string[] headers =
        [
            "\"schema_version\":1",
            "\"schema_version\":1,\"generated_utc\":null",
            "\"schema_version\":1,\"generated_utc\":\"\"",
            "\"schema_version\":1,\"generated_utc\":\"2026-09-25T12:00:00+02:00\"",
            "\"schema_version\":1,\"generated_utc\":\"2026-09-25 12:00:00Z\"",
            "\"schema_version\":1,\"generated_utc\":\"2026-09-25T12:00:00.5Z\"",
            "\"schema_version\":1,\"generated_utc\":\"2026-09-25T12:00Z\"",
            "\"schema_version\":1,\"generated_utc\":\"2026-13-01T00:00:00Z\"",
            "\"schema_version\":1,\"generated_utc\":\"yesterday\"",
        ];
        var ok = true;
        foreach (var header in headers)
        {
            var (result, log, _) = await LoadAsync(At(Write(dir, Document([station], header))));
            var line = log.Entries.SingleOrDefault(e => e.EventName == "catalog.loaded")?.Message ?? "";
            if (result.State == CatalogLoadState.Loaded && result.GeneratedUtc is null && line.Contains("(generated unknown)", StringComparison.Ordinal)
                && log.Entries.Count == 1) continue;
            ok = false;
            Console.WriteLine($"  header {{{header}}}: {Describe(result)}; log: {Describe(log)}");
        }
        Check("CAT-01 generated_utc missing, null, \"\", with an offset, a space, fractions, no seconds, month 13 or not a date: still Loaded, " +
              "GeneratedUtc null, catalog.loaded says \"(generated unknown)\"", ok);
    }

    // ─── CAT-04: entries that violate the app's rules are skipped ───

    private static async Task EntriesSkippedAsync(TempDirectory dir)
    {
        var url2048 = "https://two.example.org/" + new string('a', 2048 - "https://two.example.org/".Length);
        var url2049 = "https://long.example.org/" + new string('a', 2049 - "https://long.example.org/".Length);
        string[] stations =
        [
            Station("Keep One"),
            Station(""),
            Station("   "),
            "{\"country\":\"DE\",\"stream_url\":\"https://noname.example.org/live\"}",
            Station("Empty Country", country: ""),
            "{\"name\":\"No Country\",\"stream_url\":\"https://nocountry.example.org/live\"}",
            Station("Ftp", url: "ftp://ftp.example.org/live"),
            Station("Relative", url: "stream.example.org/live"),
            Station("Empty Url", url: ""),
            "{\"name\":\"No Url\",\"country\":\"DE\"}",
            Station("No Host", url: "http://"),
            Station("File", url: "file:///etc/passwd"),
            Station("Too Long", url: url2049),
            "null",
            Station("Keep Two", url: url2048),
            Station("Keep Three", url: "  https://three.example.org/live \t"),
            Station("Script", url: "javascript:alert(1)"),
        ];
        var (result, log, _) = await LoadAsync(At(Write(dir, Document(stations))));
        var entries = result.Catalog.Entries;
        Check("CAT-04 entries without a name or country, with a stream URL that is not absolute http(s) with a host or is over 2,048 characters, " +
              "and null elements are skipped; the rest load (Loaded: Keep One, Keep Two, Keep Three, file order)",
            result.State == CatalogLoadState.Loaded && entries.Select(e => e.Name).SequenceEqual(["Keep One", "Keep Two", "Keep Three"]));
        Check("CAT-04 a 2,048-character stream URL is kept and a padded one is trimmed",
            entries.Count == 3 && entries[1].StreamUrl == url2048 && entries[1].StreamUrl.Length == 2048
            && entries[2].StreamUrl == "https://three.example.org/live");
        var skipped = log.Entries.Where(e => e.EventName == "catalog.entries_skipped").ToList();
        if (skipped.Count != 1) Console.WriteLine("  log: " + Describe(log));
        Check("CAT-04 one Warn catalog.entries_skipped \"Skipped 14 of 17 catalog entries without a name, country or valid stream URL.\", " +
              "one catalog.loaded (3 stations), no catalog.unavailable",
            skipped.Count == 1 && skipped[0].Level == AppLogLevel.Warn
            && skipped[0].Message == "Skipped 14 of 17 catalog entries without a name, country or valid stream URL."
            && log.Entries.Count(e => e.EventName == "catalog.loaded" && e.Message.StartsWith("Loaded 3 stations ", StringComparison.Ordinal)) == 1
            && !log.HasEvent("catalog.unavailable"));
    }

    // ─── CAT-04: U+FFFE and lone surrogates in the JSON (D77) ───

    private static async Task NoncharactersAsync(TempDirectory dir)
    {
        // Raw: the C# string holds U+FFFE itself, written as UTF-8 EF BF BE. Escaped: the file holds the six characters \ufffe.
        var raw = Station("Radio\uFFFE Raw", url: "https://raw.example.org/live");
        var escaped = "{\"name\":\"Radio\\ufffe Escaped\",\"country\":\"DE\",\"stream_url\":\"https://escaped.example.org/live\"}";
        var content = Document([raw, escaped]).Replace("\\uFFFE", "\uFFFE", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(content);
        var (result, log, _) = await LoadAsync(At(Write(dir, content)));
        var names = result.Catalog.Entries.Select(e => e.Name).ToArray();
        var hits = StationCatalogQuery.Search(result.Catalog, "radio", CatalogFilters.None, StationCatalogQuery.DefaultCap);
        if (result.State != CatalogLoadState.Loaded) Console.WriteLine($"  actual: {Describe(result)}; log: {Describe(log)}");
        Check("CAT-04 D77 a station name with U+FFFE, raw (UTF-8 EF BF BE) or escaped (\\ufffe), loads: Loaded, both names keep U+FFFE, radio finds both",
            bytes.AsSpan().IndexOf([(byte)0xEF, (byte)0xBF, (byte)0xBE]) >= 0 && content.Contains("\\ufffe", StringComparison.Ordinal)
            && result.State == CatalogLoadState.Loaded && names.SequenceEqual(["Radio\uFFFE Raw", "Radio\uFFFE Escaped"]) && hits.TotalCount == 2
            && !log.Entries.Any(e => e.Level != AppLogLevel.Info));
        await CheckUnavailableAsync("CAT-04 D77 an escaped lone surrogate (\\ud800) in a name is a JSON error: Unavailable \"the file is not valid catalog JSON.\"",
            At(Write(dir, Document(["{\"name\":\"Radio\\ud800\",\"country\":\"DE\",\"stream_url\":\"https://lone.example.org/live\"}"]))),
            NotJson, typeof(JsonException));
    }

    // ─── CAT-04: degraded files end Unavailable ───

    private static async Task DegradedContentAsync(TempDirectory dir)
    {
        var one = Station("Lima");
        var full = Document([Station("Lima"), Station("Mike"), Station("November")]);
        (string Name, string Content, string Reason, bool Json, bool Skipped)[] cases =
        [
            ("an empty file (0 bytes)", "", NotJson, true, false),
            ("a file of white space only", " \n\t\r\n", NotJson, true, false),
            ("a BOM and nothing else", "\uFEFF", NotJson, true, false),
            ("plain text, not JSON", "this is not a catalog", NotJson, true, false),
            ("a document truncated mid-station", full[..(full.Length / 2)], NotJson, true, false),
            ("a document truncated after \"stations\":[", "{" + DefaultHeader + ",\"stations\":[\n", NotJson, true, false),
            ("a document without its closing ]}", full[..full.LastIndexOf(']')], NotJson, true, false),
            ("a JSON null root", "null", "the file holds no JSON object.", false, false),
            ("a root array", "[" + one + "]", NotJson, true, false),
            ("a root number", "42", NotJson, true, false),
            ("a comment (not allowed)", "// generated\n" + Document([one]), NotJson, true, false),
            ("a trailing comma (not allowed)", "{" + DefaultHeader + ",\"stations\":[\n" + one + ",\n]}\n", NotJson, true, false),
            ("schema_version 2", Document([one], "\"schema_version\":2"), "schema_version 2 is not supported (expected 1).", false, false),
            ("schema_version 0", Document([one], "\"schema_version\":0"), "schema_version 0 is not supported (expected 1).", false, false),
            ("schema_version \"1\" (a string)", Document([one], "\"schema_version\":\"1\""), NotJson, true, false),
            ("schema_version 1.0 (not an integer)", Document([one], "\"schema_version\":1.0"), NotJson, true, false),
            ("schema_version true", Document([one], "\"schema_version\":true"), NotJson, true, false),
            ("schema_version missing", Document([one], "\"generated_utc\":\"2026-09-25T12:00:00Z\""), SchemaMissing, false, false),
            ("schema_version null", Document([one], "\"schema_version\":null"), SchemaMissing, false, false),
            ("stations missing", "{" + DefaultHeader + "}", NoStations, false, false),
            ("stations null", "{" + DefaultHeader + ",\"stations\":null}", NoStations, false, false),
            ("stations an object", "{" + DefaultHeader + ",\"stations\":{\"name\":\"Lima\"}}", NotJson, true, false),
            ("stations a string", "{" + DefaultHeader + ",\"stations\":\"Lima\"}", NotJson, true, false),
            ("stations an empty array", Document([]).Replace("[\n\n]", "[]", StringComparison.Ordinal), NoUsable, false, false),
            ("a station element that is a number", Document([one, "1"]), NotJson, true, false),
            ("a station element that is an array", Document([one, "[" + one + "]"]), NotJson, true, false),
            ("a station element that is a string", Document([one, "\"Lima\""]), NotJson, true, false),
            ("only null station elements", Document(["null", "null"]), NoUsable, false, true),
            ("every entry invalid", Document([Station(""), Station("Oscar", url: "ftp://oscar.example.org/")]), NoUsable, false, true),
            ("a type mismatch: votes \"12\"", Document([Station("Papa", extra: ",\"votes\":\"12\"")]), NotJson, true, false),
            ("a type mismatch: internet_only \"true\"", Document([Station("Papa", extra: ",\"internet_only\":\"true\"")]), NotJson, true, false),
            ("a type mismatch: bitrate 1.5", Document([Station("Papa", extra: ",\"bitrate\":1.5")]), NotJson, true, false),
            ("a type mismatch: name 5", Document(["{\"name\":5,\"country\":\"DE\",\"stream_url\":\"https://papa.example.org/\"}"]), NotJson, true, false),
            ("a type mismatch: generated_utc a number", Document([one], "\"schema_version\":1,\"generated_utc\":20260925"), NotJson, true, false),
        ];
        foreach (var (name, content, reason, json, skipped) in cases)
            await CheckUnavailableAsync($"CAT-04 {name} → Unavailable \"{reason}\", one catalog.unavailable, no exception",
                At(Write(dir, content)), reason, json ? typeof(JsonException) : null, skipped);

        var (_, allNull, _) = await LoadAsync(At(Write(dir, Document(["null", "null"]))));
        Check("CAT-04 only null elements: catalog.entries_skipped says \"Skipped 2 of 2 catalog entries without a name, country or valid stream URL.\"",
            allNull.Entries.Any(e => e.EventName == "catalog.entries_skipped"
                                     && e.Message == "Skipped 2 of 2 catalog entries without a name, country or valid stream URL."));
    }

    private static async Task DegradedPathsAsync(TempDirectory dir)
    {
        await CheckUnavailableAsync("CAT-04 a missing file → Unavailable \"the file does not exist.\", one catalog.unavailable, no exception",
            At(dir.Combine("does-not-exist.json")), "the file does not exist.");
        var folder = dir.Combine("a-directory.json");
        Directory.CreateDirectory(folder);
        await CheckUnavailableAsync("CAT-04 a directory → Unavailable \"the path is a directory, not a file.\", one catalog.unavailable, no exception",
            At(folder), "the path is a directory, not a file.");
        await CheckUnavailableAsync("CAT-04 a location with a Problem (relative override) → Unavailable with that Problem, logged against DIALSHIFT_CATALOG_PATH",
            new CatalogLocation(null, CatalogLocationSource.Override, MustBeAbsolute), MustBeAbsolute);
        await CheckUnavailableAsync("CAT-04 a location with neither Path nor Problem (breaks §4.2) → Unavailable \"the file does not exist.\", no exception",
            new CatalogLocation(null, CatalogLocationSource.Override, null), "the file does not exist.");
    }

    // ─── CAT-04: non-regular files are refused before opening (D81 item 4) ───

    /// <summary>A FIFO load finishes in milliseconds; the provider opening the FIFO would block until a writer appears.</summary>
    private static readonly TimeSpan FifoBound = TimeSpan.FromSeconds(2);

    private static async Task NonRegularFilesAsync(TempDirectory dir)
    {
        const string fifoName = "CAT-04 D81 a FIFO with no writer → Unavailable \"the path is not a regular file.\" within 2 s (refused before opening), " +
                                "one catalog.unavailable, no exception";
        string[] devices = ["/dev/null", "/dev/zero"];
        string DeviceName(string device) =>
            $"CAT-04 D81 a character device ({device}) → Unavailable \"the path is not a regular file.\", one catalog.unavailable, no exception";
        // The check before opening reads stat's st_mode on Apple Silicon macOS, the one Unix DialShift ships on (D2). Windows
        // has neither FIFOs nor /dev; elsewhere the provider has no check before opening, so a FIFO would hang the load.
        var reason = OperatingSystem.IsWindows() ? "Unix FIFO and device paths (macOS only)"
            : !OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64
                ? "the check before opening is Apple Silicon macOS only (D2, D81)"
                : null;
        if (reason != null)
        {
            Skip(fifoName, reason);
            foreach (var device in devices) Skip(DeviceName(device), reason);
            return;
        }

        foreach (var device in devices) await CheckUnavailableAsync(DeviceName(device), At(device), NotRegular);

        var fifo = dir.Combine("no-writer.fifo");
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo }, UseShellExecute = false })!)
        {
            await mkfifo.WaitForExitAsync().WaitAsync(Bound);
            Check("CAT-04 D81 precondition: mkfifo creates a FIFO the file APIs report as an existing file",
                mkfifo.ExitCode == 0 && File.Exists(fifo) && !Directory.Exists(fifo));
        }
        var log = new RecordingAppLog();
        var provider = new CatalogProvider(At(fifo), log);
        var clock = Stopwatch.StartNew();
        var load = provider.GetCatalogAsync();
        var finished = await Task.WhenAny(load, Task.Delay(FifoBound)) == load;
        clock.Stop();
        if (!finished)
        {
            Console.WriteLine($"  expected: Unavailable \"{NotRegular}\" within {FifoBound.TotalSeconds} s");
            Console.WriteLine($"  actual:   no result after {clock.ElapsedMilliseconds} ms (the provider opened the FIFO); log: {Describe(log)}");
            await OpenWriterAsync(fifo);
            Check(fifoName, false);
        }
        CheckUnavailable(fifoName, At(fifo), NotRegular, await load, await provider.GetCatalogAsync().WaitAsync(Bound), log);
    }

    /// <summary>Opens and closes a writer on <paramref name="fifo"/>, so a reader blocked in its open returns (end of file)
    /// and no thread-pool thread is left blocked after a failed check.</summary>
    private static async Task OpenWriterAsync(string fifo)
    {
        try
        {
            await Task.Run(() => new FileStream(fifo, FileMode.Open, FileAccess.Write).Dispose()).WaitAsync(Bound);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  could not release the blocked reader: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task EntryLimitAsync(TempDirectory dir)
    {
        static IEnumerable<string> Many(int count) => Enumerable.Range(0, count).Select(i => Station($"S{i}", url: $"https://s{i}.example.org/live"));
        var (atLimit, log, _) = await LoadAsync(At(Write(dir, Document(Many(CatalogProvider.MaxEntries)))));
        Check("CAT-04 exactly 10,000 entries load (Loaded, 10,000 entries, no warning)",
            atLimit.State == CatalogLoadState.Loaded && atLimit.Catalog.Entries.Count == CatalogProvider.MaxEntries
            && !log.Entries.Any(e => e.Level != AppLogLevel.Info));
        await CheckUnavailableAsync("CAT-04 10,001 entries → Unavailable \"the file lists 10001 stations, more than the 10000 supported.\", one catalog.unavailable",
            At(Write(dir, Document(Many(CatalogProvider.MaxEntries + 1)))), "the file lists 10001 stations, more than the 10000 supported.");
    }

    private static async Task FileSizeLimitAsync(TempDirectory dir)
    {
        // Sparse files of zeros: the size check comes before any parsing.
        var over = dir.Combine("oversize.json");
        using (var file = File.Create(over)) file.SetLength(CatalogProvider.MaxFileBytes + 1);
        await CheckUnavailableAsync("CAT-04 a file of MaxFileBytes + 1 (32 MiB + 1) → Unavailable \"the file is 33554433 bytes, more than the 33554432 bytes allowed.\"",
            At(over), "the file is 33554433 bytes, more than the 33554432 bytes allowed.");
        File.Delete(over);
        var exact = dir.Combine("exact-limit.json");
        using (var file = File.Create(exact)) file.SetLength(CatalogProvider.MaxFileBytes);
        await CheckUnavailableAsync("CAT-04 a file of exactly MaxFileBytes is read (the limit is inclusive), then rejected as not JSON (it is zeros)",
            At(exact), NotJson, typeof(JsonException));
        File.Delete(exact);
    }

    private static async Task UnreadableAsync(TempDirectory dir)
    {
        const string name = "CAT-04 an unreadable file (mode 000) → Unavailable \"the file could not be read.\" with UnauthorizedAccessException, one catalog.unavailable";
        if (OperatingSystem.IsWindows())
        {
            Skip(name, "Unix file modes (macOS/Linux only)");
            return;
        }
        var path = Write(dir, Document([Station("Quebec")]));
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            bool readable;
            try
            {
                _ = File.ReadAllBytes(path);
                readable = true;
            }
            catch (UnauthorizedAccessException) { readable = false; }
            if (readable) Skip(name, "running as root: mode 000 does not stop reads");
            else await CheckUnavailableAsync(name, At(path), "the file could not be read.", typeof(UnauthorizedAccessException));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ─── CAT-04: load once; a cancelled caller ───

    /// <summary>Records like <see cref="RecordingAppLog"/>, but <c>catalog.loaded</c> blocks until <see cref="Release"/>, holding the one load open.</summary>
    private sealed class GatedAppLog : IAppLog
    {
        public RecordingAppLog Inner { get; } = new();
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public void Info(string eventName, string message)
        {
            Inner.Info(eventName, message);
            if (eventName != "catalog.loaded") return;
            Entered.Set();
            Release.Wait(Bound);
        }

        public void Warn(string eventName, string message, Exception? ex = null) => Inner.Warn(eventName, message, ex);

        public void Error(string eventName, string message, Exception? ex = null) => Inner.Error(eventName, message, ex);
    }

    private static async Task LoadOnceAndCancellationAsync(TempDirectory dir)
    {
        var path = Write(dir, Document([Station("Romeo")]));
        var log = new GatedAppLog();
        var provider = new CatalogProvider(At(path), log);
        try
        {
            Task<CatalogLoadResult>? first = null;
            // Called on its own thread with a bound, so a load that ran on the caller's thread fails here instead of hanging.
            var returned = await NoThrowAsync(() => Task.Run(() => { first = provider.GetCatalogAsync(); }).WaitAsync(Bound));
            var inLoad = log.Entered.Wait(Bound);
            Check("CAT-04 GetCatalogAsync returns before the load completes: the load runs on the thread pool, not on the caller's thread",
                returned && inLoad && first is { IsCompleted: false });

            using var cts = new CancellationTokenSource();
            var cancelled = provider.GetCatalogAsync(cts.Token);
            var second = provider.GetCatalogAsync();
            var third = provider.GetCatalogAsync(CancellationToken.None);
            var preCancelled = provider.GetCatalogAsync(new CancellationToken(canceled: true));
            cts.Cancel();
            var threw = await ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Check("CAT-04 a cancelled caller token ends only that caller's wait (OperationCanceledException); a pre-cancelled token ends at once; " +
                  "the other callers still wait",
                threw && cancelled.IsCanceled && preCancelled.IsCanceled && !first!.IsCompleted && !second.IsCompleted && !third.IsCompleted);

            log.Release.Set();
            var results = await Task.WhenAll(first!, second, third).WaitAsync(Bound);
            var later = await provider.GetCatalogAsync().WaitAsync(Bound);
            Check("CAT-04 load once: the load a cancelled caller left still completes, and every caller (before and after) gets the same Loaded result object",
                results[0].State == CatalogLoadState.Loaded && results[0].Catalog.Entries.Count == 1
                && results.All(r => ReferenceEquals(r, results[0])) && ReferenceEquals(later, results[0]));

            File.Delete(path);
            var afterDelete = await provider.GetCatalogAsync().WaitAsync(Bound);
            Check("CAT-04 load once: after the file is deleted the provider still returns the first result; one catalog.loaded line for every call, no warning",
                ReferenceEquals(afterDelete, results[0]) && log.Inner.Entries.Count == 1 && log.Inner.HasEvent("catalog.loaded"));
        }
        finally
        {
            log.Release.Set();
        }

        var many = new CatalogProvider(At(dir.Combine("missing-for-many.json")), new RecordingAppLog());
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => many.GetCatalogAsync()))).WaitAsync(Bound);
        Check("CAT-04 load once: 16 concurrent first callers of an Unavailable catalog share one result object",
            concurrent.All(r => ReferenceEquals(r, concurrent[0])) && concurrent[0].State == CatalogLoadState.Unavailable);
    }

    // ─── CAT-04: a failing log never changes the result (D80 item 4 as amended) ───

    /// <summary>Records every call like <see cref="RecordingAppLog"/>, then throws when the event is <c>throwOn</c> (every event when null).</summary>
    private sealed class ThrowingAppLog(string? throwOn) : IAppLog
    {
        public RecordingAppLog Inner { get; } = new();
        public int Thrown;

        public string[] Events => [.. Inner.Entries.Select(e => e.EventName)];

        public void Info(string eventName, string message)
        {
            Inner.Info(eventName, message);
            Fail(eventName);
        }

        public void Warn(string eventName, string message, Exception? ex = null)
        {
            Inner.Warn(eventName, message, ex);
            Fail(eventName);
        }

        public void Error(string eventName, string message, Exception? ex = null)
        {
            Inner.Error(eventName, message, ex);
            Fail(eventName);
        }

        private void Fail(string eventName)
        {
            if (throwOn != null && eventName != throwOn) return;
            Interlocked.Increment(ref Thrown);
            throw new InvalidOperationException("log failure");
        }
    }

    /// <summary>Loads <paramref name="location"/> with <paramref name="log"/> and calls again for the same result. An exception
    /// out of the provider is returned as Escaped, so it fails the check with a report instead of stopping the suite.</summary>
    private static async Task<(CatalogLoadResult? Result, bool Same, Exception? Escaped)> LoadWithAsync(CatalogLocation location, IAppLog log)
    {
        try
        {
            var provider = new CatalogProvider(location, log);
            var result = await provider.GetCatalogAsync().WaitAsync(Bound);
            return (result, ReferenceEquals(result, await provider.GetCatalogAsync().WaitAsync(Bound)), null);
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return (null, false, ex);
        }
    }

    private static void Report(string expected, CatalogLoadResult? result, ThrowingAppLog log, Exception? escaped) =>
        Console.WriteLine($"  expected: {expected}\n  actual:   {(result is null ? "no result" : Describe(result))}; " +
                          $"log calls [{string.Join(", ", log.Events)}], {log.Thrown} thrown" +
                          (escaped is null ? "" : $"; escaped {escaped.GetType().Name}: {escaped.Message}"));

    private static async Task FailingLogAsync(TempDirectory dir)
    {
        // A log that throws on catalog.loaded: the good file still loads, with exactly the entries a working log sees.
        var goodPath = Write(dir, Document([Station("Sierra"), Station("Tango", country: "GR")]));
        var (reference, _, _) = await LoadAsync(At(goodPath));
        var loadedLog = new ThrowingAppLog("catalog.loaded");
        var (good, goodSame, goodEscaped) = await LoadWithAsync(At(goodPath), loadedLog);
        var goodOk = good is { State: CatalogLoadState.Loaded, Message: null } && goodSame && goodEscaped is null
                     && reference.State == CatalogLoadState.Loaded && reference.Catalog.Entries.Count == 2 && good.GeneratedUtc == reference.GeneratedUtc
                     && SameEntries(good.Catalog.Entries, reference.Catalog.Entries)
                     && loadedLog.Events.SequenceEqual(["catalog.loaded"]) && loadedLog.Thrown == 1;
        if (!goodOk) Report("Loaded, Message null, the reference load's 2 entries; log calls [catalog.loaded], 1 thrown", good, loadedLog, goodEscaped);
        Check("CAT-04 a log that throws on catalog.loaded does not change the result: Loaded with the same entries and generated_utc as a " +
              "working log's load, one log call (the throw swallowed), the same result on a second call, no exception", goodOk);

        // A log that throws on catalog.entries_skipped: the usable entry still loads and catalog.loaded is still written.
        var skippedLog = new ThrowingAppLog("catalog.entries_skipped");
        var (partial, partialSame, partialEscaped) = await LoadWithAsync(At(Write(dir, Document([Station("Uniform"), Station("")]))), skippedLog);
        var partialOk = partial is { State: CatalogLoadState.Loaded, Message: null } && partialSame && partialEscaped is null
                        && partial.Catalog.Entries.Select(e => e.Name).SequenceEqual(["Uniform"])
                        && skippedLog.Events.SequenceEqual(["catalog.entries_skipped", "catalog.loaded"]) && skippedLog.Thrown == 1;
        if (!partialOk) Report("Loaded [Uniform]; log calls [catalog.entries_skipped, catalog.loaded], 1 thrown", partial, skippedLog, partialEscaped);
        Check("CAT-04 a log that throws on catalog.entries_skipped (one entry skipped) does not change the result: Loaded [Uniform], " +
              "2 log calls (entries_skipped, then loaded), no exception", partialOk);

        // A log that throws on catalog.unavailable: still Unavailable with the provider's reason, never a fault.
        var missingLog = new ThrowingAppLog("catalog.unavailable");
        var (missing, missingSame, missingEscaped) = await LoadWithAsync(At(dir.Combine("missing-with-bad-log.json")), missingLog);
        var missingOk = missing is { State: CatalogLoadState.Unavailable, Message: "the file does not exist." } && missingSame && missingEscaped is null
                        && ReferenceEquals(missing.Catalog, StationCatalogIndex.Empty)
                        && missingLog.Events.SequenceEqual(["catalog.unavailable"]) && missingLog.Thrown == 1;
        if (!missingOk) Report("Unavailable \"the file does not exist.\"; log calls [catalog.unavailable], 1 thrown", missing, missingLog, missingEscaped);
        Check("CAT-04 a log that throws on catalog.unavailable does not change the result: Unavailable \"the file does not exist.\", one log call, " +
              "no exception", missingOk);

        var jsonLog = new ThrowingAppLog("catalog.unavailable");
        var (broken, brokenSame, brokenEscaped) = await LoadWithAsync(At(Write(dir, "this is not a catalog")), jsonLog);
        var brokenOk = broken is { State: CatalogLoadState.Unavailable, Message: NotJson } && brokenSame && brokenEscaped is null
                       && jsonLog.Events.SequenceEqual(["catalog.unavailable"]) && jsonLog.Thrown == 1
                       && jsonLog.Inner.Entries[0].Exception is JsonException;
        if (!brokenOk) Report($"Unavailable \"{NotJson}\"; log calls [catalog.unavailable] with a JsonException, 1 thrown", broken, jsonLog, brokenEscaped);
        Check("CAT-04 a log that throws on catalog.unavailable (carrying its JsonException) keeps the JSON reason: Unavailable \"the file is not " +
              "valid catalog JSON.\", not \"the catalog could not be loaded.\"; one log call, no exception", brokenOk);

        // A log that throws on every call, on a file whose every entry is invalid: both lines attempted, the reason unchanged.
        var everyLog = new ThrowingAppLog(null);
        var (none, noneSame, noneEscaped) =
            await LoadWithAsync(At(Write(dir, Document([Station(""), Station("Victor", url: "ftp://victor.example.org/")]))), everyLog);
        var noneOk = none is { State: CatalogLoadState.Unavailable, Message: NoUsable } && noneSame && noneEscaped is null
                     && everyLog.Events.SequenceEqual(["catalog.entries_skipped", "catalog.unavailable"]) && everyLog.Thrown == 2;
        if (!noneOk) Report($"Unavailable \"{NoUsable}\"; log calls [catalog.entries_skipped, catalog.unavailable], 2 thrown", none, everyLog, noneEscaped);
        Check("CAT-04 a log that throws on every call, with every entry invalid: Unavailable \"the file has no usable station entry.\", " +
              "2 log calls (entries_skipped, then unavailable), no exception", noneOk);
    }

    // ─── CAT-05: DIALSHIFT_CATALOG_PATH ───

    private static void ResolveLocation(TempDirectory dir)
    {
        var baseDir = dir.Path;
        var asked = new List<string>();
        CatalogLocation Resolve(string? value) => CatalogProvider.ResolveLocation(variable => { asked.Add(variable); return value; }, baseDir);
        CatalogLocation Override(string path) => new(path, CatalogLocationSource.Override, null);
        CatalogLocation Problem(string problem) => new(null, CatalogLocationSource.Override, problem);
        bool All(string?[] values, CatalogLocation expected)
        {
            var ok = true;
            foreach (var value in values)
            {
                var actual = Resolve(value);
                if (actual == expected) continue;
                ok = false;
                Console.WriteLine($"  value {CatalogFixtures.Show(value)}: expected {expected}, actual {actual}");
            }
            return ok;
        }

        var appFolder = new CatalogLocation(Path.Combine(baseDir, CatalogProvider.FileName), CatalogLocationSource.AppFolder, null);
        Check("CAT-05 DIALSHIFT_CATALOG_PATH unset, empty or white space (spaces, tab/CR/LF, no-break space) → app-catalog.json in the base directory (AppFolder)",
            All([null, "", "   ", "\t\r\n", "\u00A0"], appFolder));
        Check("CAT-05 ResolveLocation reads exactly the variable DIALSHIFT_CATALOG_PATH", asked.Count > 0 && asked.All(v => v == "DIALSHIFT_CATALOG_PATH"));

        var fixture = dir.Combine("fixture.json");
        Check("CAT-05 an absolute override is used as given, trimmed of surrounding white space (Override, no Problem)",
            All([fixture, "  " + fixture + " \t", "\n" + fixture], Override(fixture)));
        Check("CAT-05 an absolute override is normalized (GetFullPath: sub/../fixture.json → fixture.json) and may name any file, existing or not",
            All([dir.Combine("sub", "..", "fixture.json")], Override(fixture)) && All([dir.Combine("any-name.txt")], Override(dir.Combine("any-name.txt"))));
        Check("CAT-05 a relative override → Problem \"DIALSHIFT_CATALOG_PATH must be an absolute path.\" (app-catalog.json, ./x, ../x, data/x, ~/x)",
            All(["app-catalog.json", "./app-catalog.json", "../data/output/app-catalog.json", "data/app-catalog.json", "~/app-catalog.json", " app-catalog.json "],
                Problem(MustBeAbsolute)));
        Check("CAT-05 an absolute override with an embedded NUL → Problem \"DIALSHIFT_CATALOG_PATH is not a valid path.\" (no exception)",
            All([dir.Combine("a\0b.json")], Problem(NotValidPath)));

        if (OperatingSystem.IsWindows())
        {
            Check("CAT-05 (Windows) drive and UNC paths are absolute: C:\\data\\app-catalog.json, C:/data/app-catalog.json, \\\\server\\share\\app-catalog.json",
                All([@"C:\data\app-catalog.json", "C:/data/app-catalog.json"], Override(@"C:\data\app-catalog.json"))
                && All([@"\\server\share\app-catalog.json"], Override(@"\\server\share\app-catalog.json")));
            Check("CAT-05 (Windows) drive-relative C:app-catalog.json and rooted \\data\\app-catalog.json, /data/app-catalog.json are relative → Problem",
                All(["C:app-catalog.json", @"\data\app-catalog.json", "/data/app-catalog.json"], Problem(MustBeAbsolute)));
            Skip("CAT-05 (macOS/Linux) Windows drive and UNC spellings are relative here → Problem", "Unix path rules (macOS/Linux only)");
        }
        else
        {
            Skip("CAT-05 (Windows) drive and UNC paths are absolute; drive-relative and rooted paths are Problems", "Windows path rules (windows-latest)");
            Check("CAT-05 (macOS/Linux) Windows drive and UNC spellings are relative here → Problem (C:\\data\\x.json, C:/data/x.json, \\\\server\\share\\x.json)",
                All([@"C:\data\app-catalog.json", "C:/data/app-catalog.json", @"\\server\share\app-catalog.json"], Problem(MustBeAbsolute)));
        }

        Check("CAT-05 ResolveLocation(null getter) and ResolveLocation(getter, null base) throw ArgumentNullException; so does a null location or log for the provider",
            Throws<ArgumentNullException>(() => CatalogProvider.ResolveLocation(null!, baseDir))
            && Throws<ArgumentNullException>(() => CatalogProvider.ResolveLocation(_ => null, null!))
            && Throws<ArgumentNullException>(() => _ = new CatalogProvider(null!, new RecordingAppLog()))
            && Throws<ArgumentNullException>(() => _ = new CatalogProvider(appFolder, null!)));
    }

    private static async Task OverrideLoadsAsync(TempDirectory dir)
    {
        var fixture = Write(dir, Document([Station("Tango"), Station("Uniform")]));
        var location = CatalogProvider.ResolveLocation(_ => "  " + fixture + "  ", "/unused-base-directory");
        var (result, log, _) = await LoadAsync(location);
        var line = log.Entries.SingleOrDefault(e => e.EventName == "catalog.loaded")?.Message ?? "";
        Check("CAT-05 a fixture loads through the override: Loaded, 2 stations, catalog.loaded names the path and [DIALSHIFT_CATALOG_PATH]",
            location.Source == CatalogLocationSource.Override && result.State == CatalogLoadState.Loaded && result.Catalog.Entries.Count == 2
            && line.Contains($" from {fixture} [DIALSHIFT_CATALOG_PATH] in ", StringComparison.Ordinal));

        using var appDir = new TempDirectory("catalog-appfolder");
        File.WriteAllText(appDir.Combine(CatalogProvider.FileName), Document([Station("Victor")]));
        var (fromFolder, folderLog, _) = await LoadAsync(CatalogProvider.ResolveLocation(_ => null, appDir.Path));
        Check("CAT-05 unset, the base directory's app-catalog.json loads and catalog.loaded says [app folder]",
            fromFolder.State == CatalogLoadState.Loaded && fromFolder.Catalog.Entries.Single().Name == "Victor"
            && folderLog.Entries.Any(e => e.EventName == "catalog.loaded" && e.Message.Contains(" [app folder] in ", StringComparison.Ordinal)));

        // No fallback: the test binary's folder holds the real app-catalog.json (CAT-02), yet a relative override stays Unavailable.
        var withRealFile = CatalogProvider.ResolveLocation(_ => CatalogProvider.FileName, AppContext.BaseDirectory);
        Check("CAT-05 precondition: the test binary's folder holds app-catalog.json", File.Exists(Path.Combine(AppContext.BaseDirectory, CatalogProvider.FileName)));
        await CheckUnavailableAsync("CAT-05 a relative override does not fall back to the app folder, even with app-catalog.json there: Unavailable " +
                                    "\"DIALSHIFT_CATALOG_PATH must be an absolute path.\", logged against (DIALSHIFT_CATALOG_PATH)",
            withRealFile, MustBeAbsolute);
        await CheckUnavailableAsync("CAT-05 an override with a NUL loads as Unavailable \"DIALSHIFT_CATALOG_PATH is not a valid path.\"",
            CatalogProvider.ResolveLocation(_ => dir.Combine("x\0y.json"), AppContext.BaseDirectory), NotValidPath);
    }
}
