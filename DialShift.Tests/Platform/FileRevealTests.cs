using System.Diagnostics;
using System.Runtime.Versioning;
using DialShift.App.Platform;
using DialShift.App.Platform.MacOS;
using DialShift.App.Platform.Windows;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Platform;

/// <summary>
/// <see cref="IFileRevealService"/> (acceptance matrix HS-12, §8.2.3) through the process-launcher seam: each check
/// inspects the <see cref="ProcessStartInfo"/> that would be launched; nothing is started.
/// </summary>
public static class FileRevealTests
{
    /// <summary>Names that would break a shell or a command string: spaces, quotes, ';', '&amp;', '$()', commas, a leading '-'.</summary>
    private static readonly string[] HostileNames =
    [
        "My Stations", "it's \"quoted\"", "a;b & c", "$(touch pwned)", "comma, separated", "-R", "/select,x",
    ];

    public static async Task RunAsync()
    {
        using var temp = new TempDirectory("reveal");
        if (OperatingSystem.IsMacOS()) await MacAsync(temp);
        else Skip("HS-12 macOS /usr/bin/open start info", "macOS only");

        if (OperatingSystem.IsWindows()) await WindowsAsync(temp);
        else Skip("HS-12 Windows explorer.exe start info", "Windows only (runs on windows-latest in CI)");
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacAsync(TempDirectory temp)
    {
        var launches = new List<ProcessStartInfo>();
        var service = new MacFileRevealService((info, _) => { launches.Add(info); return Task.CompletedTask; });

        foreach (var name in HostileNames.Where(n => !n.Contains('/')))
        {
            var folder = Directory.CreateDirectory(temp.Combine("mac-folders", name)).FullName;
            launches.Clear();
            await service.RevealInFileManagerAsync(folder);
            var info = launches.Single();
            Check($"HS-12 mac folder '{name}': /usr/bin/open <dir> as one ArgumentList entry",
                info.FileName == "/usr/bin/open" && info.ArgumentList.SequenceEqual([folder]) && info.Arguments.Length == 0 && !info.UseShellExecute);

            var file = temp.Combine("mac-files", name + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
            launches.Clear();
            await service.RevealInFileManagerAsync(file);
            info = launches.Single();
            Check($"HS-12 mac file '{name}': /usr/bin/open -R <file>, path verbatim in its own entry",
                info.FileName == "/usr/bin/open" && info.ArgumentList.SequenceEqual(["-R", file]) && info.Arguments.Length == 0 && !info.UseShellExecute);
        }

        var nested = Directory.CreateDirectory(temp.Combine("mac-rel", "sub")).FullName;
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(nested)!;
            File.WriteAllText(Path.Combine(nested, "-x.txt"), "x");
            launches.Clear();
            await service.RevealInFileManagerAsync(Path.Combine("sub", "-x.txt"));
            var path = launches.Single().ArgumentList[^1];
            Check("HS-12 mac: a relative path is made absolute, so it can't be read as an option", path.StartsWith('/') && path.EndsWith("/sub/-x.txt", StringComparison.Ordinal));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        launches.Clear();
        await service.RevealInFileManagerAsync(nested + "/");
        Check("HS-12 mac: a trailing separator on a folder is trimmed", launches.Single().ArgumentList.SequenceEqual([Path.TrimEndingDirectorySeparator(Path.GetFullPath(nested))]));

        await MissingPathsAsync("mac", service, temp, launches);
    }

    [SupportedOSPlatform("windows")]
    private static async Task WindowsAsync(TempDirectory temp)
    {
        var launches = new List<ProcessStartInfo>();
        var service = new WindowsFileRevealService((info, _) => { launches.Add(info); return Task.CompletedTask; });
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        foreach (var name in HostileNames.Where(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0))
        {
            var folder = Directory.CreateDirectory(temp.Combine("win-folders", name)).FullName;
            launches.Clear();
            await service.RevealInFileManagerAsync(folder);
            var info = launches.Single();
            Check($"HS-12 win folder '{name}': explorer.exe <dir> (full path from %WINDIR%) as one ArgumentList entry",
                info.FileName == explorer && Path.IsPathFullyQualified(info.FileName) && info.ArgumentList.SequenceEqual([folder]) &&
                info.Arguments.Length == 0 && !info.UseShellExecute);

            var file = temp.Combine("win-files", name + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");
            launches.Clear();
            await service.RevealInFileManagerAsync(file);
            info = launches.Single();
            Check($"HS-12 win file '{name}': /select,<file> is a SINGLE ArgumentList entry",
                info.FileName == explorer && info.ArgumentList.SequenceEqual(["/select," + file]) && info.Arguments.Length == 0 && !info.UseShellExecute);
        }

        var spaced = temp.Combine("win-files", "comma, separated.log");
        launches.Clear();
        await service.RevealInFileManagerAsync(spaced);
        Check("HS-12 win: a path with spaces and commas stays one entry, which .NET quotes as a whole",
            launches.Single().ArgumentList.Count == 1 && launches.Single().ArgumentList[0] == "/select," + spaced);

        await MissingPathsAsync("win", service, temp, launches);
    }

    private static async Task MissingPathsAsync(string os, IFileRevealService service, TempDirectory temp, List<ProcessStartInfo> launches)
    {
        launches.Clear();
        Check($"HS-12 {os}: a missing file raises FileNotFoundException",
            await ThrowsExactlyAsync<FileNotFoundException>(() => service.RevealInFileManagerAsync(temp.Combine("missing", "dialshift.log"))));
        Check($"HS-12 {os}: a missing folder raises DirectoryNotFoundException",
            await ThrowsExactlyAsync<DirectoryNotFoundException>(() => service.RevealInFileManagerAsync(temp.Combine("missing-folder"))));
        Check($"HS-12 {os}: a missing path with a trailing separator raises DirectoryNotFoundException",
            await ThrowsExactlyAsync<DirectoryNotFoundException>(() => service.RevealInFileManagerAsync(temp.Combine("missing.d") + Path.DirectorySeparatorChar)));
        Check($"HS-12 {os}: blank and null paths raise ArgumentException",
            await ThrowsAsync<ArgumentException>(() => service.RevealInFileManagerAsync(" ")) &&
            await ThrowsAsync<ArgumentException>(() => service.RevealInFileManagerAsync(null!)));
        Check($"HS-12 {os}: nothing is launched for an invalid path", launches.Count == 0);
    }

    private static async Task<bool> ThrowsExactlyAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (Exception ex) { return ex.GetType() == typeof(T); }
    }
}
