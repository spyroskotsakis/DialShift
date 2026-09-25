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
/// through the constructor seams, with a fake executable path. The real user entries are never touched: the Windows
/// StartupApproved key is a test subkey too, and macOS <c>launchctl</c> goes to a fake, except for one read-only
/// <c>print-disabled</c> call that proves the real output parses.
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
        var registration = NewMac(log, agents, exe);

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
        await MacDisabledAsync(temp);
        await MacLaunchctlAsync(temp);
        MacPrintDisabledParsing();
        await MacRealLaunchctlAsync(temp);
        await MacLegacyAsync(temp);
        await MacInPlaceUpgradeAsync(temp);
        await MacTranslocatedAsync(temp);
        Check("HS-11 mac: the happy path logs no startup_registration.error", !log.HasEvent("startup_registration.error"));
    }

    /// <summary>A registration whose <c>launchctl</c> calls go to <paramref name="launchd"/> (a fresh fake by default),
    /// so no test ever reads or changes the real user's launchd overrides unless it asks for that.</summary>
    [SupportedOSPlatform("macos")]
    private static MacStartupRegistration NewMac(RecordingAppLog log, string agents, string exe, FakeLaunchctl? launchd = null) =>
        new(log, agents, exe, (launchd ?? new FakeLaunchctl()).RunAsync);

    /// <summary>F2 (a): a plist with <c>Disabled=true</c> is not enabled; enabling rewrites it without the key.</summary>
    [SupportedOSPlatform("macos")]
    private static async Task MacDisabledAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-disabled");
        var exe = CreateFile(temp.Combine("disabled-bin", "DialShift"));
        var registration = NewMac(new RecordingAppLog(), agents, exe);
        Check("F2 mac: precondition: enabled", (await registration.SetEnabledAsync(true)).IsEnabled);

        SetPlistKey(registration.PlistPath, "Disabled", "<true/>");
        Check("F2 mac: plist Disabled=true is not enabled, with the disabled diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.PlistDisabledDiagnostic));
        Check("F2 mac: ... plutil agrees the file is valid", Lint(registration.PlistPath));
        Check("F2 mac: turning it on again rewrites the plist and is verified", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        Check("F2 mac: ... without a Disabled key", !ReadPlist(registration.PlistPath).ContainsKey("Disabled"));

        SetPlistKey(registration.PlistPath, "Disabled", "<false/>");
        Check("F2 mac: plist Disabled=false is still enabled", await registration.GetStatusAsync() == new StartupRegistrationStatus(true));
    }

    /// <summary>F2 (b): launchd's own disabled state, through the fake <c>launchctl</c>.</summary>
    [SupportedOSPlatform("macos")]
    private static async Task MacLaunchctlAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-launchd");
        var exe = CreateFile(temp.Combine("launchd-bin", "DialShift"));
        var launchd = new FakeLaunchctl();
        var log = new RecordingAppLog();
        var registration = NewMac(log, agents, exe, launchd);
        var domain = "gui/" + RunTool("/usr/bin/id", "-u").Output.Trim();

        Check("F2 mac: no plist: launchctl is not consulted", await registration.GetStatusAsync() == new StartupRegistrationStatus(false) && launchd.Calls.Count == 0);
        Check("F2 mac: no override: enabled", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        var call = launchd.Calls[^1];
        Check($"F2 mac: the check is /bin/launchctl print-disabled {domain}, as ArgumentList entries, no shell",
            call.FileName == "/bin/launchctl" && call.ArgumentList.SequenceEqual(["print-disabled", domain]) && call.Arguments.Length == 0 && !call.UseShellExecute);
        Check("F2 mac: enable ran no launchctl command other than print-disabled", launchd.Calls.All(c => c.ArgumentList[0] == "print-disabled"));

        launchd.Override = "disabled";
        var disabled = await registration.GetStatusAsync();
        Check("F2 mac: \"com.tsiger.dialshift\" => disabled: not enabled, with the disabled diagnostic",
            disabled == new StartupRegistrationStatus(false, MacStartupRegistration.DisabledDiagnostic));
        Check("F2 mac: ... which points to Login Items", disabled.DiagnosticMessage!.Contains(MacStartupRegistration.LoginItemsHint, StringComparison.Ordinal));
        launchd.Override = "true";
        Check("F2 mac: legacy => true is disabled", (await registration.GetStatusAsync()).DiagnosticMessage == MacStartupRegistration.DisabledDiagnostic);
        launchd.Override = "false";
        Check("F2 mac: legacy => false is enabled", await registration.GetStatusAsync() == new StartupRegistrationStatus(true));
        launchd.Override = "enabled";
        Check("F2 mac: => enabled is enabled", await registration.GetStatusAsync() == new StartupRegistrationStatus(true));

        launchd.Override = "disabled";
        launchd.Calls.Clear();
        Check("F2 mac: turning it on clears the launchd override and is verified", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        Check($"F2 mac: ... with launchctl enable {domain}/com.tsiger.dialshift (never bootstrap/bootout/load)",
            launchd.Calls.Any(c => c.ArgumentList.SequenceEqual(["enable", domain + "/com.tsiger.dialshift"])) &&
            launchd.Calls.All(c => c.ArgumentList[0] is "print-disabled" or "enable"));

        launchd.Override = "disabled";
        launchd.EnableClears = false; // e.g. Login Items keeps it off
        var stuck = await registration.SetEnabledAsync(true);
        Check("F2 mac: an override that stays disabled is never reported enabled",
            !stuck.IsEnabled && stuck.DiagnosticMessage == "Launch at login was saved but couldn't be verified. " + MacStartupRegistration.DisabledDiagnostic);
        launchd.EnableClears = true;
        launchd.Override = null;

        launchd.ExitCode = 1;
        Check("F2 mac: print-disabled exits non-zero: not enabled, unverified diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.UnverifiedDiagnostic));
        launchd.ExitCode = 0;
        launchd.Error = new TimeoutException("launchctl hung");
        Check("F2 mac: launchctl times out: not enabled, unverified diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.UnverifiedDiagnostic));
        launchd.Error = new System.ComponentModel.Win32Exception(2, "No such file or directory");
        Check("F2 mac: launchctl can't start: not enabled, unverified diagnostic, never throws",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.UnverifiedDiagnostic));
        launchd.Error = null;
        launchd.Output = "garbage";
        Check("F2 mac: unrecognized print-disabled output: not enabled, unverified diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.UnverifiedDiagnostic));
        Check("F2 mac: ... the unverified checks are logged", log.HasEvent("startup_registration.error"));
        launchd.Output = null;
        Check("F2 mac: back to a readable state: enabled again", await registration.GetStatusAsync() == new StartupRegistrationStatus(true));

        launchd.Hang = true;
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            Check("F2 mac: a caller cancellation during launchctl is OperationCanceledException, not a status",
                await ThrowsAsync<OperationCanceledException>(() => registration.GetStatusAsync(cancel.Token)));
        launchd.Hang = false;
    }

    [SupportedOSPlatform("macos")]
    private static void MacPrintDisabledParsing()
    {
        const string header = "\n\tdisabled services = {\n\t\t\"com.docker.helper\" => enabled\n";
        const string footer = "\t}\n\n\tlogin item associations = {\n\t}\n";
        (string Output, string Expected)[] cases =
        [
            (header + footer, "NotDisabled"),
            (header + "\t\t\"com.tsiger.dialshift\" => disabled\n" + footer, "Disabled"),
            (header + "\t\t\"com.tsiger.dialshift\" => enabled\n" + footer, "NotDisabled"),
            (header + "\t\t\"com.tsiger.dialshift\" => true\n" + footer, "Disabled"),
            (header + "\t\t\"com.tsiger.dialshift\" => false\n" + footer, "NotDisabled"),
            (header + "\t\t\"com.tsiger.dialshift.helper\" => disabled\n" + footer, "NotDisabled"),
            (header + "\t\t\"xcom.tsiger.dialshift\" => disabled\n" + footer, "NotDisabled"),
            (header + "\t\t\"com.tsiger.dialshift\" => maybe\n" + footer, "Unknown"),
            (header + "\t\t\"com.tsiger.dialshift\" => enabled\n\t\t\"com.tsiger.dialshift\" => disabled\n" + footer, "Unknown"),
            ("\"com.tsiger.dialshift\" => enabled\n", "Unknown"),
            ("", "Unknown"),
        ];
        foreach (var (output, expected) in cases)
        {
            var actual = MacStartupRegistration.ParsePrintDisabled(output).ToString();
            Check($"F2 mac: print-disabled parse {output.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "", StringComparison.Ordinal)} → {expected}", actual == expected);
        }

        var both = header + "\t\t\"com.dialshift.radio\" => disabled\n\t\t\"com.tsiger.dialshift\" => enabled\n" + footer;
        Check("S1 mac: print-disabled parses the upstream v0.2.0 label on its own line",
            MacStartupRegistration.ParsePrintDisabled(both, MacStartupRegistration.LegacyLabel) == MacStartupRegistration.LaunchdState.Disabled &&
            MacStartupRegistration.ParsePrintDisabled(both) == MacStartupRegistration.LaunchdState.NotDisabled &&
            MacStartupRegistration.ParsePrintDisabled(header + footer, MacStartupRegistration.LegacyLabel) == MacStartupRegistration.LaunchdState.NotDisabled);
        Check("S1 mac: print-disabled rejects a label that isn't DialShift's",
            Throws<ArgumentOutOfRangeException>(() => MacStartupRegistration.ParsePrintDisabled(both, "com.example.other")));
    }

    /// <summary>
    /// S1: upstream v0.2.0's <c>com.dialshift.radio</c> entry is recognized, reported, and replaced or removed, so an
    /// upgrading user never ends up with two launch-at-login entries.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static async Task MacLegacyAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-legacy");
        var bundle = temp.Combine("legacy-Applications", "DialShift.app");
        var exe = CreateFile(Path.Combine(bundle, "Contents", "MacOS", "DialShift"));
        var otherBundle = temp.Combine("legacy-elsewhere", "DialShift.app");
        Directory.CreateDirectory(otherBundle);
        var launchd = new FakeLaunchctl();
        var log = new RecordingAppLog();
        var registration = NewMac(log, agents, exe, launchd);
        Check("S1 mac: the legacy plist path is <LaunchAgents>/com.dialshift.radio.plist",
            registration.LegacyPlistPath == Path.Combine(agents, "com.dialshift.radio.plist"));

        WriteUpstreamPlist(registration.LegacyPlistPath, bundle);
        Check("S1 mac: precondition: the upstream v0.2.0 plist is valid (plutil)", Lint(registration.LegacyPlistPath));
        Check("S1 mac: an upstream v0.2.0 entry for this copy is enabled, with the \"set up by an older DialShift\" diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(true, MacStartupRegistration.LegacyDiagnostic));
        Check("S1 mac: ... and the diagnostic says so", MacStartupRegistration.LegacyDiagnostic.Contains("set up by an older DialShift", StringComparison.Ordinal));
        Check("S1 mac: ... after checking launchd for the legacy label",
            launchd.Calls.Any(c => c.ArgumentList[0] == "print-disabled"));

        launchd.LegacyOverride = "disabled";
        Check("S1 mac: launchd has the legacy job disabled: not enabled, with the disabled diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.DisabledDiagnostic));
        launchd.LegacyOverride = null;

        WriteUpstreamPlist(registration.LegacyPlistPath, otherBundle);
        Check("S1 mac: an upstream v0.2.0 entry for another copy is not enabled, with the stale diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        WriteUpstreamPlist(registration.LegacyPlistPath, temp.Combine("legacy-gone", "DialShift.app"));
        Check("S1 mac: an upstream v0.2.0 entry for a deleted copy is not enabled, with the stale diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        File.WriteAllText(registration.LegacyPlistPath, "<plist><dict><key>Label</key>");
        var corrupt = await registration.GetStatusAsync();
        Check("S1 mac: a corrupt legacy plist is not enabled, with a diagnostic, never throws", !corrupt.IsEnabled && !string.IsNullOrEmpty(corrupt.DiagnosticMessage));
        WriteUpstreamPlist(registration.LegacyPlistPath, bundle, label: "com.example.other");
        var foreign = await registration.GetStatusAsync();
        Check("S1 mac: a legacy file with another label is not enabled, with a diagnostic", !foreign.IsEnabled && !string.IsNullOrEmpty(foreign.DiagnosticMessage));

        WriteUpstreamPlist(registration.LegacyPlistPath, bundle);
        Check("S1 mac: turning it off removes the upstream v0.2.0 entry: not enabled, no diagnostic",
            await registration.SetEnabledAsync(false) == new StartupRegistrationStatus(false) && !File.Exists(registration.LegacyPlistPath));

        WriteUpstreamPlist(registration.LegacyPlistPath, otherBundle);
        Check("S1 mac: turning it on writes ours and is verified without a diagnostic",
            await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        Check("S1 mac: ... and removes the legacy entry, so there is exactly one",
            Directory.GetFiles(agents).Select(Path.GetFileName).SequenceEqual(["com.tsiger.dialshift.plist"]));

        WriteUpstreamPlist(registration.LegacyPlistPath, bundle);
        Check("S1 mac: ours enabled plus a leftover legacy entry: enabled, with the leftover diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(true, MacStartupRegistration.LegacyLeftoverDiagnostic));
        Check("S1 mac: ... turning it on again removes the leftover",
            await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true) && !File.Exists(registration.LegacyPlistPath));
        WriteUpstreamPlist(registration.LegacyPlistPath, bundle);
        Check("S1 mac: ... turning it off removes both entries",
            await registration.SetEnabledAsync(false) == new StartupRegistrationStatus(false) && Directory.GetFiles(agents).Length == 0);

        // Ours exists but is stale while the legacy one still starts this copy: the one that works wins.
        CreateFile(temp.Combine("legacy-old-dev", "DialShift"));
        await NewMac(new RecordingAppLog(), agents, temp.Combine("legacy-old-dev", "DialShift")).SetEnabledAsync(true);
        WriteUpstreamPlist(registration.LegacyPlistPath, bundle);
        Check("S1 mac: a stale entry of ours plus a working legacy one: enabled, with the legacy diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(true, MacStartupRegistration.LegacyDiagnostic));
        File.Delete(registration.LegacyPlistPath);
        Check("S1 mac: ... without the legacy one, ours explains itself (stale)",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        await registration.SetEnabledAsync(false);

        WriteUpstreamPlist(registration.LegacyPlistPath, bundle);
        if (TempDirectory.TryMakeReadOnly(agents))
        {
            var failed = await registration.SetEnabledAsync(true);
            Check("S1 mac: when ours can't be written, the legacy entry is kept and still reported (enabled, with the write failure)",
                failed.IsEnabled && failed.DiagnosticMessage?.StartsWith("Couldn't turn on launch at login", StringComparison.Ordinal) == true &&
                File.Exists(registration.LegacyPlistPath) && !File.Exists(registration.PlistPath));
            var stuck = await registration.SetEnabledAsync(false);
            Check("S1 mac: when the legacy entry can't be deleted, off reports the true state with a diagnostic",
                stuck.IsEnabled && stuck.DiagnosticMessage?.StartsWith("Couldn't turn off launch at login", StringComparison.Ordinal) == true);
            File.SetUnixFileMode(agents, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else Skip("S1 mac: unwritable LaunchAgents dir with a legacy entry", "chmod 0500 does not stop writes (running as root?)");
        Check("S1 mac: the happy legacy paths log no errors except the unwritable ones",
            log.Entries.Where(e => e.EventName == "startup_registration.error").All(e => e.Message.StartsWith("Couldn't", StringComparison.Ordinal)));
    }

    /// <summary>
    /// NIT N3: after an in-place upgrade (the new app replaced the old one at the same path), entries the older apps wrote
    /// still start this copy and are enabled.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static async Task MacInPlaceUpgradeAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-inplace");
        var bundle = temp.Combine("inplace-Applications", "DialShift.app");
        var exe = CreateFile(Path.Combine(bundle, "Contents", "MacOS", "DialShift"));
        var helper = CreateFile(Path.Combine(bundle, "Contents", "MacOS", "DialShift.Mac"));
        var otherExe = CreateFile(temp.Combine("inplace-elsewhere", "DialShift.app", "Contents", "MacOS", "DialShift"));
        var registration = NewMac(new RecordingAppLog(), agents, exe);
        Directory.CreateDirectory(agents);

        // Byte-for-byte what the retired DialShift.Mac wrote (legacy-last-known-good, App.axaml.cs SetStartup).
        await File.WriteAllTextAsync(registration.PlistPath, LegacyMacPlist(exe));
        Check("N3 mac: DialShift.Mac's [<this bundle's exe>, --tray] entry is enabled, no diagnostic",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(true));
        await File.WriteAllTextAsync(registration.PlistPath, LegacyMacPlist(helper));
        Check("N3 mac: another existing executable inside this bundle resolves to the same bundle: enabled",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(true));
        await File.WriteAllTextAsync(registration.PlistPath, LegacyMacPlist(Path.Combine(bundle, "Contents", "MacOS", "Gone")));
        Check("N3 mac: an executable in this bundle that doesn't exist: stale",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        await File.WriteAllTextAsync(registration.PlistPath, LegacyMacPlist(otherExe));
        Check("N3 mac: an executable in another bundle: stale",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        await File.WriteAllTextAsync(registration.PlistPath, LegacyMacPlist("DialShift.app/Contents/MacOS/DialShift"));
        Check("N3 mac: a relative executable path: stale",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));
        await File.WriteAllTextAsync(registration.PlistPath, LegacyMacPlist(exe, "--hidden"));
        Check("N3 mac: this executable without --tray: stale",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.StaleDiagnostic));

        WriteUpstreamPlist(registration.PlistPath, bundle + "/", label: MacStartupRegistration.Label);
        Check("N3 mac: open -a with this bundle and a trailing slash: enabled",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(true));
        WriteUpstreamPlist(registration.PlistPath, Path.Combine(bundle, "Contents", ".."), label: MacStartupRegistration.Label);
        Check("N3 mac: open -a with this bundle spelled with ..: enabled",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(true));

        Check("N3 mac: turning it on again rewrites the current form",
            await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true) &&
            Strings(ReadPlist(registration.PlistPath)["ProgramArguments"]).SequenceEqual(["/usr/bin/open", "-a", bundle, "--args", "--tray"]));

        var devExe = CreateFile(temp.Combine("inplace-dev", "DialShift"));
        var dev = NewMac(new RecordingAppLog(), temp.Combine("LaunchAgents-inplace-dev"), devExe);
        Directory.CreateDirectory(dev.LaunchAgentsDirectory);
        await File.WriteAllTextAsync(dev.PlistPath, LegacyMacPlist(devExe + "/"));
        Check("N3 mac: outside a bundle only the exact form counts", (await dev.GetStatusAsync()).IsEnabled == false);
    }

    /// <summary>S3: a copy running from its App Translocation path never registers that path and never reports enabled.</summary>
    [SupportedOSPlatform("macos")]
    private static async Task MacTranslocatedAsync(TempDirectory temp)
    {
        Check("S3 mac: a translocated path is detected",
            MacAppTranslocation.IsTranslocated("/private/var/folders/xy/abc123/T/AppTranslocation/0A1B2C3D-4E5F/d/DialShift.app/Contents/MacOS/DialShift"));
        Check("S3 mac: /Applications, a lookalike folder, null and empty are not translocated",
            !MacAppTranslocation.IsTranslocated("/Applications/DialShift.app/Contents/MacOS/DialShift") &&
            !MacAppTranslocation.IsTranslocated("/Users/me/AppTranslocationNotes/DialShift.app/Contents/MacOS/DialShift") &&
            !MacAppTranslocation.IsTranslocated(null) && !MacAppTranslocation.IsTranslocated(""));

        var agents = temp.Combine("LaunchAgents-translocated");
        var bundle = temp.Combine("AppTranslocation", "0A1B2C3D-4E5F", "d", "DialShift.app");
        var exe = CreateFile(Path.Combine(bundle, "Contents", "MacOS", "DialShift"));
        var registration = NewMac(new RecordingAppLog(), agents, exe);

        Check("S3 mac: translocated, nothing registered: not enabled, no diagnostic until the user asks",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false));
        Check("S3 mac: turning it on is refused with \"Move DialShift to Applications first\"",
            await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(false, MacStartupRegistration.TranslocatedDiagnostic) &&
            MacStartupRegistration.TranslocatedDiagnostic == "Move DialShift to Applications first, then turn this on again.");
        Check("S3 mac: ... and writes nothing", !Directory.Exists(agents) || Directory.GetFiles(agents).Length == 0);

        Directory.CreateDirectory(agents);
        WriteUpstreamPlist(registration.PlistPath, bundle, label: MacStartupRegistration.Label);
        Check("S3 mac: an entry for the translocated path itself is never reported enabled",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.TranslocatedDiagnostic));
        WriteUpstreamPlist(registration.LegacyPlistPath, bundle);
        Check("S3 mac: ... nor is an upstream v0.2.0 entry for it",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false, MacStartupRegistration.TranslocatedDiagnostic));
        Check("S3 mac: a refused turn-on leaves existing entries alone",
            !(await registration.SetEnabledAsync(true)).IsEnabled && File.Exists(registration.PlistPath) && File.Exists(registration.LegacyPlistPath));
        Check("S3 mac: turning it off still works and removes both",
            await registration.SetEnabledAsync(false) == new StartupRegistrationStatus(false) && Directory.GetFiles(agents).Length == 0);
    }

    /// <summary>A launch agent in upstream v0.2.0's exact format (one line, no ProcessType), for <paramref name="bundle"/>.</summary>
    [SupportedOSPlatform("macos")]
    private static void WriteUpstreamPlist(string path, string bundle, string label = MacStartupRegistration.LegacyLabel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var escaped = System.Security.SecurityElement.Escape(bundle);
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>{label}</string><key>ProgramArguments</key><array><string>/usr/bin/open</string><string>-a</string><string>{escaped}</string><string>--args</string><string>--tray</string></array><key>RunAtLoad</key><true/></dict></plist>");
    }

    /// <summary>The retired DialShift.Mac's launch agent, as it wrote it (unescaped: its paths had no XML metacharacters).</summary>
    private static string LegacyMacPlist(string exe, string argument = "--tray") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\"><dict>\n" +
        "  <key>Label</key><string>com.tsiger.dialshift</string>\n" +
        "  <key>ProgramArguments</key><array><string>" + exe + "</string><string>" + argument + "</string></array>\n" +
        "  <key>RunAtLoad</key><true/>\n" +
        "  <key>ProcessType</key><string>Interactive</string>\n" +
        "</dict></plist>\n";

    /// <summary>The real <c>launchctl print-disabled</c> (read-only; the status call never runs <c>enable</c>).</summary>
    [SupportedOSPlatform("macos")]
    private static async Task MacRealLaunchctlAsync(TempDirectory temp)
    {
        var agents = temp.Combine("LaunchAgents-real-launchctl");
        var exe = CreateFile(temp.Combine("real-launchctl-bin", "DialShift"));
        var writer = NewMac(new RecordingAppLog(), agents, exe);
        Check("F2 mac: real launchctl precondition: a valid plist (fake launchctl)", (await writer.SetEnabledAsync(true)).IsEnabled);

        var log = new RecordingAppLog();
        var real = new MacStartupRegistration(log, agents, exe);
        var status = await real.GetStatusAsync();
        Console.WriteLine($"  real launchctl status: {status}");
        Check("F2 mac: the real launchctl print-disabled output is parsed (enabled, or disabled by a real override; never unverified)",
            status == new StartupRegistrationStatus(true) || status == new StartupRegistrationStatus(false, MacStartupRegistration.DisabledDiagnostic));
        Check("F2 mac: ... without a startup_registration.error", !log.HasEvent("startup_registration.error"));
    }

    /// <summary>Adds or replaces a boolean top-level key in a plist this class wrote (as a user or launchctl edit would).</summary>
    private static void SetPlistKey(string path, string key, string xmlValue)
    {
        var raw = File.ReadAllText(path);
        var withoutKey = System.Text.RegularExpressions.Regex.Replace(raw, $"\\s*<key>{key}</key>\\s*<(true|false)/>", "");
        File.WriteAllText(path, withoutKey.Replace("</dict>", $"  <key>{key}</key>\n  {xmlValue}\n</dict>", StringComparison.Ordinal));
    }

    /// <summary>
    /// Stands in for <c>/bin/launchctl</c>: <c>print-disabled</c> prints an override block with <see cref="Override"/>
    /// for our label and <see cref="LegacyOverride"/> for upstream v0.2.0's (none when null), <c>enable</c> sets ours to
    /// <c>enabled</c> when <see cref="EnableClears"/>.
    /// </summary>
    private sealed class FakeLaunchctl
    {
        public List<ProcessStartInfo> Calls { get; } = [];
        public string? Override { get; set; }
        public string? LegacyOverride { get; set; }
        public bool EnableClears { get; set; } = true;
        public int ExitCode { get; set; }
        public Exception? Error { get; set; }
        public string? Output { get; set; }
        public bool Hang { get; set; }

        public async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo startInfo, CancellationToken ct)
        {
            Calls.Add(startInfo);
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            if (Error != null) throw Error;
            if (startInfo.ArgumentList[0] == "enable")
            {
                if (EnableClears) Override = "enabled";
                return (0, "");
            }
            var line = (Override == null ? "" : $"\t\t\"com.tsiger.dialshift\" => {Override}\n") +
                       (LegacyOverride == null ? "" : $"\t\t\"com.dialshift.radio\" => {LegacyOverride}\n");
            return (ExitCode, Output ?? $"\n\tdisabled services = {{\n\t\t\"com.docker.helper\" => enabled\n{line}\t}}\n\n\tlogin item associations = {{\n\t}}\n");
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacBundleAsync(TempDirectory temp)
    {
        var bundle = temp.Combine("Applications", "Dial Shift.app");
        var exe = CreateFile(Path.Combine(bundle, "Contents", "MacOS", "DialShift"));
        var registration = NewMac(new RecordingAppLog(), temp.Combine("LaunchAgents-bundle"), exe);
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
        var registration = NewMac(new RecordingAppLog(), temp.Combine("LaunchAgents-escape"), exe);
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
        var old = NewMac(new RecordingAppLog(), agents, oldExe);
        Check("HS-11 mac: stale precondition: the old copy enabled itself", (await old.SetEnabledAsync(true)).IsEnabled);

        var current = NewMac(new RecordingAppLog(), agents, newExe);
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
        var registration = NewMac(log, agents, exe);
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
        var registration = NewMac(new RecordingAppLog(), agents, exe);
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

        var parentIsFile = NewMac(new RecordingAppLog(), Path.Combine(exe, "LaunchAgents"), exe);
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
            // Both keys live under the throw-away test key: the real Run and StartupApproved values are never read or written.
            var registration = new WindowsStartupRegistration(log, subkey, exe, subkey + @"\StartupApproved-basic");
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

            await WindowsStartupApprovedAsync(subkey, temp);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false);
        }
        using (var gone = Registry.CurrentUser.OpenSubKey(subkey))
            Check("HS-11 win: the test subkey was deleted", gone == null);
    }

    /// <summary>
    /// F1: Task Manager's Startup apps switch (<c>StartupApproved\Run</c>, REG_BINARY, byte 0 low bit = disabled),
    /// against a throw-away StartupApproved subkey under the test key.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task WindowsStartupApprovedAsync(string subkey, TempDirectory temp)
    {
        var runKey = subkey + @"\Run";
        var approvedKey = subkey + @"\StartupApproved";
        var exe = CreateFile(temp.Combine("approved", "DialShift.exe"));
        var registration = new WindowsStartupRegistration(new RecordingAppLog(), runKey, exe, approvedKey);
        Check("F1 win: default StartupApproved key is HKCU ...\\Explorer\\StartupApproved\\Run",
            WindowsStartupRegistration.DefaultStartupApprovedKeyPath == @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");

        Check("F1 win: no StartupApproved value: enable is verified", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        using (var key = Registry.CurrentUser.OpenSubKey(approvedKey))
            Check("F1 win: ... and nothing is written there when it was already enabled", key?.GetValue("DialShift") == null);

        foreach (var (flag, enabled) in new[] { ((byte)0x02, true), ((byte)0x06, true), ((byte)0x03, false), ((byte)0x07, false) })
        {
            SetApproved(approvedKey, [flag, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01]);
            var expected = enabled ? new StartupRegistrationStatus(true) : new StartupRegistrationStatus(false, WindowsStartupRegistration.DisabledInTaskManagerDiagnostic);
            Check($"F1 win: StartupApproved flag 0x{flag:X2}: {(enabled ? "enabled" : "turned off in Task Manager")}", await registration.GetStatusAsync() == expected);
        }

        SetApproved(approvedKey, [0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01]);
        Check("F1 win: turning it on again while Task Manager has it off is verified", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));
        using (var key = Registry.CurrentUser.OpenSubKey(approvedKey)!)
            Check("F1 win: ... by writing the enabled marker 02 00 .. 00 (REG_BINARY, 12 bytes)",
                key.GetValueKind("DialShift") == RegistryValueKind.Binary &&
                ((byte[])key.GetValue("DialShift")!).SequenceEqual(WindowsStartupRegistration.EnabledMarker.ToArray()));

        using (var key = Registry.CurrentUser.CreateSubKey(approvedKey, writable: true))
            key.SetValue("DialShift", "not binary", RegistryValueKind.String);
        var odd = await registration.GetStatusAsync();
        Check("F1 win: a StartupApproved value that isn't REG_BINARY is not enabled, with a diagnostic", !odd.IsEnabled && !string.IsNullOrEmpty(odd.DiagnosticMessage));
        SetApproved(approvedKey, []);
        Check("F1 win: an empty StartupApproved value is not enabled", !(await registration.GetStatusAsync()).IsEnabled);
        Check("F1 win: turning it on replaces an unreadable value and is verified", await registration.SetEnabledAsync(true) == new StartupRegistrationStatus(true));

        SetApproved(approvedKey, [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Check("F1 win: turning it off returns not enabled without a diagnostic", await registration.SetEnabledAsync(false) == new StartupRegistrationStatus(false));
        using (var key = Registry.CurrentUser.OpenSubKey(approvedKey)!)
            Check("F1 win: ... and removes the StartupApproved value as well", key.GetValue("DialShift") == null);

        SetApproved(approvedKey, [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Check("F1 win: a disabled StartupApproved value without a Run value is simply off (no diagnostic)",
            await registration.GetStatusAsync() == new StartupRegistrationStatus(false));
    }

    [SupportedOSPlatform("windows")]
    private static void SetApproved(string approvedKey, byte[] value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(approvedKey, writable: true);
        key.SetValue("DialShift", value, RegistryValueKind.Binary);
    }

    // ---- shared ----------------------------------------------------------------------------------------------

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        return path;
    }
}
