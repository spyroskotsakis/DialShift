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
/// <para><b>Method (D69, D102).</b> "Load" is one <see cref="CatalogProvider.GetCatalogAsync"/> on a fresh provider (read,
/// parse, validate, index: contracts §4.2 steps 1–9), timed by <see cref="Stopwatch"/> around the call and also as the
/// <c>catalog.loaded</c> line reports it. The first load of the real file in this process is printed as the cold load
/// (reported, not gated), then, for continuity with earlier evidence, the number gated before D102: the median of 7
/// loads of each file after one warm-up (reported, not gated). Then the measured code is brought to the tiered JIT's steady state (D102):
/// <see cref="WarmUpRounds"/> rounds, each of <see cref="WarmUpLoads"/> loads of each file, one search per query on
/// each catalog, <see cref="WarmUpLoads"/> passes of the calibration workload and one timed calibration, then a
/// <see cref="WarmUpPause"/> pause for the JIT's background compilation. Only then is anything gated: the median of 7 loads of each file, and per query
/// one more warm-up search, then 7 samples; each load and each query's samples preceded by a full collection, so no
/// sample pays for an earlier one's garbage. Measured this way the numbers do not depend on what the suites before this
/// one ran, and a repeat measures the same.</para>
/// <para><b>Gates.</b> Only the budgets: at most 10,000 entries; load median under 50 ms and, per query, search median
/// under 10 ms and the p95 of every search sample of a catalog under 10 ms, at the real count and at 10,000. A
/// <see cref="Calibration"/> workload, which does not run the code under test, is timed just before and just after each
/// attempt at those numbers. An attempt over budget whose slower calibration is within <see cref="BusyFactor"/> times
/// the fastest calibration in this process ran on a quiet machine, and its gates fail at once. An attempt over budget
/// on a busy machine is measured again after 10 s, then 30 s, up to 3 attempts (D102), each gate passing on its best;
/// when every attempt was busy the gate fails as "runner contended, inconclusive, re-run". Each check line lists every
/// attempt's value, and each attempt prints its calibration, wall time and CPU time; a failing check also gives, per
/// attempt, its margin over the budget, wall and CPU time and calibration. The timing budgets are defined for
/// a Release build on Apple Silicon (D69), and Windows hardware is reported, not gated (contracts §8 CAT-16), so the
/// timing gates run only in an optimized build on macOS arm64 and report SKIP with the reason elsewhere; the numbers
/// are printed everywhere, and the entry count is gated everywhere. Index build, filter lists, allocation per search and
/// UI-thread time are printed, not gated. Every printed measurement starts with <c>CAT-16</c>.</para>
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
    private const int WarmUpRounds = 3;
    private const int WarmUpLoads = 20;
    private static readonly TimeSpan WarmUpPause = TimeSpan.FromMilliseconds(250);
    private const int MaxAttempts = 3;
    private const double BusyFactor = 1.2;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
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

        var realPath = Path.Combine(AppContext.BaseDirectory, CatalogProvider.FileName);
        Check("CAT-16 the real app-catalog.json is next to the test binary", File.Exists(realPath));

        // ─── the cold load, and the two catalogs ───
        var cold = await LoadAsync(realPath);
        Check("CAT-16 the real catalog loads", cold.Result.State == CatalogLoadState.Loaded);
        var real = cold.Result.Catalog;
        Console.WriteLine($"CAT-16 real catalog: {real.Entries.Count} stations, {new FileInfo(realPath).Length / 1024.0 / 1024.0:F2} MiB");
        Check($"CAT-16 entry count {real.Entries.Count} ≤ {CatalogProvider.MaxEntries} (D69)", real.Entries.Count <= CatalogProvider.MaxEntries);
        Console.WriteLine($"CAT-16 cold first load (first in this process; reported, not gated) {cold.WallMs:F1} ms wall, catalog.loaded {cold.ReportedMs} ms " +
                          $"({(cold.WallMs < LoadBudgetMs ? "within" : "over")} the {LoadBudgetMs:0} ms budget)");
        // Before D102 the gated number was the median of 7 loads after one warm-up (the cold load; for the synthetic file,
        // its first load), measured here: printed for continuity with earlier evidence, not gated.
        const string legacy = "one warm-up, as gated before D102; reported, not gated";
        PrintLoads($"real {real.Entries.Count}, {legacy}", await LoadRunsAsync(realPath));
        using var temp = new TempDirectory("catalog-perf");
        var syntheticPath = temp.Combine(CatalogProvider.FileName);
        WriteSynthetic(realPath, syntheticPath);
        var syntheticFirst = await LoadAsync(syntheticPath);
        var synthetic = syntheticFirst.Result.Catalog;
        Check($"CAT-16 the synthetic catalog built from the real one loads {SyntheticCount} stations",
            syntheticFirst.Result.State == CatalogLoadState.Loaded && synthetic.Entries.Count == SyntheticCount);
        PrintLoads($"synthetic {SyntheticCount}, {legacy}", await LoadRunsAsync(syntheticPath));
        var subject = new Subject(realPath, syntheticPath, real, synthetic, Queries(real.Entries));
        var calibration = new Calibration(File.ReadAllBytes(realPath));

        // ─── the JIT's steady state (D102), then attempt 1 ───
        await WarmUpAsync(subject, calibration);
        var first = await MeasureAttemptAsync(subject, calibration, 1);

        // ─── index build and filter lists (inside the load, and on the thread pool before the dialog is usable) ───
        var realLists = IndexAndLists(real);
        IndexAndLists(synthetic);
        Console.WriteLine($"CAT-16 load median + filter lists, real {real.Entries.Count}: {first.RealLoadMs + realLists:F1} ms " +
                          $"(the time from GetCatalogAsync to a searchable, filterable dialog, UI thread excluded)");
        PrintAllocations(real.Entries.Count, first.RealSearch, first.SyntheticSearch);

        // ─── the gates (D69; retried only on a busy machine, D102) ───
        if (ungated is null)
            await GateAsync(subject, calibration, first);
        else
        {
            Skip("CAT-16 load median < 50 ms (real and synthetic 10,000)", ungated);
            Skip("CAT-16 search median and p95 < 10 ms (real and synthetic 10,000)", ungated);
        }

        // ─── UI thread ───
        await Headless.RunAsync(() => UiThreadAsync(cold.Result));
    }

    // ─── the measurement (D69, D102) ───

    /// <summary>What is measured: the two catalog files, their indexes and the query set.</summary>
    private sealed record Subject(string RealPath, string SyntheticPath, StationCatalogIndex Real, StationCatalogIndex Synthetic, List<Query> Queries);

    /// <summary>One attempt at the gated numbers: its wall time and the CPU time of every thread meanwhile, the
    /// calibration just before and just after it, and whether the machine was busy then (the slower calibration over
    /// <see cref="BusyFactor"/> times <see cref="FastestCalibrationMs"/>, the fastest calibration in this process when the
    /// attempt ended).</summary>
    private sealed record Attempt(int Number, List<LoadSample> RealLoads, List<LoadSample> SyntheticLoads,
        List<SearchTiming> RealSearch, List<SearchTiming> SyntheticSearch, double WallMs, double CpuMs,
        double CalibrationBeforeMs, double CalibrationAfterMs, double FastestCalibrationMs)
    {
        public double RealLoadMs => Median(RealLoads.Select(l => l.WallMs));
        public double CalibrationMs => Math.Max(CalibrationBeforeMs, CalibrationAfterMs);
        public bool Busy => CalibrationMs > BusyFactor * FastestCalibrationMs;
    }

    /// <summary>The machine's speed now, from a fixed workload that runs none of the code under test: one
    /// <see cref="Utf8JsonReader"/> pass over the real catalog's bytes, held in memory, materializing every property name
    /// and string (CPU, allocation and memory traffic like a load's, no file I/O). <see cref="Measure"/> runs
    /// <see cref="SpinUpPasses"/> untimed passes first, so a CPU that clocked down while idle (after the warm-up's pause or
    /// a retry's wait: without them the first calibration measured up to 1.5× slower on a quiet Mac) is back up to speed,
    /// then takes the median of 5 timed passes. The fastest median in this process stands for the quiet machine.</summary>
    private sealed class Calibration(byte[] json)
    {
        private const int SpinUpPasses = 10;
        private const int Passes = 5;
        private long sink;

        public double FastestMs { get; private set; } = double.PositiveInfinity;

        public void Pass()
        {
            var reader = new Utf8JsonReader(json);
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) sink += reader.GetString()!.Length;
        }

        public double Measure()
        {
            for (var i = 0; i < SpinUpPasses; i++) Pass();
            var samples = new double[Passes];
            for (var i = 0; i < Passes; i++)
            {
                var start = Stopwatch.GetTimestamp();
                Pass();
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            var median = Median(samples);
            FastestMs = Math.Min(FastestMs, median);
            return median;
        }
    }

    /// <summary>Brings the loads, the searches and the calibration to the tiered JIT's steady state (D102): each round
    /// runs them repeatedly (method call counts pass the JIT's promotion thresholds), then times one calibration (so the
    /// fastest calibration in this process includes the seconds before attempt 1, not only the attempt itself), then
    /// pauses so the background compilation it queued lands before the next round. A load here is untimed and skips the
    /// collection. Measured on an
    /// Apple M4 Max (D102), three attempts in a row: without this warm-up a standalone run's attempt 1 measured the real
    /// load at 26–27 ms and attempts 2 and 3 at 14–15 ms; 1 round of 10 still let a later attempt's synthetic median jump
    /// to 27–28 ms; from 2 rounds of 20 on, attempts 1, 2 and 3 agreed within noise, standalone and in the full run. 3
    /// rounds of 20 keep a margin, about 4 s here.</summary>
    private static async Task WarmUpAsync(Subject subject, Calibration calibration)
    {
        var start = Stopwatch.GetTimestamp();
        var calibrations = new List<double>();
        for (var round = 0; round < WarmUpRounds; round++)
        {
            for (var i = 0; i < WarmUpLoads; i++)
            {
                foreach (var path in new[] { subject.RealPath, subject.SyntheticPath })
                    await new CatalogProvider(new CatalogLocation(path, CatalogLocationSource.Override, null), new RecordingAppLog()).GetCatalogAsync();
                calibration.Pass();
            }
            foreach (var index in new[] { subject.Real, subject.Synthetic })
                foreach (var query in subject.Queries)
                    StationCatalogQuery.Search(index, query.Text, query.Filters);
            calibrations.Add(calibration.Measure());
            await Task.Delay(WarmUpPause);
        }
        Console.WriteLine($"CAT-16 warm-up to the JIT's steady state (D102; not gated): {WarmUpRounds} rounds of {WarmUpLoads} loads of each file, " +
                          $"a search per query on each catalog, {WarmUpLoads} calibration passes and a calibration, then a {WarmUpPause.TotalMilliseconds:0} ms pause; " +
                          $"{Stopwatch.GetElapsedTime(start).TotalSeconds:F1} s; calibrations " +
                          $"{string.Join(", ", calibrations.Select(c => c.ToString("F2", CultureInfo.InvariantCulture)))} ms");
    }

    /// <summary>The gated numbers once: the median of <see cref="Runs"/> loads of each file, then every query's search
    /// timings on each catalog, between two calibrations. Prints the attempt's lines, its wall time, and the CPU time of
    /// every thread of this process meanwhile (a machine that takes the CPU away shows wall time well above CPU time).</summary>
    private static async Task<Attempt> MeasureAttemptAsync(Subject subject, Calibration calibration, int number)
    {
        var suffix = number == 1 ? "" : $", attempt {number}";
        var before = calibration.Measure();
        var cpu = Environment.CpuUsage.TotalTime;
        var start = Stopwatch.GetTimestamp();
        var realLoads = await LoadRunsAsync(subject.RealPath);
        PrintLoads($"real {subject.Real.Entries.Count}{suffix}", realLoads);
        var syntheticLoads = await LoadRunsAsync(subject.SyntheticPath);
        PrintLoads($"synthetic {SyntheticCount}{suffix}", syntheticLoads);
        var realSearch = SearchSet($"real {subject.Real.Entries.Count}{suffix}", subject.Real, subject.Queries);
        var syntheticSearch = SearchSet($"synthetic {SyntheticCount}{suffix}", subject.Synthetic, subject.Queries);
        var wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var cpuMs = (Environment.CpuUsage.TotalTime - cpu).TotalMilliseconds;
        var after = calibration.Measure();
        var attempt = new Attempt(number, realLoads, syntheticLoads, realSearch, syntheticSearch, wallMs, cpuMs, before, after, calibration.FastestMs);
        Console.WriteLine($"CAT-16 attempt {number}: {wallMs:F0} ms wall, {cpuMs:F0} ms CPU (all threads); calibration {before:F2} ms before, " +
                          $"{after:F2} ms after, fastest in this process {attempt.FastestCalibrationMs:F2} ms: " +
                          $"{(attempt.Busy ? "busy" : "quiet")} (busy above {BusyFactor:0.0}×)");
        return attempt;
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

    /// <summary>One timing gate: its value in an attempt, its budget, the value's format, and the check name for the
    /// attempt given.</summary>
    private sealed record Gate(Func<Attempt, double> Value, double BudgetMs, string Format, Func<Attempt, string> Title);

    /// <summary>D69's timing budgets. Attempt 1 decides unless it misses a budget on a busy machine: over budget on a
    /// quiet machine (its slower calibration within <see cref="BusyFactor"/> times the fastest in this process) is a real
    /// result and fails at once; over budget on a busy machine earns another attempt after the next of
    /// <see cref="RetryDelays"/>, up to <see cref="MaxAttempts"/>. A gate passes on its best attempt, since noise only
    /// adds time. When the last attempt is still busy and over budget, the gate fails as inconclusive. All attempts run
    /// here, at the steady state <see cref="WarmUpAsync"/> reached, so a repeat measures the same code as attempt 1.</summary>
    private static async Task GateAsync(Subject subject, Calibration calibration, Attempt first)
    {
        Gate Load(string label, Func<Attempt, List<LoadSample>> loads) =>
            new(a => Median(loads(a).Select(l => l.WallMs)), LoadBudgetMs, "F1",
                a => $"CAT-16 load median ({label}) {Median(loads(a).Select(l => l.WallMs)):F1} ms < {LoadBudgetMs:0} ms");
        Gate[] Search(string label, Func<Attempt, List<SearchTiming>> set) =>
        [
            new(a => set(a).Max(s => s.MedianMs), SearchBudgetMs, "F3", a =>
            {
                var slowest = set(a).MaxBy(s => s.MedianMs)!;
                return $"CAT-16 search median < {SearchBudgetMs:0} ms for each of {set(a).Count} queries ({label}; slowest {slowest.Name} {slowest.MedianMs:F3} ms)";
            }),
            new(a => Percentile(set(a).SelectMany(s => s.Samples), 0.95), SearchBudgetMs, "F3", a =>
                $"CAT-16 search p95 over {set(a).Count * Runs} samples ({label}) {Percentile(set(a).SelectMany(s => s.Samples), 0.95):F3} ms < {SearchBudgetMs:0} ms"),
        ];
        var real = $"real {subject.Real.Entries.Count}";
        var synthetic = $"synthetic {SyntheticCount}";
        Gate[] gates =
        [
            Load(real, a => a.RealLoads), Load(synthetic, a => a.SyntheticLoads),
            .. Search(real, a => a.RealSearch), .. Search(synthetic, a => a.SyntheticSearch),
        ];

        var attempts = new List<Attempt> { first };
        bool Met(Gate gate) => attempts.Any(a => gate.Value(a) < gate.BudgetMs);
        string? verdict = null;
        while (!gates.All(Met))
        {
            var last = attempts[^1];
            if (!last.Busy)
            {
                verdict = $"over budget on a quiet machine in attempt {last.Number} (calibration {last.CalibrationMs:F2} ms, within {BusyFactor:0.0}× " +
                          $"the fastest {last.FastestCalibrationMs:F2} ms): a result, not retried";
                break;
            }
            if (last.Number == MaxAttempts)
            {
                verdict = $"runner contended in all {MaxAttempts} attempts (calibration " +
                          $"{string.Join(", ", attempts.Select(a => a.CalibrationMs.ToString("F2", CultureInfo.InvariantCulture)))} ms against the fastest " +
                          $"{last.FastestCalibrationMs:F2} ms): inconclusive, re-run";
                break;
            }
            var delay = RetryDelays[last.Number - 1];
            var missed = string.Join("; ", gates.Where(g => !Met(g)).Select(g => g.Title(attempts.MinBy(g.Value)!)));
            Console.WriteLine($"CAT-16 attempt {last.Number + 1} of up to {MaxAttempts} in {delay.TotalSeconds:0} s (D102): attempt {last.Number} ran on a busy " +
                              $"machine (calibration {last.CalibrationMs:F2} ms, over {BusyFactor:0.0}× the fastest {last.FastestCalibrationMs:F2} ms); " +
                              $"over budget so far: {missed}");
            await Task.Delay(delay);
            attempts.Add(await MeasureAttemptAsync(subject, calibration, last.Number + 1));
        }
        foreach (var gate in gates)
        {
            var best = attempts.MinBy(gate.Value)!;
            var met = gate.Value(best) < gate.BudgetMs;
            var values = string.Join(", ", attempts.Select(a => gate.Value(a).ToString(gate.Format, CultureInfo.InvariantCulture) + (a.Busy ? " ms busy" : " ms")));
            // A failing gate also names, per attempt, how far over budget it was and the attempt's wall and CPU time.
            var detail = met ? "" : "; " + verdict + "; " + string.Join("; ", attempts.Select(a =>
                $"attempt {a.Number}: +{(gate.Value(a) - gate.BudgetMs).ToString(gate.Format, CultureInfo.InvariantCulture)} ms over budget, " +
                $"{a.WallMs:F0} ms wall, {a.CpuMs:F0} ms CPU, calibration {a.CalibrationMs:F2} ms {(a.Busy ? "busy" : "quiet")}"));
            Check($"{gate.Title(best)} (attempts: {values}; D102{detail})", met);
        }
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
