namespace DialShift.Tests;

/// <summary>
/// A unique directory under the system temp path (never user data). <see cref="Dispose"/> restores write permission
/// on anything a test made read-only and deletes the tree; cleanup failures are ignored so they never mask a result.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DialShift-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A path inside this directory (not created).</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path)) return;
            if (!OperatingSystem.IsWindows())
            {
                foreach (var dir in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Prepend(Path))
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort: a leftover temp folder must not fail a green run.
        }
    }

    /// <summary>
    /// Makes <paramref name="directory"/> read-only (0500) and reports whether writes into it really fail. They don't
    /// when the tests run as root, in which case the caller skips the check.
    /// </summary>
    public static bool TryMakeReadOnly(string directory)
    {
        if (OperatingSystem.IsWindows()) return false;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var probe = System.IO.Path.Combine(directory, ".write-probe");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }
}
