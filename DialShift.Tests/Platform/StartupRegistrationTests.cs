using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using DialShift.App.Platform;
using DialShift.App.Platform.MacOS;
using DialShift.App.Platform.Windows;
using DialShift.Tests.Fakes;
using Microsoft.Win32;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Platform;

/// <summary>
/// <see cref="IStartupRegistration"/> contract (acceptance matrix HS-11, §8.2.1; matrix row "Launch at login: contract
/// test"). macOS runs against a temp LaunchAgents directory, Windows against a throw-away HKCU test subkey, both injected
/// through the constructor seams, with a fake executable path. The real user entries are never touched.
/// </summary>
public static class StartupRegistrationTests
{
    public static async Task RunAsync()
    {
        if (OperatingSystem.IsMacOS()) await MacAsync();
        else Skip("HS-11 macOS LaunchAgent contract", "macOS only");

        if (OperatingSystem.IsWindows()) await WindowsAsync();
        else Skip("HS-11 Windows HKCU Run contract", "Windows only (runs on windows-latest in CI)");
    }

    // ---- macOS -----------------------------------------------------------------------------------------------

    [SupportedOSPlatform("macos")]
    private static async Task MacAsync()
    {
        using var temp = new TempDirectory("launchagents");
        var agents = temp.Combine("LaunchAgents");
        var exe = CreateFile(temp.Combine("bin", "DialShift"));
        var log = new RecordingAppLog();
        var registration = new MacStartupRegistration(log, agents, exe);

        Check("HS-11 mac: the plist path is <LaunchAgents>/com.tsiger.dialshift.plist", registration.PlistPath == Path.Combine(agents, "com.tsiger.dialshift.plist"));
        Check("HS-11 mac: no plist means not enabled, no diagnostic", await registration.GetStatusAsync() == new StartupRegistrationStatus(false));

        var enabled = await registration.SetEnabledAsync(true);
        Check("HS-11 mac: enable returns the verified status enabled", enabled == new StartupRegistrationStatus(true));
        Check("HS-11 mac: GetStatusAsync agrees", await registration.GetStatusAsync() == new StartupRegistrationStatus(true));
        Check("HS-11 mac: plutil -lint accepts the plist", Lint(registration.PlistPath));
        var plist = ReadPlist(registration.PlistPath);
        Check("HS-11 mac: Label is com.tsiger.dialshift", plist["Label"].Value == "com.tsiger.dialshift");
        Check("HS-11 mac: dev run: ProgramArguments = [<exe>, --tray]", Strings(plist["ProgramArguments"]).SequenceEqual([exe, "--tray"]));
        Check("HS-11 mac: RunAtLoad is true and ProcessType Interactive", plist["RunAtLoad"].Name.LocalName == "true" && plist["ProcessType"].Value == "Interactive");
        Check("HS-11 mac: no temp file is left behind (atomic write)", Directory.GetFiles(agents).Select(Path.GetFileName).SequenceEqual(["com.tsiger.dialshift.plist"]));
        Check("HS-11 mac: enabling twice stays enabled", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true) && Directory.GetFiles(agents).Length == 1);

        var disabled = await registration.SetEnabledAsync(false);
        Check("HS-11 mac: disable returns not enabled without a diagnostic", disabled == new StartupRegistrationStatus(false));
        Check("HS-11 mac: ... and deletes the plist", !File.Exists(registration.PlistPath));
        Check("HS-11 mac: disabling again is harmless", await registration.SetEnabledAsync(false) == new StartupRegistrationStatus(false));

        await MacBundleAsync(temp);
        await MacEscapingAsync(temp);
        await MacStaleAsync(temp);
        await MacForeignAsync(temp);
        await MacUnwritableAsync(temp);
        Check("HS-11 mac: the happy path logs no startup_registration.error", !log.HasEvent("startup_registration.error"));
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacBundleAsync(TempDirectory temp)
    {
        var bundle = temp.Combine("Applications", "Dial Shift.app");
        var exe = CreateFile(Path.Combine(bundle, "Contents", "MacOS", "DialShift"));
        var registration = new MacStartupRegistration(new RecordingAppLog(), temp.Combine("LaunchAgents-bundle"), exe);
        Check("HS-11 mac: .app bundle: enable is verified", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        Check("HS-11 mac: .app bundle: ProgramArguments = [/usr/bin/open, -a, <bundle>, --args, --tray]",
            Strings(ReadPlist(registration.PlistPath)["ProgramArguments"]).SequenceEqual(["/usr/bin/open", "-a", bundle, "--args", "--tray"]));
        Check("HS-11 mac: .app bundle: plutil -lint accepts it", Lint(registration.PlistPath));
        Directory.Delete(bundle, recursive: true);
        var stale = await registration.GetStatusAsync();
        Check("HS-11 / MX-15 mac: the bundle was deleted: not enabled with the stale diagnostic", stale == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacEscapingAsync(TempDirectory temp)
    {
        var exe = CreateFile(temp.Combine("R&B <Mix> \"Live\" 'n' ]]> dir", "DialShift"));
        var registration = new MacStartupRegistration(new RecordingAppLog(), temp.Combine("LaunchAgents-escape"), exe);
        Check("HS-11 mac: a path with & < > \" ' ]]> is enabled and verified", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        var raw = await File.ReadAllTextAsync(registration.PlistPath);
        Check("HS-11 mac: ... XML-escaped in the file (&amp; &lt; &gt;)",
            raw.Contains("R&amp;B &lt;Mix&gt;", StringComparison.Ordinal) && !raw.Contains("R&B", StringComparison.Ordinal) && !raw.Contains("<Mix>", StringComparison.Ordinal));
        Check("HS-11 mac: ... plutil -lint accepts it", Lint(registration.PlistPath));
        Check("HS-11 mac: ... and it round-trips to the exact path", Strings(ReadPlist(registration.PlistPath)["ProgramArguments"]).SequenceEqual([exe, "--tray"]));
        Check("HS-11 mac: ... Apple's parser reads back the same first argument (plutil -extract)", PlutilFirstArgument(registration.PlistPath) == exe);
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacStaleAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-stale");
        var oldExe = CreateFile(temp.Combine("old", "DialShift"));
        var newExe = CreateFile(temp.Combine("new", "DialShift"));
        var old = new MacStartupRegistration(new RecordingAppLog(), agents, oldExe);
        Check("HS-11 mac: stale precondition: the old copy enabled itself", (await old.SetEnabledAsync(true)).IsEnabled);

        var current = new MacStartupRegistration(new RecordingAppLog(), agents, newExe);
        Check("HS-11 / MX-15 mac: an entry for another copy is not enabled, with the stale diagnostic",
            await current.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        Check("HS-11 / MX-15 mac: turning it on again repairs it", await current.SetEnabledAsync(true) == new StartupRegistrationStatus(true));

        File.Delete(newExe);
        Check("HS-11 / MX-15 mac: the target executable was deleted: not enabled, stale diagnostic",
            await current.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        var reenable = await current.SetEnabledAsync(true);
        Check("HS-11 mac: enabling a missing executable never reports enabled", !reenable.IsEnabled && !string.IsNullOrEmpty(reenable.DiagnosticMessage));
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacForeignAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-foreign");
        var exe = CreateFile(temp.Combine("foreign-bin", "DialShift"));
        var log = new RecordingAppLog();
        var registration = new MacStartupRegistration(log, agents, exe);
        Directory.CreateDirectory(agents);

        await File.WriteAllTextAsync(registration.PlistPath, "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Label</key><string>com.example.other</string></dict></plist>");
        var foreign = await registration.GetStatusAsync();
        Check("HS-11 mac: an entry in an unexpected format is not enabled, with a diagnostic", !foreign.IsEnabled && !string.IsNullOrEmpty(foreign.DiagnosticMessage));

        await File.WriteAllTextAsync(registration.PlistPath, "<plist><dict><key>Label</key>");
        var corrupt = await registration.GetStatusAsync();
        Check("HS-11 mac: a corrupt plist is not enabled, with a diagnostic, and never throws", !corrupt.IsEnabled && !string.IsNullOrEmpty(corrupt.DiagnosticMessage));
        Check("HS-11 mac: ... the read failure is logged", log.HasEvent("startup_registration.error"));

        await File.WriteAllTextAsync(registration.PlistPath,
            "<!DOCTYPE plist [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><plist version=\"1.0\"><dict><key>Label</key><string>&xxe;</string></dict></plist>");
        var entity = await registration.GetStatusAsync();
        Check("HS-11 mac: a DTD with an external entity is never resolved (not enabled, no throw)", !entity.IsEnabled);

        Check("HS-11 mac: enable overwrites a corrupt entry", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            Check("HS-11 mac: a cancelled token throws OperationCanceledException",
                await ThrowsAsync<OperationCanceledException>(() => registration.SetEnabledAsync(false, cancelled.Token)) &&
                await ThrowsAsync<OperationCanceledException>(() => registration.GetStatusAsync(cancelled.Token)));
        }
        Check("HS-11 mac: ... without changing the entry", (await registration.GetStatusAsync()).IsEnabled);
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacUnwritableAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-readonly");
        Directory.CreateDirectory(agents);
        var exe = CreateFile(temp.Combine("ro-bin", "DialShift"));
        var registration = new MacStartupRegistration(new RecordingAppLog(), agents, exe);
        if (!TempDirectory.TryMakeReadOnly(agents))
        {
            Skip("HS-11 mac: unwritable LaunchAgents dir", "chmod 0500 does not stop writes (running as root?)");
            return;
        }
        var failed = await registration.SetEnabledAsync(true);
        Console.WriteLine($"  unwritable enable: {failed}");
        Check("HS-11 mac: unwritable dir: enable reports not enabled (never a false \"enabled\")", !failed.IsEnabled);
        Check("HS-11 mac: ... with a diagnostic", failed.DiagnosticMessage?.StartsWith("Couldn't turn on launch at login", StringComparison.Ordinal) == true);
        Check("HS-11 mac: ... and no file or temp file was created", Directory.GetFiles(agents).Length == 0);

        File.SetUnixFileMode(agents, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Check("HS-11 mac: disable precondition: enabled", (await registration.SetEnabledAsync(true)).IsEnabled);
        TempDirectory.TryMakeReadOnly(agents);
        var stuck = await registration.SetEnabledAsync(false);
        Console.WriteLine($"  unwritable disable: {stuck}");
        Check("HS-11 mac: unwritable dir: disable that can't delete still reports the true state (enabled) with a diagnostic",
            stuck.IsEnabled && stuck.DiagnosticMessage?.StartsWith("Couldn't turn off launch at login", StringComparison.Ordinal) == true);

        var parentIsFile = new MacStartupRegistration(new RecordingAppLog(), Path.Combine(exe, "LaunchAgents"), exe);
        var blocked = await parentIsFile.SetEnabledAsync(true);
        Check("HS-11 mac: a LaunchAgents path under a file: not enabled with a diagnostic", !blocked.IsEnabled && !string.IsNullOrEmpty(blocked.DiagnosticMessage));
    }

    private static Dictionary<string, XElement> ReadPlist(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var children = XDocument.Load(reader).Root!.Element("dict")!.Elements().ToList();
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < children.Count; i += 2) result[children[i].Value] = children[i + 1];
        return result;
    }

    private static IEnumerable<string> Strings(XElement array) => array.Elements("string").Select(e => e.Value);

    /// <summary><c>plutil -lint</c>: exit 0 means Apple's parser accepts the file.</summary>
    private static bool Lint(string path)
    {
        var (exitCode, output) = RunTool("/usr/bin/plutil", "-lint", path);
        if (exitCode != 0) Console.WriteLine($"  plutil -lint {path}: exit {exitCode}: {output}");
        return exitCode == 0;
    }

    /// <summary>ProgramArguments[0] as Apple's parser sees it (<c>plutil -extract ProgramArguments.0 raw</c>).</summary>
    private static string? PlutilFirstArgument(string path)
    {
        var (exitCode, output) = RunTool("/usr/bin/plutil", "-extract", "ProgramArguments.0", "raw", "-o", "-", path);
        return exitCode == 0 ? output.TrimEnd('\n') : null;
    }

    private static (int ExitCode, string Output) RunTool(string tool, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    // ---- Windows ---------------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static async Task WindowsAsync()
    {
        var subkey = $@"Software\DialShift-Tests-{Guid.NewGuid():N}";
        using var temp = new TempDirectory("run-key");
        try
        {
            var exe = CreateFile(temp.Combine("Dial Shift, v2", "DialShift.exe"));
            var log = new RecordingAppLog();
            var registration = new WindowsStartupRegistration(log, subkey, exe);
            Check("HS-11 win: value name is DialShift, default key is HKCU Run",
                WindowsStartupRegistration.ValueName == "DialShift" && WindowsStartupRegistration.DefaultRunKeyPath == @"Software\Microsoft\Windows\CurrentVersion\Run");
            Check("HS-11 win: no value means not enabled, no diagnostic", await registration.GetStatusAsync() == new StartupRegistrationStatus(false));

            Check("HS-11 win: enable returns the verified status enabled", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
            using (var key = Registry.CurrentUser.OpenSubKey(subkey)!)
            {
                Check("HS-11 win: the value is a REG_SZ", key.GetValueKind("DialShift") == RegistryValueKind.String);
                Check("HS-11 win: the value is \"<exe>\" --tray, as scripts/Install.ps1 writes it", (string?)key.GetValue("DialShift") == $"\"{exe}\" --tray");
            }
            Check("HS-11 win: GetStatusAsync agrees", await registration.GetStatusAsync() == new StartupRegistrationStatus(true));

            using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
                key.SetValue("DialShift", $"\"{exe.ToUpperInvariant()}\" --tray", RegistryValueKind.String);
            Check("HS-11 win: the path compares case-insensitively", await registration.GetStatusAsync() == new StartupRegistrationStatus(true));

            Check("HS-11 win: disable returns not enabled without a diagnostic", await registration.SetEnabledAsync(false) == new StartupRegistrationStatus(false));
            using (var key = Registry.CurrentUser.OpenSubKey(subkey)!)
                Check("HS-11 win: ... and deletes the value", key.GetValue("DialShift") == null);
            Check("HS-11 win: disabling again is harmless", await registration.SetEnabledAsync(false) == new StartupRegistrationStatus(false));

            using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
                key.SetValue("DialShift", "\"C:\\Old Copy\\DialShift.exe\" --tray", RegistryValueKind.String);
            Check("HS-11 / MX-15 win: a value for another copy is not enabled, with the stale diagnostic",
                await registration.GetStatusAsync() == new StartupRegistrationStatus(false, WindowsStartupRegistration.StaleDiagnostic));
            Check("HS-11 / MX-15 win: turning it on again repairs it", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));

            using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
                key.SetValue("DialShift", 42, RegistryValueKind.DWord);
            Check("HS-11 win: a non-string value is not enabled, with a diagnostic",
                await registration.GetStatusAsync() == new StartupRegistrationStatus(false, WindowsStartupRegistration.StaleDiagnostic));

            Check("HS-11 win: precondition: enabled again", (await registration.SetEnabledAsync(true)).IsEnabled);
            File.Delete(exe);
            Check("HS-11 / MX-15 win: the executable was deleted: not enabled, stale diagnostic",
                await registration.GetStatusAsync() == new StartupRegistrationStatus(false, WindowsStartupRegistration.StaleDiagnostic));
            var missing = await registration.SetEnabledAsync(true);
            Check("HS-11 win: enabling a missing executable never reports enabled", !missing.IsEnabled && !string.IsNullOrEmpty(missing.DiagnosticMessage));
            Skip("HS-11 win: write failure carries a diagnostic", "needs a registry ACL denial on HKCU; covered by the macOS unwritable-dir check and NC-04");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false);
        }
        using (var gone = Registry.CurrentUser.OpenSubKey(subkey))
            Check("HS-11 win: the test subkey was deleted", gone == null);
    }

    // ---- shared ----------------------------------------------------------------------------------------------

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        return path;
    }
}
