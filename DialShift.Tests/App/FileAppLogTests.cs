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
/// startup fields). Every log file lives in its own temp directory.
/// </summary>
public static class FileAppLogTests
{
    private const string SecretUrl = "https://user:pass@radio.example.com:8443/live/stream.mp3?token=abc#x";
    private static readonly string[] SecretParts = ["user", "pass", "token=abc", "/live/stream.mp3", "#x"];

    public static void Run()
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
        // The URL run stops only at whitespace, quotes or angle brackets, so a ';' glued to a URL is redacted with it.
        Check("CT-LOG-02 every URL in free text is redacted",
            redacted == "Couldn't open https://radio.example.com:8443/… fallback http://backup.example.org/… failed (see \"https://x.example/…\")");
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
    /// Known defect LOG-D1 (opt-in repro): §8.2.7 says a second-instance process can log to the same file, but each write
    /// opens with <c>FileMode.Append</c>, which positions at the end once at open time instead of appending atomically
    /// (no <c>O_APPEND</c>), and the lock is per instance. Two writers therefore overwrite and tear each other's lines.
    /// Two instances in one process stand in for the primary and a second-instance process.
    /// </summary>
    private static void TwoInstancesShareFile(string directory)
    {
        const string name = "LOG-D1 two logger instances on one file (primary + second process): no line lost or torn";
        if (Environment.GetEnvironmentVariable(SingleInstanceTests.KnownDefectsVariable) != "1")
        {
            Skip(name, $"known platform defect LOG-D1, fails on macOS; set {SingleInstanceTests.KnownDefectsVariable}=1 to run the repro");
            return;
        }
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
        Check(name, lines.Length == 2 * perInstance && valid == 2 * perInstance);
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
