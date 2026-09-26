using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DialShift.App.Services;
using DialShift.App.ViewModels;
using DialShift.App.Views.Dialogs;
using DialShift.Core;
using DialShift.Core.Catalog;
using DialShift.Tests.Fakes;
using DialShift.Tests.Ui;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// The <c>CatalogPerf</c> suite: CAT-16 (brief 3 §5.2, §9; D69, D80; docs/catalog-contracts.md §8). Measures the real
/// <c>app-catalog.json</c> the build copies next to the test binary, and a synthetic 10,000-entry catalog built from it.
/// </summary>
/// <remarks>
/// <para><b>Method (D69).</b> "Load" is one <see cref="CatalogProvider.GetCatalogAsync"/> on a fresh provider (read, parse,
/// validate, index: contracts §4.2 steps 1–9), timed by <see cref="Stopwatch"/> around the call and also as the
/// <c>catalog.loaded</c> line reports it. The first load of the real file in this process is printed as the cold load and
/// serves as the warm-up; the gated number is the median of the next 7. It is cold in the D80 sense only when this suite
/// runs alone (<c>--filter CatalogPerf</c>): in the full run, earlier suites have already loaded the catalog. Every other
/// measurement is one warm-up, then the median of 7, each preceded by a full collection so no sample pays for an
/// earlier one's garbage.</para>
/// <para><b>Gates.</b> Only the budgets: at most 10,000 entries; load median under 50 ms and, per query, search median
/// under 10 ms and the p95 of every search sample of a catalog under 10 ms, at the real count and at 10,000. Each timing
/// gate passes on its best of up to 3 attempts (D102). The measurement above is attempt 1. While some gate has missed
/// its budget in every attempt so far, the loads and searches are measured again, in the same order, their lines
/// labelled "attempt n": in this process in a full run, where earlier suites have already brought the load path to the
/// JIT's optimized code before attempt 1, and in a fresh process of this binary (<c>--catalog-perf-attempt n</c>) in a
/// filtered run, where attempt 1 may be the process's first catalog work (a repeat in that process would measure code
/// the JIT has optimized since, 20–50% faster). So no attempt runs warmer than attempt 1, and noise on a shared machine
/// only ever adds time: a regression over a budget fails every attempt. Each check line lists every attempt's value.
/// The timing budgets are defined for a Release build on Apple Silicon (D69), and Windows hardware is reported, not
/// gated (contracts §8 CAT-16), so the timing gates run only in an optimized build on macOS arm64 and report SKIP with
/// the reason elsewhere; the numbers are printed everywhere, and the entry count is gated everywhere. Index build,
/// filter lists, allocation per search and UI-thread time are printed, not gated. Every printed measurement starts
/// with <c>CAT-16</c>.</para>
/// <para><b>UI thread.</b> The Add dialog is opened on Avalonia's headless platform over the real catalog, with a
/// dispatcher that queues the view model's posts so each one is timed on its own: a keystroke's synchronous work, the
/// applied search (50 result rows, their logo requests to a real <see cref="CatalogLogoLoader"/> whose handler answers
/// 404 at once, so nothing leaves the machine), the layout pass and the rendered frame, a highlight move with its detail
/// pane, a filter change and the catalog's arrival. Headless Skia layout is not a native window's: these numbers bound
/// the view model's and Avalonia's own work, not a Windows or macOS compositor's.</para>
/// </remarks>
internal static class CatalogPerfTests
{
    private const int Runs = 7;
    private const int MaxAttempts = 3;
    private const double LoadBudgetMs = 50;
    private const double SearchBudgetMs = 10;
    private const double UiReportMs = 5;
    private const int SyntheticCount = CatalogProvider.MaxEntries;

    public static async Task RunAsync()
    {
        var optimized = IsOptimized(typeof(StationCatalogIndex).Assembly) && IsOptimized(typeof(CatalogProvider).Assembly);
        PrintMachine(optimized);
        var appleSilicon = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        string? ungated = !optimized
            ? "the JIT optimizer is disabled (a Debug build); D69's budgets are defined for Release, so the numbers above are printed, not gated"
            : !appleSilicon
                ? $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} is not macOS on arm64: D69's budgets gate every optimized macOS arm64 run (this Mac and CI runners alike); other platforms are reported, not gated (contracts §8 CAT-16)"
                : null;

        var measured = await MeasureAsync(attempt: 1);

        // ─── the gates (D69), each on its best of up to MaxAttempts attempts (D102) ───
        if (ungated is null)
            await GateAsync(measured.Real.Entries.Count, measured.Gated);
        else
        {
            Skip("CAT-16 load median < 50 ms (real and synthetic 10,000)", ungated);
            Skip("CAT-16 search median and p95 < 10 ms (real and synthetic 10,000)", ungated);
        }

        // ─── UI thread ───
        await Headless.RunAsync(() => UiThreadAsync(measured.Cold.Result));
    }

    // ─── the measurements (attempt 1, and each later attempt of the gates) ───

    /// <summary>What the gates read from one attempt; a later attempt in a child process prints it as one JSON line.</summary>
    private sealed record AttemptResult(double RealLoadMs, double SyntheticLoadMs, SearchSummary RealSearch, SearchSummary SyntheticSearch);

    /// <summary>A search set: its slowest per-query median and the p95 of all its samples.</summary>
    private sealed record SearchSummary(int Queries, string SlowestQuery, double SlowestMedianMs, double P95Ms);

    private sealed record Measurement(LoadSample Cold, StationCatalogIndex Real, AttemptResult Gated);

    /// <summary>Loads, index and filter lists, and searches, in this order, printing every number. Attempt 1 is the
    /// suite's measurement: its preconditions are counted checks. A later attempt (D102) labels its lines with its number,
    /// asserts the same preconditions without counting them, and leaves out the index, filter-list and allocation reports:
    /// nothing gates them, their collections are most of an attempt's time, and without them its searches run no
    /// warmer.</summary>
    private static async Task<Measurement> MeasureAsync(int attempt)
    {
        Action<string, bool> require = attempt == 1 ? Check : (name, condition) =>
        {
            if (!condition) throw new CheckFailedException($"precondition of CAT-16 attempt {attempt}: {name}");
        };
        var suffix = attempt == 1 ? "" : $", attempt {attempt}";
        var realPath = Path.Combine(AppContext.BaseDirectory, CatalogProvider.FileName);
        require("CAT-16 the real app-catalog.json is next to the test binary", File.Exists(realPath));

        // ─── load: the real file ───
        var cold = await LoadAsync(realPath);
        require("CAT-16 the real catalog loads", cold.Result.State == CatalogLoadState.Loaded);
        var real = cold.Result.Catalog;
        if (attempt == 1) Console.WriteLine($"CAT-16 real catalog: {real.Entries.Count} stations, {new FileInfo(realPath).Length / 1024.0 / 1024.0:F2} MiB");
        require($"CAT-16 entry count {real.Entries.Count} ≤ {CatalogProvider.MaxEntries} (D69)", real.Entries.Count <= CatalogProvider.MaxEntries);
        if (attempt == 1)
            Console.WriteLine($"CAT-16 cold first load (first in this process; reported, not gated) {cold.WallMs:F1} ms wall, catalog.loaded {cold.ReportedMs} ms " +
                              $"({(cold.WallMs < LoadBudgetMs ? "within" : "over")} the {LoadBudgetMs:0} ms budget)");
        else
            Console.WriteLine($"CAT-16 warm-up load (real {real.Entries.Count}{suffix}; not gated) {cold.WallMs:F1} ms wall, catalog.loaded {cold.ReportedMs} ms");
        var realLoads = await LoadRunsAsync(realPath);
        PrintLoads($"real {real.Entries.Count}{suffix}", realLoads);

        // ─── load: the synthetic 10,000 ───
        using var temp = new TempDirectory("catalog-perf");
        var syntheticPath = temp.Combine(CatalogProvider.FileName);
        WriteSynthetic(realPath, syntheticPath);
        var syntheticFirst = await LoadAsync(syntheticPath); // warm-up
        var synthetic = syntheticFirst.Result.Catalog;
        require($"CAT-16 the synthetic catalog built from the real one loads {SyntheticCount} stations",
            syntheticFirst.Result.State == CatalogLoadState.Loaded && synthetic.Entries.Count == SyntheticCount);
        var syntheticLoads = await LoadRunsAsync(syntheticPath);
        PrintLoads($"synthetic {SyntheticCount}{suffix}", syntheticLoads);

        // ─── index build and filter lists (inside the load, and on the thread pool before the dialog is usable) ───
        if (attempt == 1)
        {
            var realLists = IndexAndLists(real);
            IndexAndLists(synthetic);
            Console.WriteLine($"CAT-16 load median + filter lists, real {real.Entries.Count}: {Median(realLoads.Select(l => l.WallMs)) + realLists:F1} ms " +
                              $"(the time from GetCatalogAsync to a searchable, filterable dialog, UI thread excluded)");
        }

        // ─── search ───
        var queries = Queries(real.Entries);
        var realSearch = SearchSet($"real {real.Entries.Count}{suffix}", real, queries);
        var syntheticSearch = SearchSet($"synthetic {SyntheticCount}{suffix}", synthetic, queries);
        if (attempt == 1) PrintAllocations(real.Entries.Count, realSearch, syntheticSearch);

        return new Measurement(cold, real, new AttemptResult(Median(realLoads.Select(l => l.WallMs)), Median(syntheticLoads.Select(l => l.WallMs)),
            Summarize(realSearch), Summarize(syntheticSearch)));
    }

    // ─── load ───

    private sealed record LoadSample(CatalogLoadResult Result, double WallMs, long ReportedMs);

    private static async Task<LoadSample> LoadAsync(string path)
    {
        Collect();
        var log = new RecordingAppLog();
        var provider = new CatalogProvider(new CatalogLocation(path, CatalogLocationSource.Override, null), log);
        var start = Stopwatch.GetTimestamp();
        var result = await provider.GetCatalogAsync();
        var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var reported = log.Entries.Where(e => e.EventName == "catalog.loaded")
            .Select(e => Regex.Match(e.Message, @" in (\d+) ms\.$"))
            .Where(m => m.Success)
            .Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .FirstOrDefault(-1);
        return new LoadSample(result, wall, reported);
    }

    private static async Task<List<LoadSample>> LoadRunsAsync(string path)
    {
        var samples = new List<LoadSample>();
        for (var i = 0; i < Runs; i++) samples.Add(await LoadAsync(path));
        return samples;
    }

    private static void PrintLoads(string label, List<LoadSample> loads)
    {
        var wall = loads.Select(l => l.WallMs).ToList();
        Console.WriteLine($"CAT-16 load median ({label}) {Median(wall):F1} ms wall [min {wall.Min():F1}, max {wall.Max():F1}], " +
                          $"catalog.loaded median {Median(loads.Select(l => (double)l.ReportedMs)):F0} ms; runs: {string.Join(" ", wall.Select(w => w.ToString("F1", CultureInfo.InvariantCulture)))}");
    }

    /// <summary>The real file with its stations repeated, in order, until there are <see cref="SyntheticCount"/>. A copy's
    /// stream URL gets a query parameter, so no two entries share (name, country, stream URL) as in the real file; a copy
    /// whose marked URL would pass the 2,048-character limit keeps its URL. Non-ASCII text is written unescaped, as the
    /// pipeline writes it, so the parse does the same work per character as on the real file.</summary>
    private static void WriteSynthetic(string realPath, string path)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(realPath))!.AsObject();
        var stations = root["stations"]!.AsArray();
        var count = stations.Count;
        for (var i = count; i < SyntheticCount; i++)
        {
            var copy = stations[i % count]!.DeepClone().AsObject();
            var url = copy["stream_url"]!.GetValue<string>();
            var marked = url + (url.Contains('?') ? "&" : "?") + "dialshift-perf-copy=" + (i / count).ToString(CultureInfo.InvariantCulture);
            if (marked.Length <= 2_048) copy["stream_url"] = marked;
            stations.Add(copy);
        }
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(path, root.ToJsonString(options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Prints the index build and each field's <see cref="StationCatalogQuery.AvailableValues"/>; returns the
    /// median of the five lists together, as the dialog builds them.</summary>
    private static double IndexAndLists(StationCatalogIndex index)
    {
        var entries = index.Entries;
        var label = $"({entries.Count})";
        var build = Time(() => new StationCatalogIndex(entries));
        Console.WriteLine($"CAT-16 index build {label} median {build.Median:F2} ms, max {build.Max:F2} ms");
        foreach (var field in Enum.GetValues<CatalogField>())
        {
            var count = StationCatalogQuery.AvailableValues(entries, field).Count;
            var values = Time(() => StationCatalogQuery.AvailableValues(entries, field));
            Console.WriteLine($"CAT-16 AvailableValues {field} {label} median {values.Median:F2} ms, max {values.Max:F2} ms, {count} values");
        }
        var all = Time(() => Enum.GetValues<CatalogField>().Select(f => StationCatalogQuery.AvailableValues(entries, f)).ToList());
        Console.WriteLine($"CAT-16 AvailableValues all five {label} median {all.Median:F2} ms, max {all.Max:F2} ms");
        return all.Median;
    }

    // ─── search ───

    private sealed record Query(string Name, string? Text, CatalogFilters Filters);

    private sealed record SearchTiming(string Name, double MedianMs, double MaxMs, long Bytes, int Total, double[] Samples);

    /// <summary>brief 3 §9 and the orchestrator's set: empty, one letter, an ASCII word, diacritics, Greek, the frequency
    /// forms (D79), no match, each filter alone, the Language filter with text (D84), and all five together, with and
    /// without text. Filter values come from the catalog itself (its most common value per field, and the five values of
    /// its most-voted entry that has all five), so no station fact is a literal here (CAT-17).</summary>
    private static List<Query> Queries(IReadOnlyList<StationCatalogEntry> entries)
    {
        static string MostCommon(IEnumerable<string> values) => values.Where(v => v.Length > 0)
            .GroupBy(v => v, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;

        var country = MostCommon(entries.Select(e => e.Country));
        var city = MostCommon(entries.Select(e => e.City));
        var type = MostCommon(entries.Select(e => e.Type));
        var genre = MostCommon(entries.Select(e => e.Genre));
        var language = MostCommon(entries.SelectMany(e => StationCatalogQuery.LanguageNames(e.Language)));
        var full = entries.Where(e => e is { Country.Length: > 0, City.Length: > 0, Type.Length: > 0, Genre.Length: > 0, Language.Length: > 0 })
            .OrderByDescending(e => e.Votes ?? 0).First();
        var all = new CatalogFilters(full.Country, full.City, full.Type, full.Genre, StationCatalogQuery.LanguageNames(full.Language)[0]);
        var none = CatalogFilters.None;
        return
        [
            new("empty", "", none),
            new("one letter \"r\"", "r", none),
            new("ASCII word \"radio\"", "radio", none),
            new("diacritics \"münchen\"", "münchen", none),
            new("diacritics \"fréquence\"", "fréquence", none),
            new("Greek \"αθήνα\"", "αθήνα", none),
            new("Greek \"ΚΟΣΜΟΣ\"", "ΚΟΣΜΟΣ", none),
            new("frequency \"101.5\"", "101.5", none),
            new("frequency \"FM 101.5\"", "FM 101.5", none),
            new("frequency \"1015\"", "1015", none),
            new("frequency \"1017\"", "1017", none),
            new("frequency \"1017 kHz\"", "1017 kHz", none),
            new("no match \"zqxjv\"", "zqxjv", none),
            new("Country filter", "", none with { Country = country }),
            new("City filter", "", none with { City = city }),
            new("Type filter", "", none with { Type = type }),
            new("Genre filter", "", none with { Genre = genre }),
            new("Language filter (D84)", "", none with { Language = language }),
            new("Language filter + \"radio\"", "radio", none with { Language = language }),
            new("all five filters", "", all),
            new("all five filters + text", full.Name[..Math.Min(3, full.Name.Length)], all),
        ];
    }

    private static List<SearchTiming> SearchSet(string label, StationCatalogIndex index, List<Query> queries)
    {
        var timings = new List<SearchTiming>();
        foreach (var query in queries)
        {
            var total = StationCatalogQuery.Search(index, query.Text, query.Filters).TotalCount; // the warm-up
            var samples = new double[Runs];
            var bytes = new long[Runs];
            Collect();
            for (var i = 0; i < Runs; i++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                StationCatalogQuery.Search(index, query.Text, query.Filters);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                bytes[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
            var timing = new SearchTiming(query.Name, Median(samples), samples.Max(), (long)Median(bytes.Select(b => (double)b)), total, samples);
            timings.Add(timing);
            Console.WriteLine($"CAT-16 search ({label}) {timing.Name}: median {timing.MedianMs:F3} ms, max {timing.MaxMs:F3} ms, " +
                              $"{timing.Bytes} B/search, {timing.Total} matches");
        }
        var all = timings.SelectMany(t => t.Samples).ToList();
        Console.WriteLine($"CAT-16 search ({label}) all {all.Count} samples: median {Median(all):F3} ms, p95 {Percentile(all, 0.95):F3} ms, " +
                          $"max {all.Max():F3} ms");
        return timings;
    }

    private static SearchSummary Summarize(List<SearchTiming> set)
    {
        var slowest = set.MaxBy(s => s.MedianMs)!;
        return new SearchSummary(set.Count, slowest.Name, slowest.MedianMs, Percentile(set.SelectMany(s => s.Samples), 0.95));
    }

    private static void PrintAllocations(int realCount, List<SearchTiming> real, List<SearchTiming> synthetic)
    {
        var growth = real.Zip(synthetic, (r, s) => (r.Name, Real: r.Bytes, Synthetic: s.Bytes, Delta: s.Bytes - r.Bytes)).ToList();
        foreach (var (name, r, s, delta) in growth.Where(g => g.Delta != 0))
            Console.WriteLine($"CAT-16 allocation per search differs: {name}: {r} B at {realCount}, {s} B at {SyntheticCount}");
        var largest = growth.MaxBy(g => Math.Abs(g.Delta));
        Console.WriteLine($"CAT-16 allocation per search: {real.Min(t => t.Bytes)}–{real.Max(t => t.Bytes)} B at {realCount}, " +
                          $"{synthetic.Min(t => t.Bytes)}–{synthetic.Max(t => t.Bytes)} B at {SyntheticCount}; " +
                          $"largest difference {largest.Delta} B ({largest.Name}) for {SyntheticCount - realCount} more entries " +
                          $"({(growth.All(g => Math.Abs(g.Delta) <= 1024) ? "independent of catalog size" : "GROWS with the catalog")})");
    }

    // ─── the gates (D69, D102) ───

    private const string AttemptMode = "--catalog-perf-attempt";
    private const string AttemptResultPrefix = "CAT-16-ATTEMPT-RESULT ";
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>One timing gate: its value in an attempt, its budget, the value's format, and the check name for the
    /// attempt given.</summary>
    private sealed record Gate(Func<AttemptResult, double> Value, double BudgetMs, string Format, Func<AttemptResult, string> Title);

    /// <summary>D69's timing budgets, each passing on its best attempt (D102). Attempt 1 is the suite's measurement. While
    /// some gate has missed its budget in every attempt so far, and fewer than <see cref="MaxAttempts"/> were made, the
    /// loads and searches are measured again, in a JIT state no warmer than attempt 1's. In a full run, earlier suites
    /// have loaded the catalog many times, so attempt 1 already measures JIT-optimized code and a repeat in this process
    /// measures the same. In a filtered run (<c>--filter CatalogPerf</c> among them) attempt 1 may be this process's first
    /// catalog work, and a repeat here would measure code the JIT has optimized since, 20–50% faster, so the repeat runs
    /// in a fresh process (<see cref="MeasureInChildAsync"/>). Each check line lists every attempt's value.</summary>
    private static async Task GateAsync(int realCount, AttemptResult first)
    {
        var inProcess = TestHarness.Filter is null;
        Gate Load(string label, Func<AttemptResult, double> value) =>
            new(value, LoadBudgetMs, "F1", attempt => $"CAT-16 load median ({label}) {value(attempt):F1} ms < {LoadBudgetMs:0} ms");
        Gate[] Search(string label, Func<AttemptResult, SearchSummary> set) =>
        [
            new(a => set(a).SlowestMedianMs, SearchBudgetMs, "F3", attempt =>
                $"CAT-16 search median < {SearchBudgetMs:0} ms for each of {set(attempt).Queries} queries ({label}; slowest {set(attempt).SlowestQuery} {set(attempt).SlowestMedianMs:F3} ms)"),
            new(a => set(a).P95Ms, SearchBudgetMs, "F3", attempt =>
                $"CAT-16 search p95 over {set(attempt).Queries * Runs} samples ({label}) {set(attempt).P95Ms:F3} ms < {SearchBudgetMs:0} ms"),
        ];
        var real = $"real {realCount}";
        var synthetic = $"synthetic {SyntheticCount}";
        Gate[] gates =
        [
            Load(real, a => a.RealLoadMs), Load(synthetic, a => a.SyntheticLoadMs),
            .. Search(real, a => a.RealSearch), .. Search(synthetic, a => a.SyntheticSearch),
        ];

        var attempts = new List<AttemptResult> { first };
        bool Met(Gate gate) => attempts.Any(a => gate.Value(a) < gate.BudgetMs);
        while (attempts.Count < MaxAttempts && !gates.All(Met))
        {
            var next = attempts.Count + 1;
            var missed = string.Join("; ", gates.Where(g => !Met(g)).Select(g => g.Title(attempts.MinBy(g.Value)!)));
            Console.WriteLine($"CAT-16 attempt {next} of up to {MaxAttempts} (D102), " +
                              $"{(inProcess ? "in this process (a full run)" : "in a fresh process (a filtered run)")}; over budget in every attempt so far: {missed}");
            attempts.Add(inProcess ? (await MeasureAsync(next)).Gated : await MeasureInChildAsync(next));
        }
        foreach (var gate in gates)
        {
            var best = attempts.MinBy(gate.Value)!;
            var values = string.Join(", ", attempts.Select(a => gate.Value(a).ToString(gate.Format, CultureInfo.InvariantCulture)));
            Check($"{gate.Title(best)} (attempts: {values} ms; best of up to {MaxAttempts}, D102)", gate.Value(best) < gate.BudgetMs);
        }
    }

    /// <summary>A later attempt of a filtered run: <see cref="MeasureAsync"/> in a fresh process
    /// (<see cref="RunAttemptChildAsync"/>), whose lines are printed here.</summary>
    private static async Task<AttemptResult> MeasureInChildAsync(int attempt)
    {
        var startInfo = SelfProcess.StartInfo(AttemptMode, attempt.ToString(CultureInfo.InvariantCulture));
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        using var process = Process.Start(startInfo) ?? throw new CheckFailedException($"precondition: CAT-16 attempt {attempt}'s process did not start");
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(AttemptTimeout);
        AttemptResult? result = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line.StartsWith(AttemptResultPrefix, StringComparison.Ordinal))
                    result = JsonSerializer.Deserialize<AttemptResult>(line[AttemptResultPrefix.Length..]);
                else
                    Console.WriteLine(line);
            }
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new CheckFailedException($"precondition: CAT-16 attempt {attempt}'s process did not finish within {AttemptTimeout.TotalSeconds:0} s");
        }
        if (process.ExitCode != 0 || result is null)
            throw new CheckFailedException($"precondition: CAT-16 attempt {attempt}'s process exited {process.ExitCode} without a result; stderr: {await stderr}");
        return result;
    }

    /// <summary>The <c>--catalog-perf-attempt &lt;n&gt;</c> child mode (D102; never part of a normal run): attempt
    /// <c>n</c> of <see cref="MeasureAsync"/> in this fresh process, printing its lines, then one
    /// <see cref="AttemptResultPrefix"/> line with what the gates read.</summary>
    public static async Task<int> RunAttemptChildAsync(string[] args)
    {
        if (args is not [AttemptMode, var number] || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) ||
            attempt < 2 || attempt > MaxAttempts)
        {
            Console.Error.WriteLine($"Usage: DialShift.Tests {AttemptMode} <2..{MaxAttempts}>");
            return 64;
        }
        var measured = await MeasureAsync(attempt);
        Console.WriteLine(AttemptResultPrefix + JsonSerializer.Serialize(measured.Gated));
        return 0;
    }

    // ─── UI thread (headless Avalonia, real catalog) ───

    /// <summary>Queues the view model's posts; <see cref="Drain"/> runs them on the calling (UI) thread and times them.</summary>
    private sealed class TimedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new();

        public int Pending => queue.Count;

        public bool CheckAccess() => true;

        public void Post(Action action) => queue.Enqueue(action);

        public Task InvokeAsync(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() => { action(); done.SetResult(); });
            return done.Task;
        }

        /// <summary>Runs the posts queued so far (not those a background search queues meanwhile, which wait for the next
        /// drain); returns the milliseconds they took together.</summary>
        public double Drain()
        {
            var start = Stopwatch.GetTimestamp();
            for (var count = queue.Count; count > 0 && queue.TryDequeue(out var action); count--) action();
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>Answers every logo request with 404 at once: the loader's full synchronous path runs, nothing is sent.</summary>
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static async Task UiThreadAsync(CatalogLoadResult loaded)
    {
        var ui = new TimedDispatcher();
        using var logos = new CatalogLogoLoader(new NotFoundHandler());
        var catalog = new FakeCatalogProvider { Result = loaded };
        var editor = new StationEditorViewModel(new Settings(), null, new RecordingDialogService(), _ => { }, catalog, logos, ui, TimeSpan.Zero);
        var dialog = new StationEditorDialog(editor);
        dialog.Show();
        var slow = new List<string>();

        async Task<bool> PostedAsync()
        {
            var deadline = new TestDeadline(Headless.WaitTimeout);
            while (ui.Pending == 0)
            {
                if (deadline.HasPassed) return false;
                await Task.Delay(1);
            }
            return true;
        }

        // What one UI-thread step costs: the view model's posts, then a layout pass, then one rendered frame; and whether a
        // garbage collection ran during the posts or the layout, so a GC pause is told apart from the step's own work.
        (double Posts, double Layout, double Render, string Gc) Settle()
        {
            var collections = GC.CollectionCount(0);
            var posts = ui.Drain();
            var postsGc = GC.CollectionCount(0) - collections;
            var start = Stopwatch.GetTimestamp();
            dialog.UpdateLayout();
            var layout = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var layoutGc = GC.CollectionCount(0) - collections - postsGc;
            start = Stopwatch.GetTimestamp();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var gc = (postsGc > 0 ? ", GC in the posts" : "") + (layoutGc > 0 ? ", GC in the layout" : "");
            return (posts, layout, Stopwatch.GetElapsedTime(start).TotalMilliseconds, gc);
        }

        // Prints the median and max after the warm-ups, and lists every step over UiReportMs, warm-ups included: a
        // first-time cost (the first overlay, the first Greek text) is one the user pays too.
        void Report(string what, IReadOnlyList<(string Step, double Ms)> samples, int warmUps = 0)
        {
            var measured = samples.Skip(warmUps).Select(s => s.Ms).ToList();
            Console.WriteLine($"CAT-16 UI thread {what}: median {Median(measured):F2} ms, max {measured.Max():F2} ms " +
                              $"(n={measured.Count}{(warmUps > 0 ? $" after {warmUps} warm-up" : "")})");
            slow.AddRange(samples.Select((s, i) => (s.Step, s.Ms, WarmUp: i < warmUps)).Where(s => s.Ms > UiReportMs)
                .Select(s => $"{what} [{s.Step}{(s.WarmUp ? ", warm-up" : "")}] {s.Ms:F2} ms"));
        }

        // The catalog arrives: filter lists and status line (the lists themselves were built on the thread pool).
        if (!await PostedAsync()) throw new TimeoutException("The Add dialog's catalog did not arrive.");
        var arrival = Settle();
        Report("catalog arrival (ApplyCatalog: five option lists, status)", [("arrival", arrival.Posts)]);
        Report("catalog arrival layout", [("arrival", arrival.Layout)]);
        if (!await PostedAsync()) throw new TimeoutException("The Add dialog's first search did not land.");
        var first = Settle();
        Report("first results (50 rows, overlay closed)", [("first results", first.Posts)]);
        Check("CAT-16 UI-thread measurement: the Add dialog shows the real catalog's first results", editor.Results.Count == StationCatalogQuery.DefaultCap);

        // Keystrokes: each one's synchronous work (text input → binding → SearchText → a search scheduled on the thread
        // pool), then the applied result of that keystroke's search. Real typing coalesces behind the 200 ms debounce
        // (D72), so a burst costs one applied result; this applies one per keystroke, the worst case. The first word is
        // the warm-up; "münchen" is typed twice, so a first-time cost (new glyphs in the results) shows against a repeat.
        const string warmUpWord = "radio";
        var box = Headless.ByName<TextBox>(dialog, "Search stations");
        box.Focus();
        var keystroke = new List<(string, double)>();
        var apply = new List<(string, double)>();
        var layout = new List<(string, double)>();
        var render = new List<(string, double)>();
        foreach (var word in new[] { warmUpWord, "radio", "αθήνα", "münchen", "münchen", "101.5", "zqxjv" })
        {
            box.SelectAll();
            foreach (var c in word)
            {
                var start = Stopwatch.GetTimestamp();
                dialog.KeyTextInput(c.ToString());
                var typed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (!await PostedAsync()) throw new TimeoutException($"The search after typing '{c}' did not land.");
                var step = Settle();
                var text = $"\"{editor.SearchText}\", {editor.Results.Count} rows";
                keystroke.Add((text, typed));
                apply.Add((text + step.Gc, step.Posts));
                layout.Add((text + step.Gc, step.Layout));
                render.Add((text, step.Render));
            }
        }
        Check("CAT-16 UI-thread measurement: the last typed text is the search text", editor.SearchText == "zqxjv" && editor.HasNoMatches);
        Report("per keystroke (TextBox input → SearchText setter → search scheduled)", keystroke, warmUpWord.Length);
        Report("per applied search (up to 50 rows built, results bound, their logo requests)", apply, warmUpWord.Length);
        Report("layout after an applied search", layout, warmUpWord.Length);
        Report("rendered frame after an applied search (headless Skia)", render, warmUpWord.Length);

        // A filter change and the detail pane: "r" lists 50 rows, then Down walks all of them.
        box.SelectAll();
        dialog.KeyTextInput("r");
        if (!await PostedAsync()) throw new TimeoutException("The search for 'r' did not land.");
        Settle();
        var filter = new List<(string, double)>();
        foreach (var option in editor.LanguageOptions.Skip(1).Take(Runs + 1))
        {
            var collections = GC.CollectionCount(0);
            var start = Stopwatch.GetTimestamp();
            editor.SelectedLanguage = option;
            var set = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var setGc = GC.CollectionCount(0) > collections ? ", GC in the setter" : "";
            if (!await PostedAsync()) throw new TimeoutException("The search after a filter change did not land.");
            var step = Settle();
            filter.Add(($"{option.Label}, {editor.TotalCount} matches{setGc}{step.Gc}", set + step.Posts + step.Layout));
        }
        Report("per Language filter change (setter + applied search + layout)", filter, 1);
        editor.SelectedLanguage = CatalogFilterOption.All;
        if (!await PostedAsync()) throw new TimeoutException("The search after resetting the filter did not land.");
        Settle();

        var highlight = new List<(string, double)>();
        for (var i = 0; i < editor.Results.Count; i++)
        {
            var collections = GC.CollectionCount(0);
            var start = Stopwatch.GetTimestamp();
            editor.MoveHighlight(1); // what Down does: the list selection and the detail pane, with its logo request
            dialog.UpdateLayout();
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var notes = editor.DetailRow?.Notes.Length ?? 0;
            highlight.Add(($"row {i + 1}, {notes} characters of notes{(GC.CollectionCount(0) > collections ? ", GC" : "")}", ms));
            Settle();
        }
        Report("per highlight move (detail pane update + layout)", highlight, 1);

        var pick = Stopwatch.GetTimestamp();
        editor.SelectEntryCommand.Execute(null);
        dialog.UpdateLayout();
        Report("pick (fill the three fields, close the overlay, layout)", [("pick", Stopwatch.GetElapsedTime(pick).TotalMilliseconds)]);

        Console.WriteLine(slow.Count == 0
            ? $"CAT-16 UI thread: no step exceeded {UiReportMs:0} ms"
            : $"CAT-16 UI thread: steps over {UiReportMs:0} ms:\n  " + string.Join("\n  ", slow));
        editor.CancelCommand.Execute(null);
        ui.Drain();
    }

    // ─── helpers ───

    private static (double Median, double Max) Time(Func<object> work)
    {
        GC.KeepAlive(work()); // the warm-up
        var samples = new double[Runs];
        for (var i = 0; i < Runs; i++)
        {
            Collect();
            var start = Stopwatch.GetTimestamp();
            GC.KeepAlive(work());
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return (Median(samples), samples.Max());
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Nearest-rank percentile.</summary>
    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    /// <summary>False for an assembly built with the JIT optimizer disabled (a Debug build).</summary>
    private static bool IsOptimized(Assembly assembly) => assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    private static void PrintMachine(bool optimized)
    {
        var cpu = "";
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                using var sysctl = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl", "-n machdep.cpu.brand_string")
                    { RedirectStandardOutput = true, UseShellExecute = false });
                cpu = sysctl?.StandardOutput.ReadToEnd().Trim() ?? "";
                sysctl?.WaitForExit();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                cpu = "";
            }
        }
        Console.WriteLine($"CAT-16 machine: {(cpu.Length > 0 ? cpu : "cpu unknown")}, {Environment.ProcessorCount} logical CPUs, " +
                          $"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}, " +
                          $"{(optimized ? "optimized (Release)" : "JIT optimizer disabled (Debug)")}, server GC {System.Runtime.GCSettings.IsServerGC}");
    }
}
