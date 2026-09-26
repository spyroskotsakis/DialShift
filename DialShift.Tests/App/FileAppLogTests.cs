using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DialShift.App;
using DialShift.App.Services;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

/// <summary>
/// <see cref="FileAppLog"/> and <see cref="StreamUrlRedactor"/> (acceptance matrix §7.8 CT-LOG-01..04, §8.2.7, HS-10
/// startup fields). Every log file lives in its own temp directory. The cross-process checks run two child processes of
/// this test executable (<c>--log-child</c>) against one file.
/// </summary>
public static class FileAppLogTests
{
    private const string SecretUrl = "https://user:pass@radio.example.com:8443/live/stream.mp3?token=abc#x";
    private static readonly string[] SecretParts = ["user", "pass", "token=abc", "/live/stream.mp3", "#x"];

    public static async Task RunAsync()
    {
        UrlRedaction();
        TextRedaction();
        HomeTilde();
        using var temp = new TempDirectory("log");
        JsonLines(temp.Combine("shape"));
        RedactedOnDisk(temp.Combine("redacted"));
        RotationBoundary(temp.Combine("rotate-boundary"));
        RotationReplacesOld(temp.Combine("rotate-replace"));
        RotationUnderLoad(temp.Combine("rotate-load"));
        ConcurrentWrites(temp.Combine("concurrent"));
        TwoInstancesShareFile(temp.Combine("two-instances"));
        WaitsForBusyHolder(temp.Combine("busy-holder"));
        GivesUpOnStuckHolder(temp.Combine("stuck-holder"));
        GivesUpAfterWaitLimit(temp.Combine("endless-holder"));
        await TwoProcessesShareFileAsync(temp.Combine("two-processes"));
        await TwoProcessesRotateAsync(temp.Combine("two-processes-rotate"));
        await ManyProcessesRotateAsync(temp.Combine("many-processes-rotate"));
        NeverThrows(temp);
        Startup(temp.Combine("startup"));
    }

    // ---- CT-LOG-01 -------------------------------------------------------------------------------------------

    private static void UrlRedaction()
    {
        var redacted = StreamUrlRedactor.RedactUrl(new Uri(SecretUrl));
        Check("CT-LOG-01 user-info, path, query and fragment are dropped: https://radio.example.com:8443/…", redacted == "https://radio.example.com:8443/…");
        Check("CT-LOG-01 ... none of user, pass, token=abc, /live/stream.mp3 remain", !SecretParts.Any(p => redacted.Contains(p, StringComparison.Ordinal)));
        Check("CT-LOG-01 the scheme's default port is omitted", StreamUrlRedactor.RedactUrl(new Uri("https://radio.example.com:443/a?b")) == "https://radio.example.com/…");
        Check("CT-LOG-01 a non-default http port is kept", StreamUrlRedactor.RedactUrl(new Uri("http://10.0.0.5:8000/stream")) == "http://10.0.0.5:8000/…");
        Check("CT-LOG-01 a relative URI is replaced entirely", StreamUrlRedactor.RedactUrl(new Uri("/live?token=abc", UriKind.Relative)) == "[relative-url]");
    }

    // ---- CT-LOG-02 -------------------------------------------------------------------------------------------

    private static void TextRedaction()
    {
        var text = $"Couldn't open {SecretUrl}; fallback http://u:p@backup.example.org/b?k=1 failed (see \"https://x.example/p?q\")";
        var redacted = StreamUrlRedactor.RedactText(text);
        // The URL run stops at whitespace, quotes or angle brackets; closing punctuation at its end (the ';') goes back to the text.
        Check("CT-LOG-02 every URL in free text is redacted",
            redacted == "Couldn't open https://radio.example.com:8443/…; fallback http://backup.example.org/… failed (see \"https://x.example/…\")");
        Check("CT-LOG-02 ... and no secret survives", !SecretParts.Concat(["u:p@", "k=1", "p?q"]).Any(p => redacted.Contains(p, StringComparison.Ordinal)));
        Check("CT-LOG-02 surrounding text is kept", redacted.StartsWith("Couldn't open https://radio.example.com:8443/…", StringComparison.Ordinal));
        Check("CT-LOG-02 non-http schemes are redacted too (icy://, rtsp://)",
            StreamUrlRedactor.RedactText("icy://a:b@h.example/s?t=1 rtsp://h2.example:554/x") == "icy://h.example/… rtsp://h2.example:554/…");
        Check("CT-LOG-02 an unparseable URL is replaced, not passed through",
            StreamUrlRedactor.RedactText("bad http://[::1/secret?token=abc end") == "bad http://[redacted]/… end");
        Check("CT-LOG-02 null and empty text are safe", StreamUrlRedactor.RedactText(null) == "" && StreamUrlRedactor.RedactText("") == "");
        Check("CT-LOG-02 text without URLs is unchanged (station names allowed)", StreamUrlRedactor.RedactText("Groove Salad · 128k") == "Groove Salad · 128k");
    }

    // ---- §8.2.7 rule 3 ---------------------------------------------------------------------------------------

    private static void HomeTilde()
    {
        var home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (home.Length <= 3)
        {
            Skip("§8.2.7 the home prefix is replaced by ~", $"home directory '{home}' is a root, which the redactor deliberately leaves alone");
            return;
        }
        var file = Path.Combine(home, "Library", "Application Support", "DialShift", "settings.json");
        var redacted = StreamUrlRedactor.RedactText("Couldn't save " + file);
        Check("§8.2.7 the home prefix is replaced by ~", redacted == "Couldn't save ~" + file[home.Length..]);
        Check("§8.2.7 ... every occurrence", StreamUrlRedactor.RedactText($"{home} and {home}") == "~ and ~");
        Check("§8.2.7 ... only at a path boundary (a sibling like <home>by/x is left alone)", StreamUrlRedactor.RedactText(home + "by/x") == home + "by/x");
        if (OperatingSystem.IsWindows())
            Check("§8.2.7 Windows: the home prefix matches case-insensitively", StreamUrlRedactor.RedactText(home.ToUpperInvariant() + "\\x") == "~\\x");
        else
            Check("§8.2.7 Unix: a differently-cased home prefix is not a match", StreamUrlRedactor.RedactText(home.ToUpperInvariant() + "/x") == home.ToUpperInvariant() + "/x");
    }

    // ---- CT-LOG-03 -------------------------------------------------------------------------------------------

    private static void JsonLines(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        log.Info("app.start", "hello \"quoted\" and \\ backslash and ünïcödé · Groove Salad");
        log.Warn("playback.failed", "no exception");
        log.Warn("playback.failed", "with exception", new IOException("disk full"));
        log.Error("app.startup_failed", "boom", new InvalidOperationException("outer", new TimeoutException("inner")));

        var bytes = File.ReadAllBytes(log.LogFile);
        Check("CT-LOG-03 the log is created in a missing directory", bytes.Length > 0);
        Check("CT-LOG-03 UTF-8 without BOM", !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        Check("CT-LOG-03 every line ends with \\n and there are 4 lines", text.EndsWith('\n') && text.Count(c => c == '\n') == 4 && !text.Contains('\r'));

        var lines = text.TrimEnd('\n').Split('\n').Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Check("CT-LOG-03 each line is one JSON object", lines.All(l => l.ValueKind == JsonValueKind.Object));
        Check("CT-LOG-03 ts, level, event, msg are strings on every line",
            lines.All(l => new[] { "ts", "level", "event", "msg" }.All(p => l.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String)));
        Check("CT-LOG-03 ts is an ISO-8601 round-trip timestamp in UTC",
            lines.All(l => DateTimeOffset.TryParseExact(l.GetProperty("ts").GetString(), "O", null, System.Globalization.DateTimeStyles.None, out var ts) &&
                           ts.Offset == TimeSpan.Zero && Math.Abs((DateTimeOffset.UtcNow - ts).TotalMinutes) < 5));
        Check("CT-LOG-03 levels are info, warn, warn, error", lines.Select(l => l.GetProperty("level").GetString()).SequenceEqual(["info", "warn", "warn", "error"]));
        Check("CT-LOG-03 events are written verbatim", lines.Select(l => l.GetProperty("event").GetString()).SequenceEqual(["app.start", "playback.failed", "playback.failed", "app.startup_failed"]));
        Check("CT-LOG-03 msg round-trips quotes, backslashes and non-ASCII",
            lines[0].GetProperty("msg").GetString() == "hello \"quoted\" and \\ backslash and ünïcödé · Groove Salad");
        Check("CT-LOG-03 non-ASCII stays readable in the file (relaxed escaping)", text.Contains("ünïcödé · Groove Salad", StringComparison.Ordinal));
        Check("CT-LOG-03 ex is present only when an exception is given",
            !lines[0].TryGetProperty("ex", out _) && !lines[1].TryGetProperty("ex", out _) && lines[2].TryGetProperty("ex", out _) && lines[3].TryGetProperty("ex", out _));
        Check("CT-LOG-03 no other properties", lines.All(l => l.EnumerateObject().All(p => p.Name is "ts" or "level" or "event" or "msg" or "ex")));
        var ex = lines[3].GetProperty("ex").GetString()!;
        Check("CT-LOG-03 ex carries type, message and the inner exception",
            ex.Contains("System.InvalidOperationException: outer", StringComparison.Ordinal) && ex.Contains("System.TimeoutException: inner", StringComparison.Ordinal));
        Check("CT-LOG-03 blank log path is rejected", Throws<ArgumentException>(() => new FileAppLog(" ")));
        Check("CT-LOG-03 LogFile is absolute", Path.IsPathFullyQualified(log.LogFile));
    }

    private static void RedactedOnDisk(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        log.Info("playback.state", $"Connecting to {SecretUrl}");
        log.Warn("playback.failed", "Stream failed", new HttpRequestException($"Response status code does not indicate success: 403 ({SecretUrl})"));
        var text = File.ReadAllText(log.LogFile);
        Check("HS-10 / CT-LOG-02 the file never contains credentials, token, path or fragment", !SecretParts.Any(p => text.Contains(p, StringComparison.Ordinal)));
        Check("HS-10 / CT-LOG-02 ... in msg and ex alike", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.Contains("radio.example.com:8443/…", StringComparison.Ordinal)));
    }

    // ---- CT-LOG-04 -------------------------------------------------------------------------------------------

    private static void RotationBoundary(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        var rotated = log.LogFile + ".1";
        log.Info("x.probe", "same length");
        var lineLength = new FileInfo(log.LogFile).Length;
        File.Delete(log.LogFile);

        File.WriteAllBytes(log.LogFile, Filler(FileAppLog.MaxFileBytes - lineLength));
        log.Info("x.probe", "same length");
        Check("CT-LOG-04 a write that reaches exactly 1 MiB does not rotate",
            new FileInfo(log.LogFile).Length == FileAppLog.MaxFileBytes && !File.Exists(rotated));
        log.Info("x.probe", "same length");
        Check("CT-LOG-04 the next write rotates to dialshift.log.1", File.Exists(rotated) && new FileInfo(rotated).Length == FileAppLog.MaxFileBytes);
        Check("CT-LOG-04 ... and logging continues in a fresh dialshift.log", new FileInfo(log.LogFile).Length == lineLength);
        Check("CT-LOG-04 MaxFileBytes is 1 MiB", FileAppLog.MaxFileBytes == 1024 * 1024);
    }

    private static void RotationReplacesOld(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        var rotated = log.LogFile + ".1";
        Directory.CreateDirectory(directory);
        File.WriteAllText(rotated, "OLD-ROTATED-CONTENT\n");
        File.WriteAllBytes(log.LogFile, Filler(FileAppLog.MaxFileBytes));
        log.Warn("x.after", "first line after rotation");
        Check("CT-LOG-04 rotation replaces the previous dialshift.log.1",
            !File.ReadAllText(rotated).Contains("OLD-ROTATED-CONTENT", StringComparison.Ordinal) && new FileInfo(rotated).Length == FileAppLog.MaxFileBytes);
        var fresh = File.ReadAllLines(log.LogFile);
        Check("CT-LOG-04 the new file holds only the new line", fresh.Length == 1 && fresh[0].Contains("\"x.after\"", StringComparison.Ordinal));
        Check("CT-LOG-04 only dialshift.log and dialshift.log.1 exist", Directory.GetFiles(directory).Length == 2);
    }

    private static void RotationUnderLoad(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        var message = new string('m', 1000);
        var maxSeen = 0L;
        for (var i = 0; i < 2600; i++)
        {
            log.Info("x.load", $"{i:D5} {message}");
            maxSeen = Math.Max(maxSeen, new FileInfo(log.LogFile).Length);
        }
        var rotated = log.LogFile + ".1";
        var all = File.ReadAllLines(rotated).Concat(File.ReadAllLines(log.LogFile)).ToList();
        Check("CT-LOG-04 under sustained load the live file never exceeds 1 MiB", maxSeen <= FileAppLog.MaxFileBytes);
        Check("CT-LOG-04 ... rotated files never exceed 1 MiB", new FileInfo(rotated).Length <= FileAppLog.MaxFileBytes);
        Check("CT-LOG-04 ... every kept line is complete JSON", all.All(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString() == "x.load"));
        var numbers = all.Select(l => int.Parse(JsonDocument.Parse(l).RootElement.GetProperty("msg").GetString()![..5])).ToList();
        Check("CT-LOG-04 ... .1 then the live file hold consecutive lines ending with the newest (none lost at a rotation)",
            numbers[^1] == 2599 && numbers.Zip(numbers.Skip(1)).All(pair => pair.Second == pair.First + 1));
    }

    // ---- §8.2.7 thread safety --------------------------------------------------------------------------------

    private static void ConcurrentWrites(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        const int threads = 8, perThread = 400;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                if (i % 3 == 0) log.Warn("x.concurrent", $"t{t}-i{i}", new InvalidOperationException("e"));
                else log.Info("x.concurrent", $"t{t}-i{i}");
            }
        });
        var lines = File.ReadAllLines(log.LogFile);
        var messages = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("msg").GetString()).ToList();
        Check($"CT-LOG-03 concurrent writers: {threads * perThread} lines, all valid JSON", lines.Length == threads * perThread);
        Check("CT-LOG-03 concurrent writers: every message exactly once", messages.Distinct().Count() == threads * perThread);
    }

    /// <summary>
    /// LOG-D1 regression (fixed in e3ceaa2), in-process half: two instances on one file share the per-file lock, so no
    /// line is lost or torn. <see cref="TwoProcessesShareFileAsync"/> covers the real cross-process case.
    /// </summary>
    private static void TwoInstancesShareFile(string directory)
    {
        var path = Path.Combine(directory, "dialshift.log");
        var first = new FileAppLog(path);
        var second = new FileAppLog(path);
        const int perInstance = 1000;
        Parallel.Invoke(
            () => { for (var i = 0; i < perInstance; i++) first.Info("x.first", $"a{i}"); },
            () => { for (var i = 0; i < perInstance; i++) second.Info("x.second", $"b{i}"); });
        var lines = File.ReadAllLines(path);
        var valid = lines.Count(l => { try { JsonDocument.Parse(l); return true; } catch (JsonException) { return false; } });
        Console.WriteLine($"  two instances on one file: {lines.Length} lines, {valid} valid of {2 * perInstance}");
        Check("LOG-D1 two logger instances in one process on one file: no line lost or torn", lines.Length == 2 * perInstance && valid == 2 * perInstance);
    }

    // ---- LOG-D2 waiting for another process's lock ----------------------------------------------------------

    private const string HolderEvent = "x.holder";

    /// <summary>
    /// LOG-D2 regression, deterministic half: the test takes the cross-process lock the way another process's writer
    /// does (the lock file opened with <c>FileShare.None</c>, which also conflicts within one process), holds it for
    /// longer than <see cref="FileAppLog.LockWaitBudget"/> while appending a line every 20 ms, and logs one line
    /// meanwhile. Before the fix that write gave up after 250 ms and appended in the middle of the holder's lines,
    /// without the lock (every run); now it waits for the busy holder and appends after its last line.
    /// </summary>
    private static void WaitsForBusyHolder(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        var holdFor = 3 * FileAppLog.LockWaitBudget;
        var holder = WriteWhileLockHeld(log, holdFor, appendEvery: TimeSpan.FromMilliseconds(20));
        var events = File.ReadAllLines(log.LogFile).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()).ToList();
        Console.WriteLine($"  busy holder: held {holdFor.TotalMilliseconds:F0} ms and appended {holder.Lines} lines; the waiter returned after {holder.Waited.TotalMilliseconds:F0} ms; events in file order: {string.Join(" ", events.Select(e => e == HolderEvent ? "H" : "W"))}");
        var inOrder = events.Count == holder.Lines + 1 && events.Take(holder.Lines).All(e => e == HolderEvent) && events[^1] == "x.waiter";
        if (!inOrder || holder.Waited >= FileAppLog.LockWaitLimit) Diagnose(HolderGap(holder));
        Check("LOG-D2 a writer keeps waiting while the lock's holder is still writing (its line comes after all of the holder's, none overwritten)", inOrder);
        Check("LOG-D2 ... and it did not wait longer than LockWaitLimit", holder.Waited < FileAppLog.LockWaitLimit);
    }

    /// <summary>A holder that does not write (suspended in a debugger) is waited out after <see cref="FileAppLog.LockWaitBudget"/>.</summary>
    private static void GivesUpOnStuckHolder(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        var holdFor = 4 * FileAppLog.LockWaitBudget;
        var waited = WriteWhileLockHeld(log, holdFor, appendEvery: null).Waited;
        Console.WriteLine($"  stuck holder: held {holdFor.TotalMilliseconds:F0} ms without writing; the waiter returned after {waited.TotalMilliseconds:F0} ms");
        Check("LOG-D2 a writer gives up on a holder that makes no progress after LockWaitBudget (250 ms), before the holder lets go",
            waited >= FileAppLog.LockWaitBudget && waited < holdFor);
        Check("LOG-D2 ... and still writes its line", File.ReadAllLines(log.LogFile) is [var only] && only.Contains("\"x.waiter\"", StringComparison.Ordinal));
    }

    /// <summary>Even a holder that keeps writing is waited out after <see cref="FileAppLog.LockWaitLimit"/> (2 s) in total.</summary>
    private static void GivesUpAfterWaitLimit(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        var holdFor = FileAppLog.LockWaitLimit + 3 * FileAppLog.LockWaitBudget;
        var holder = WriteWhileLockHeld(log, holdFor, appendEvery: TimeSpan.FromMilliseconds(20));
        Console.WriteLine($"  endless holder: held {holdFor.TotalMilliseconds:F0} ms while writing; the waiter returned after {holder.Waited.TotalMilliseconds:F0} ms");
        // Its line may race the holder's unlocked appends here, so only the timing is checked.
        var bounded = holder.Waited >= FileAppLog.LockWaitLimit && holder.Waited < holdFor;
        if (!bounded) Diagnose(HolderGap(holder));
        Check("LOG-D2 a writer waits at most LockWaitLimit (2 s) in total, even while the holder keeps writing", bounded);
    }

    /// <summary>What <see cref="WriteWhileLockHeld"/> observed: the waiter's write time, the holder's appends and its longest pause between two of them.</summary>
    private sealed record HolderRun(TimeSpan Waited, int Lines, TimeSpan LongestGap);

    /// <summary>
    /// Why a busy-holder check can fail without a regression: a scheduler or GC stall that paused the holder for
    /// <see cref="FileAppLog.LockWaitBudget"/> or more makes it look stuck, and then giving up is correct.
    /// </summary>
    private static string HolderGap(HolderRun holder) =>
        $"the holder's longest gap between appends was {holder.LongestGap.TotalMilliseconds:F0} ms " +
        (holder.LongestGap >= FileAppLog.LockWaitBudget
            ? $"(at least LockWaitBudget, {FileAppLog.LockWaitBudget.TotalMilliseconds:F0} ms: the holder itself stalled, so the waiter rightly took it for stuck)"
            : $"(under LockWaitBudget, {FileAppLog.LockWaitBudget.TotalMilliseconds:F0} ms: the holder kept writing, so this is a regression)");

    /// <summary>
    /// Holds <paramref name="log"/>'s cross-process lock on a dedicated thread (not the thread pool, so its appends do
    /// not queue behind other work) for <paramref name="holdFor"/>, appending a <see cref="HolderEvent"/> line every
    /// <paramref name="appendEvery"/> (never when null), and meanwhile logs one <c>x.waiter</c> line.
    /// </summary>
    private static HolderRun WriteWhileLockHeld(FileAppLog log, TimeSpan holdFor, TimeSpan? appendEvery)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log.LogFile)!);
        using var held = new ManualResetEventSlim();
        var lines = 0;
        var longestGap = TimeSpan.Zero;
        Exception? failure = null;
        var holder = new Thread(() =>
        {
            try
            {
                using var lockFile = new FileStream(log.LockFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
                held.Set();
                var clock = Stopwatch.StartNew();
                var lastAppend = clock.Elapsed;
                while (clock.Elapsed < holdFor)
                {
                    if (appendEvery is { } every)
                    {
                        File.AppendAllText(log.LogFile, $"{{\"event\":\"{HolderEvent}\",\"msg\":\"{lines++}\"}}\n");
                        var now = clock.Elapsed;
                        if (lines > 1 && now - lastAppend > longestGap) longestGap = now - lastAppend;
                        lastAppend = now;
                        Thread.Sleep(every);
                    }
                    else Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                held.Set();
            }
        }) { IsBackground = true, Name = "log-lock-holder" };
        holder.Start();
        if (!held.Wait(TimeSpan.FromSeconds(10)) || failure is not null)
            throw new CheckFailedException($"precondition: the test couldn't take the log's lock file ({failure?.Message ?? "timed out"})");
        var wait = Stopwatch.StartNew();
        log.Info("x.waiter", "logged while another writer held the lock");
        var waited = wait.Elapsed;
        holder.Join();
        if (failure is not null) throw new CheckFailedException($"precondition: the lock holder failed: {failure.Message}");
        return new HolderRun(waited, lines, longestGap);
    }

    // ---- LOG-D1 / §8.2.7 cross-process -----------------------------------------------------------------------

    private const string ChildEvent = "x.child";

    /// <summary>
    /// LOG-D1 regression: the primary and a second-instance process log to the same file at once. Two child processes
    /// each append <c>N</c> lines, released together; below the rotation threshold every line must be kept, whole, and
    /// in each writer's order.
    /// </summary>
    private static async Task TwoProcessesShareFileAsync(string directory)
    {
        const int perChild = 2000; // ~100 bytes a line: ~400 KB in total, well below one rotation
        var path = Path.Combine(directory, "dialshift.log");
        var children = await RunLogChildrenAsync(path, perChild);
        Check("LOG-D1 two processes: both --log-child writers exit 0", children.All(c => c.ExitCode == 0));
        Check("LOG-D1 two processes: precondition: the file did not rotate", !File.Exists(path + ".1"));

        var lines = ReadChildLines(path);
        var switches = lines.Zip(lines.Skip(1)).Count(pair => pair.First.Child?.Pid != pair.Second.Child?.Pid);
        Console.WriteLine($"  two processes: {lines.Count} lines of {2 * perChild}, {lines.Count(l => l.Child is null)} invalid, writer switched {switches} times");
        Check($"LOG-D1 two processes: all {2 * perChild} lines are present and each is one valid JSON object",
            Diagnose(FirstInvalidLine(lines)) is null && lines.Count == 2 * perChild);
        Check("LOG-D1 two processes: the writes really interleaved (the scenario exercised contention)", switches > 1);
        Check("LOG-D1 two processes: each process's lines are all there, in its own order (0..N-1)",
            EachInOrder(lines, children, firstSequence: 0, newestSequence: perChild - 1));
    }

    /// <summary>
    /// LOG-D1 regression, rotation half: two processes writing past the threshold several times over rotate under the
    /// cross-process lock, so neither file ever holds more than 1 MiB and the kept lines are the newest of each writer,
    /// consecutive and whole. The oldest lines are dropped by design (only one rotated file is kept).
    /// </summary>
    private static async Task TwoProcessesRotateAsync(string directory)
    {
        const int perChild = 15_000; // ~2.6 MB in total: at least two rotations
        var path = Path.Combine(directory, "dialshift.log");
        var rotated = path + ".1";
        var children = await RunLogChildrenAsync(path, perChild);
        Check("LOG-D1 rotation: both --log-child writers exit 0", children.All(c => c.ExitCode == 0));

        var live = new FileInfo(path).Length;
        var old = File.Exists(rotated) ? new FileInfo(rotated).Length : -1;
        var lines = ReadChildLines(path);
        Console.WriteLine($"  rotation: dialshift.log {live} bytes, dialshift.log.1 {old} bytes, {lines.Count} lines kept of {2 * perChild}");
        Check("LOG-D1 rotation: precondition: the file rotated at least twice (older lines were dropped)", old > 0 && lines.Count < 2 * perChild);
        Check("LOG-D1 rotation: dialshift.log is at most 1 MiB", live <= FileAppLog.MaxFileBytes);
        Check("LOG-D1 rotation: dialshift.log.1 is at most 1 MiB", old <= FileAppLog.MaxFileBytes);
        Check("LOG-D1 rotation: every kept line is one valid JSON object", Diagnose(FirstInvalidLine(lines)) is null);
        Check("LOG-D1 rotation: each process's kept lines (.1, then the live file) are consecutive and end with its newest",
            EachInOrder(lines, children, firstSequence: null, newestSequence: perChild - 1));
    }

    /// <summary>
    /// LOG-D2 regression, many writers: more contenders than the lock's polling serves fairly, so a waiter often loses
    /// the lock to the others for long stretches. Before the fix it gave up on those healthy holders after 250 ms and
    /// appended without the lock, overwriting another writer's line. Each round writes ~1.5 MB, so the file rotates
    /// exactly once and every line of every writer is still kept: any lost line shows up as a gap.
    /// </summary>
    private static async Task ManyProcessesRotateAsync(string directory)
    {
        const int rounds = 3, childCount = 8, perChild = 2000; // ~95 bytes a line: 1 to 2 MiB a round, one rotation
        for (var round = 1; round <= rounds; round++)
        {
            var path = Path.Combine(directory, $"round-{round}", "dialshift.log");
            var children = await RunLogChildrenAsync(path, perChild, childCount);
            Check($"LOG-D2 {childCount} writers, round {round}: every --log-child writer exits 0", children.All(c => c.ExitCode == 0));
            var lines = ReadChildLines(path);
            var live = new FileInfo(path).Length;
            var old = File.Exists(path + ".1") ? new FileInfo(path + ".1").Length : -1;
            Console.WriteLine($"  round {round}: dialshift.log {live} bytes, dialshift.log.1 {old} bytes, {lines.Count} lines of {childCount * perChild}");
            Check($"LOG-D2 round {round}: precondition: the file rotated once, so both files hold every line",
                old > 0 && live + old > FileAppLog.MaxFileBytes);
            Check($"LOG-D2 round {round}: both files are at most 1 MiB and every line is one valid JSON object",
                live <= FileAppLog.MaxFileBytes && old <= FileAppLog.MaxFileBytes && Diagnose(FirstInvalidLine(lines)) is null);
            Check($"LOG-D2 round {round}: every line of every writer is there, in its own order (none overwritten by a writer that stopped waiting)",
                EachInOrder(lines, children, firstSequence: 0, newestSequence: perChild - 1) && lines.Count == childCount * perChild);
        }
    }

    private sealed record ChildLine(int Pid, int Sequence);

    /// <summary>One line of <c>dialshift.log.1</c> or <c>dialshift.log</c>: its file name, 1-based line number, text and parsed content.</summary>
    private sealed record KeptLine(string File, int LineNumber, string Text, ChildLine? Child)
    {
        public string Where => $"{File} line {LineNumber}";

        public override string ToString() => Child is null ? $"{Where}: invalid" : $"{Where}: process {Child.Pid} #{Child.Sequence}";
    }

    private sealed record LogChild(int Pid, int ExitCode);

    /// <summary>Prints <paramref name="problem"/> as a diagnostic line when there is one, and returns it.</summary>
    private static string? Diagnose(string? problem)
    {
        if (problem is not null) Console.WriteLine("  diagnostic: " + problem);
        return problem;
    }

    private static string? FirstInvalidLine(IReadOnlyList<KeptLine> lines) =>
        lines.FirstOrDefault(l => l.Child is null) is { } bad
            ? $"{bad.Where} is not a whole {ChildEvent} line: \"{(bad.Text.Length > 200 ? bad.Text[..200] + "…" : bad.Text)}\""
            : null;

    /// <summary>
    /// True when every child's kept lines pass <see cref="SequenceProblem"/>; prints a diagnostic for each child that
    /// does not (all of them, not just the first).
    /// </summary>
    private static bool EachInOrder(IReadOnlyList<KeptLine> lines, IEnumerable<LogChild> children, int? firstSequence, int newestSequence) =>
        children.Select(c => Diagnose(SequenceProblem(lines, c.Pid, firstSequence, newestSequence))).ToList().All(p => p is null);

    /// <summary>
    /// Null when process <paramref name="pid"/>'s kept lines, in file order (<c>.1</c> then the live file), are
    /// consecutive, start at <paramref name="firstSequence"/> (any start when null) and end with
    /// <paramref name="newestSequence"/>. Otherwise names the first gap: the expected and found sequence, where each
    /// was, whether the gap crosses the rotation boundary, and the lines around it (which process wrote them).
    /// </summary>
    private static string? SequenceProblem(IReadOnlyList<KeptLine> lines, int pid, int? firstSequence, int newestSequence)
    {
        var own = lines.Select((line, index) => (line, index)).Where(x => x.line.Child?.Pid == pid).ToList();
        if (own.Count == 0) return $"process {pid}: no kept lines";
        for (var i = 0; i < own.Count; i++)
        {
            var (line, index) = own[i];
            var expected = i == 0 ? firstSequence : own[i - 1].line.Child!.Sequence + 1;
            if (expected is not { } want || line.Child!.Sequence == want) continue;
            var after = i == 0
                ? "its first kept line"
                : $"after #{own[i - 1].line.Child!.Sequence} at {own[i - 1].line.Where}" +
                  (own[i - 1].line.File == line.File ? "" : ", across the rotation boundary");
            var context = string.Join("; ", lines.Skip(Math.Max(0, index - 3)).Take(6));
            return $"process {pid}: expected #{want} at {line.Where}, found #{line.Child.Sequence} ({after}); " +
                   $"{own.Count} kept, #{own[0].line.Child!.Sequence}..#{own[^1].line.Child!.Sequence}; around it: {context}";
        }
        var newest = own[^1].line;
        return newest.Child!.Sequence == newestSequence
            ? null
            : $"process {pid}: newest kept line is #{newest.Child.Sequence} at {newest.Where}, expected #{newestSequence}";
    }

    /// <summary>
    /// The lines of <c>path.1</c> then <c>path</c>, in file order; <see cref="KeptLine.Child"/> is null for a line that is
    /// not a whole <see cref="ChildEvent"/> JSON object with a <c>"&lt;pid&gt; &lt;sequence&gt;"</c> message.
    /// </summary>
    private static List<KeptLine> ReadChildLines(string path)
    {
        var lines = new List<KeptLine>();
        foreach (var file in new[] { path + ".1", path })
        {
            if (!File.Exists(file)) continue;
            var number = 0;
            foreach (var text in File.ReadAllLines(file))
                lines.Add(new KeptLine(Path.GetFileName(file), ++number, text, ParseChildLine(text)));
        }
        return lines;
    }

    private static ChildLine? ParseChildLine(string line)
    {
        try
        {
            var root = JsonDocument.Parse(line).RootElement;
            if (root.GetProperty("event").GetString() != ChildEvent) return null;
            var parts = root.GetProperty("msg").GetString()!.Split(' ');
            return parts.Length == 2 &&
                   int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                   int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                ? new ChildLine(pid, sequence)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts <paramref name="childCount"/> <c>--log-child</c> processes, waits until all report <c>ready</c>, then
    /// releases them together with <c>go</c> on stdin so their writes overlap. A child that does not finish within 60 s
    /// is a broken precondition.
    /// </summary>
    private static async Task<IReadOnlyList<LogChild>> RunLogChildrenAsync(string logFile, int perChild, int childCount = 2)
    {
        var processes = new List<Process>();
        try
        {
            for (var i = 0; i < childCount; i++)
            {
                var startInfo = SelfProcess.StartInfo("--log-child", logFile, perChild.ToString(CultureInfo.InvariantCulture));
                startInfo.RedirectStandardInput = true;
                processes.Add(Process.Start(startInfo) ?? throw new InvalidOperationException("Couldn't start a --log-child process."));
            }
            var stderr = processes.Select(p => p.StandardError.ReadToEndAsync()).ToList();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                foreach (var process in processes)
                {
                    var ready = await process.StandardOutput.ReadLineAsync(deadline.Token);
                    if (ready == "ready") continue;
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw new CheckFailedException($"precondition: --log-child {process.Id} printed '{ready}' instead of ready; stderr: {await stderr[processes.IndexOf(process)]}");
                }
                foreach (var process in processes) process.StandardInput.Write("go\n");
                foreach (var process in processes) process.StandardInput.Flush();
                foreach (var process in processes) await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new CheckFailedException("precondition: a --log-child process did not finish within 60 s");
            }
            for (var i = 0; i < processes.Count; i++)
            {
                var summary = (await processes[i].StandardOutput.ReadToEndAsync()).Trim();
                var error = await stderr[i];
                if (summary.Length > 0) Console.WriteLine($"  --log-child {processes[i].Id}: {summary}");
                if (error.Length > 0) Console.WriteLine($"  --log-child {processes[i].Id} stderr: {error.Trim()}");
            }
            return processes.Select(p => new LogChild(p.Id, p.ExitCode)).ToList();
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Entry point for <c>DialShift.Tests --log-child &lt;absolute log file&gt; &lt;n&gt;</c>: prints <c>ready</c>, waits
    /// for <c>go</c> on stdin, then appends <c>n</c> info lines <c>"&lt;pid&gt; &lt;sequence&gt;"</c> (sequence 0..n-1)
    /// with a <see cref="FileAppLog"/>, and prints how long its slowest write took and how many writes took at least
    /// <see cref="FileAppLog.LockWaitBudget"/> (a write that waited that long for the lock may have given up on it). Exit 0
    /// when done, 64 on bad arguments, 65 when stdin did not say <c>go</c>.
    /// </summary>
    public static int RunChild(string[] args)
    {
        if (args.Length != 3 || args[0] != "--log-child" || !Path.IsPathFullyQualified(args[1]) ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            Console.Error.WriteLine("Usage: DialShift.Tests --log-child <absolute log file> <line count>");
            return 64;
        }
        var log = new FileAppLog(args[1]);
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        Console.WriteLine("ready");
        Console.Out.Flush();
        if (Console.In.ReadLine() != "go") return 65;
        var slowest = TimeSpan.Zero;
        var slow = 0;
        for (var i = 0; i < count; i++)
        {
            var started = Stopwatch.GetTimestamp();
            log.Info(ChildEvent, pid + " " + i.ToString(CultureInfo.InvariantCulture));
            var took = Stopwatch.GetElapsedTime(started);
            if (took > slowest) slowest = took;
            if (took >= FileAppLog.LockWaitBudget) slow++;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"slowest write {slowest.TotalMilliseconds:F1} ms, {slow} of {count} writes took {FileAppLog.LockWaitBudget.TotalMilliseconds:F0} ms or more"));
        return 0;
    }

    // ---- never throws ----------------------------------------------------------------------------------------

    private static void NeverThrows(TempDirectory temp)
    {
        var blocker = temp.Combine("not-a-directory");
        File.WriteAllText(blocker, "file");
        var underFile = new FileAppLog(Path.Combine(blocker, "sub", "dialshift.log"));
        Check("§8.2.7 never throws when the log directory can't be created (a file is in the way)",
            NoThrow(() => { underFile.Info("a", "b"); underFile.Warn("a", "b", new Exception("x")); underFile.Error("a", "b"); underFile.LogStartup("Fake", DataDirectorySource.Default); }));

        if (OperatingSystem.IsWindows())
        {
            Skip("§8.2.7 never throws in a read-only directory", "uses Unix chmod; Windows ACLs are not exercised here");
            return;
        }
        var readOnly = temp.Combine("read-only");
        Directory.CreateDirectory(readOnly);
        if (!TempDirectory.TryMakeReadOnly(readOnly))
        {
            Skip("§8.2.7 never throws in a read-only directory", "chmod 0500 does not stop writes (running as root?)");
            return;
        }
        var log = new FileAppLog(Path.Combine(readOnly, "dialshift.log"));
        Check("§8.2.7 never throws in a read-only directory (chmod 0500)",
            NoThrow(() => { log.Info("a", "b"); log.Warn("a", "b", new Exception("x")); log.Error("a", "b"); log.LogStartup("Fake", DataDirectorySource.Default); }));
        Check("§8.2.7 ... and nothing was written", !File.Exists(log.LogFile));

        var rotateDir = temp.Combine("read-only-rotate");
        var full = new FileAppLog(Path.Combine(rotateDir, "dialshift.log"));
        Directory.CreateDirectory(rotateDir);
        File.WriteAllBytes(full.LogFile, Filler(FileAppLog.MaxFileBytes));
        TempDirectory.TryMakeReadOnly(rotateDir);
        Check("§8.2.7 never throws when rotation can't rename in a read-only directory", NoThrow(() => full.Info("a", "b")));
    }

    // ---- HS-10 app.start -------------------------------------------------------------------------------------

    private static void Startup(string directory)
    {
        var log = new FileAppLog(Path.Combine(directory, "dialshift.log"));
        log.LogStartup("FakeEngine", DataDirectorySource.EnvironmentOverride);
        var line = JsonDocument.Parse(File.ReadAllLines(log.LogFile).Single()).RootElement;
        var msg = line.GetProperty("msg").GetString()!;
        Console.WriteLine("  app.start msg: " + msg);
        var assembly = typeof(FileAppLog).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Check("HS-10 app.start is an info line", line.GetProperty("event").GetString() == "app.start" && line.GetProperty("level").GetString() == "info");
        Check($"HS-10 app.start has the app version (version={version})", msg.StartsWith($"version={version} ", StringComparison.Ordinal) && version.Length > 0);
        Check("HS-10 app.start has the runtime identifier", msg.Contains($" rid={RuntimeInformation.RuntimeIdentifier} ", StringComparison.Ordinal));
        Check("HS-10 app.start has the process architecture", msg.Contains($" arch={RuntimeInformation.ProcessArchitecture} ", StringComparison.Ordinal));
        Check("HS-10 app.start has the OS description", msg.Contains($" os=\"{RuntimeInformation.OSDescription}\" ", StringComparison.Ordinal));
        Check("HS-10 app.start has the engine name", msg.Contains(" engine=FakeEngine ", StringComparison.Ordinal));
        Check("HS-10 app.start has the data-dir source", msg.EndsWith(" data_dir_source=EnvironmentOverride", StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Newline-terminated filler of exactly <paramref name="length"/> bytes.</summary>
    private static byte[] Filler(long length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'#');
        bytes[^1] = (byte)'\n';
        return bytes;
    }
}
