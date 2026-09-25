namespace DialShift.App;

public enum DataDirectorySource { Default, EnvironmentOverride, SmokeTestTemp }

/// <summary>
/// The per-user data directory and the files in it (acceptance matrix §8.2.6, brief 1 §7.9).
/// Computed once in <c>Program.Main</c> and passed to the settings store, the log and the single-instance lock.
/// </summary>
public sealed class AppPaths
{
    public const string DataDirectoryOverrideVariable = "DIALSHIFT_DATA_DIR";

    private AppPaths(string dataDirectory, DataDirectorySource source)
    {
        DataDirectory = dataDirectory;
        Source = source;
    }

    public string DataDirectory { get; }
    public DataDirectorySource Source { get; }
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string LogFile => Path.Combine(DataDirectory, "dialshift.log");
    public string SingleInstanceLockFile => Path.Combine(DataDirectory, ".single-instance.lock");

    /// <summary>
    /// Resolution order: a non-blank <c>DIALSHIFT_DATA_DIR</c> (must be absolute, used verbatim), then a fresh
    /// temp folder for <c>--smoke-test</c>, then the OS default: <c>%LOCALAPPDATA%\DialShift</c> on Windows and
    /// <c>~/Library/Application Support/DialShift</c> on macOS. Does not touch the file system; startup creates the
    /// directory when it first writes to it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The override is set but is not an absolute path.</exception>
    public static AppPaths Resolve(Func<string, string?> getEnvironmentVariable, bool smokeTest)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var overridePath = getEnvironmentVariable(DataDirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!Path.IsPathFullyQualified(overridePath))
                throw new InvalidOperationException(
                    $"{DataDirectoryOverrideVariable} must be an absolute path, but it is \"{overridePath}\".");
            return new AppPaths(overridePath, DataDirectorySource.EnvironmentOverride);
        }

        if (smokeTest)
            return new AppPaths(
                Path.Combine(Path.GetTempPath(), "DialShift-smoke-" + Guid.NewGuid().ToString("N")),
                DataDirectorySource.SmokeTestTemp);

        var defaultDirectory = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "DialShift")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DialShift");
        return new AppPaths(defaultDirectory, DataDirectorySource.Default);
    }
}
