namespace DialShift.App;

/// <summary>
/// Command-line switches. <c>--tray</c> starts with the main window hidden (launch-at-login entries pass it, BHV-09).
/// <c>--smoke-test</c> uses a fresh temporary data directory unless <c>DIALSHIFT_DATA_DIR</c> is set (D21).
/// </summary>
public sealed record LaunchOptions(bool StartInTray, bool SmokeTest)
{
    public const string TraySwitch = "--tray";
    public const string SmokeTestSwitch = "--smoke-test";

    public static LaunchOptions Parse(IReadOnlyCollection<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return new LaunchOptions(args.Contains(TraySwitch), args.Contains(SmokeTestSwitch));
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
}
