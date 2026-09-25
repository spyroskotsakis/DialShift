namespace DialShift.App;

/// <summary>
/// Command-line switches. <c>--tray</c> starts with the main window hidden (launch-at-login entries pass it, BHV-09).
/// <c>--smoke-test</c> uses a fresh temporary data directory unless <c>DIALSHIFT_DATA_DIR</c> is set (D21), runs the
/// native UI smoke checks after startup and quits with their result; <c>--output &lt;dir&gt;</c> is where it writes
/// <c>results.json</c> and the screenshots, and <c>--recovery-test</c> adds the retry/fallback checks. Those two are
/// ignored without <c>--smoke-test</c>.
/// </summary>
public sealed record LaunchOptions(bool StartInTray, bool SmokeTest, bool RecoveryTest = false, string? SmokeOutputDirectory = null)
{
    public const string TraySwitch = "--tray";
    public const string SmokeTestSwitch = "--smoke-test";
    public const string RecoveryTestSwitch = "--recovery-test";
    public const string OutputSwitch = "--output";

    public static LaunchOptions Parse(IReadOnlyCollection<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var list = args.ToList();
        var smoke = list.Contains(SmokeTestSwitch);
        var outputIndex = list.IndexOf(OutputSwitch);
        var output = smoke && outputIndex >= 0 && outputIndex + 1 < list.Count ? list[outputIndex + 1] : null;
        return new LaunchOptions(list.Contains(TraySwitch), smoke, smoke && list.Contains(RecoveryTestSwitch), output);
    }

    /// <summary>The arguments with the smoke-only switches (and the <c>--output</c> value) removed: a normal launch of the same app.</summary>
    public static IEnumerable<string> WithoutSmokeSwitches(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var skipValue = false;
        foreach (var arg in args)
        {
            if (skipValue)
            {
                skipValue = false;
                continue;
            }
            if (arg == OutputSwitch) skipValue = true;
            else if (arg is not (SmokeTestSwitch or RecoveryTestSwitch)) yield return arg;
        }
    }
}

/// <summary>Process exit codes (acceptance matrix §8.2.4).</summary>
public static class ExitCodes
{
    /// <summary>Normal quit, or a second launch that activated the running instance.</summary>
    public const int Success = 0;

    /// <summary>Startup failed: the startup-failure dialog was shown (BHV-04, HS-08).</summary>
    public const int StartupFailed = 1;

    /// <summary>A second launch whose activation request was rejected or not answered.</summary>
    public const int ActivationFailed = 2;

    /// <summary>The single-instance lock was acquired but the activation channel could not start.</summary>
    public const int SingleInstanceFailed = 3;

    /// <summary>A <c>--smoke-test</c> run in which at least one check failed, or the watchdog fired (results.json says which).</summary>
    public const int SmokeTestFailed = 4;
}
