using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DialShift.Core.Playback;

namespace DialShift.App.Smoke;

/// <summary>One recorded smoke check. <see cref="Skipped"/> checks count as passed; their detail says why they did not run.</summary>
public sealed record SmokeCheck(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("skipped"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Skipped = false);

/// <summary>
/// The <c>results.json</c> of a smoke run. Thread-safe: the checks record on the UI thread, the watchdog on the thread
/// pool. The file is rewritten after every check, so a run that dies half-way still leaves the checks it finished.
/// After <see cref="Complete"/> nothing more is recorded (a check still running when the watchdog fired can't add to it).
/// </summary>
public sealed class SmokeResults
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object gate = new();
    private readonly List<SmokeCheck> checks = [];
    private readonly IAppLog log;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly string engine;
    private readonly string dataDirectory;

    public SmokeResults(string outputDirectory, string engine, string dataDirectory, IAppLog log)
    {
        OutputDirectory = outputDirectory;
        this.engine = engine;
        this.dataDirectory = dataDirectory;
        this.log = log;
        Directory.CreateDirectory(outputDirectory);
    }

    public string OutputDirectory { get; }

    public string ResultsFile => Path.Combine(OutputDirectory, "results.json");

    public bool IsComplete { get; private set; }

    /// <summary>True when at least one check ran and none failed.</summary>
    public bool AllPassed
    {
        get
        {
            lock (gate) return checks.Count > 0 && checks.All(c => c.Passed);
        }
    }

    public int ExitCode => AllPassed ? ExitCodes.Success : ExitCodes.SmokeTestFailed;

    public void Record(string name, bool passed, string detail) => Add(new SmokeCheck(name, passed, detail));

    public void Skip(string name, string reason) => Add(new SmokeCheck(name, true, "skipped: " + reason, Skipped: true));

    /// <summary>Records the last result and seals the file. Idempotent; only the first call writes.</summary>
    public void Complete(SmokeCheck? last = null)
    {
        lock (gate)
        {
            if (IsComplete) return;
            if (last != null) AddLocked(last);
            IsComplete = true;
            WriteLocked();
        }
        log.Info("smoke.complete", $"passed={AllPassed} results={ResultsFile}");
    }

    private void Add(SmokeCheck check)
    {
        lock (gate)
        {
            if (IsComplete) return;
            AddLocked(check);
            WriteLocked();
        }
    }

    private void AddLocked(SmokeCheck check)
    {
        checks.Add(check);
        var outcome = check.Skipped ? "skipped" : check.Passed ? "passed" : "FAILED";
        log.Info("smoke.check", $"{outcome}: {check.Name}: {check.Detail}");
    }

    private void WriteLocked()
    {
        var document = new
        {
            passed = checks.Count > 0 && checks.All(c => c.Passed),
            complete = IsComplete,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            engine,
            dataDirectory,
            startedUtc = started,
            durationSeconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            checks
        };
        try { File.WriteAllText(ResultsFile, JsonSerializer.Serialize(document, JsonOptions)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("smoke.results_write_failed", $"Couldn't write {ResultsFile}.", ex);
        }
    }
}
