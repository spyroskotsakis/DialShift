using DialShift.App;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

/// <summary>
/// <see cref="AppPaths.Resolve"/> (acceptance matrix HS-18, §8.2.6, D21) with an injected environment lookup; the real
/// process environment and the real data directory are never touched.
/// </summary>
public static class AppPathsTests
{
    public static void Run()
    {
        using var temp = new TempDirectory("paths");
        var overrideDir = temp.Combine("override", "data");

        var queried = new List<string>();
        var fromOverride = AppPaths.Resolve(name => { queried.Add(name); return name == "DIALSHIFT_DATA_DIR" ? overrideDir : null; }, smokeTest: false);
        Check("HS-18 the lookup asks for DIALSHIFT_DATA_DIR", queried.SequenceEqual(["DIALSHIFT_DATA_DIR"]) && AppPaths.DataDirectoryOverrideVariable == "DIALSHIFT_DATA_DIR");
        Check("HS-18 an absolute override is used verbatim with source EnvironmentOverride",
            fromOverride.DataDirectory == overrideDir && fromOverride.Source == DataDirectorySource.EnvironmentOverride);
        var trailing = overrideDir + Path.DirectorySeparatorChar;
        Check("HS-18 ... verbatim includes a trailing separator", AppPaths.Resolve(Env(trailing), smokeTest: false).DataDirectory == trailing);
        Check("HS-18 the override wins over --smoke-test", AppPaths.Resolve(Env(overrideDir), smokeTest: true).Source == DataDirectorySource.EnvironmentOverride);
        Check("HS-18 Resolve does not create the directory", !Directory.Exists(overrideDir));
        Check("HS-18 files: settings.json, dialshift.log, .single-instance.lock in the data dir",
            fromOverride.SettingsFile == Path.Combine(overrideDir, "settings.json") &&
            fromOverride.LogFile == Path.Combine(overrideDir, "dialshift.log") &&
            fromOverride.SingleInstanceLockFile == Path.Combine(overrideDir, ".single-instance.lock"));

        string[] relative = OperatingSystem.IsWindows()
            ? ["data", @".\data", @"..\data", @"C:data", @"\data", "~/data"]
            : ["data", "./data", "../data", "~/data"];
        foreach (var value in relative)
        {
            string? message = null;
            try { AppPaths.Resolve(Env(value), smokeTest: false); }
            catch (InvalidOperationException ex) { message = ex.Message; }
            Check($"HS-18 a non-absolute override '{value}' is rejected with a clear message",
                message != null && message.Contains("DIALSHIFT_DATA_DIR", StringComparison.Ordinal) && message.Contains("absolute", StringComparison.Ordinal) &&
                message.Contains(value, StringComparison.Ordinal));
        }

        foreach (var blank in new[] { "", " ", "\t" })
            Check($"HS-18 a blank override ('{blank.Replace("\t", "\\t", StringComparison.Ordinal)}') is ignored",
                AppPaths.Resolve(Env(blank), smokeTest: false).Source == DataDirectorySource.Default);

        var smokeA = AppPaths.Resolve(Env(null), smokeTest: true);
        var smokeB = AppPaths.Resolve(Env(null), smokeTest: true);
        var smokeName = Path.GetFileName(smokeA.DataDirectory);
        Check("HS-18 --smoke-test without an override uses a temp dir, source SmokeTestTemp",
            smokeA.Source == DataDirectorySource.SmokeTestTemp && Path.GetDirectoryName(smokeA.DataDirectory) + Path.DirectorySeparatorChar == Path.GetTempPath());
        Check("HS-18 ... named DialShift-smoke-<32 hex>",
            smokeName.StartsWith("DialShift-smoke-", StringComparison.Ordinal) && smokeName.Length == 16 + 32 && smokeName[16..].All(Uri.IsHexDigit));
        Check("HS-18 ... fresh for every run, and not created by Resolve", smokeA.DataDirectory != smokeB.DataDirectory && !Directory.Exists(smokeA.DataDirectory));

        var fallback = AppPaths.Resolve(Env(null), smokeTest: false);
        Check("HS-18 with neither, the OS default is used (source Default)", fallback.Source == DataDirectorySource.Default);
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Check("HS-18 macOS default is exactly ~/Library/Application Support/DialShift",
                fallback.DataDirectory == Path.Combine(home, "Library", "Application Support", "DialShift"));
            Console.WriteLine("  macOS default: " + fallback.DataDirectory);
        }
        else if (OperatingSystem.IsWindows())
        {
            Check("HS-18 Windows default is exactly %LOCALAPPDATA%\\DialShift (same as WPF, OQ-5)",
                fallback.DataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DialShift"));
        }
        else
        {
            Skip("HS-18 OS default location", "the App supports only macOS and Windows");
        }
        Check("HS-18 a null lookup is rejected", Throws<ArgumentNullException>(() => AppPaths.Resolve(null!, smokeTest: false)));
    }

    private static Func<string, string?> Env(string? dataDir) => name => name == AppPaths.DataDirectoryOverrideVariable ? dataDir : null;
}
