using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DialShift.Core.Playback;

namespace DialShift.App.Platform.MacOS;

/// <summary>
/// macOS launch at login via <c>~/Library/LaunchAgents/com.tsiger.dialshift.plist</c> (acceptance matrix §8.2.1).
/// </summary>
/// <remarks>
/// <para><b>Contents:</b> <c>Label=com.tsiger.dialshift</c> (kept so existing users' entries are recognized),
/// <c>RunAtLoad=true</c>, <c>ProcessType=Interactive</c>, and <c>ProgramArguments</c> =
/// <c>["/usr/bin/open", "-a", "&lt;DialShift.app&gt;", "--args", "--tray"]</c> when running inside a <c>.app</c>
/// bundle (LaunchServices then starts the bundle as a normal GUI app, honoring <c>LSUIElement</c>), otherwise
/// <c>[&lt;executable&gt;, "--tray"]</c> for development runs.</para>
/// <para><b>Writing:</b> the XML is produced by <see cref="XmlWriter"/>, so every string (including paths containing
/// <c>&amp;</c> or <c>&lt;</c>) is escaped. The file is written to a temp file in the same directory and moved into
/// place, so a crash never leaves a truncated plist.</para>
/// <para><b>No <c>launchctl bootstrap</c> or <c>bootout</c></b> (open question OQ-4): <c>bootstrap</c> with
/// <c>RunAtLoad</c> would start a second instance immediately, and <c>bootout</c> could kill the running one. The entry
/// takes effect at the next login; launchd reads the directory then.</para>
/// <para><b>Status</b> is read from the OS, never from settings: enabled only when the plist parses, has our label and
/// <c>RunAtLoad</c>, is not marked <c>Disabled</c>, its program arguments equal what this copy of DialShift would write,
/// that target still exists, <b>and</b> launchd does not have the job disabled. Anything else is not enabled, with a
/// diagnostic.</para>
/// <para><b>launchd's disabled state</b> is read with <c>/bin/launchctl print-disabled gui/&lt;uid&gt;</c> (read-only;
/// <see cref="ProcessStartInfo.ArgumentList"/>, no shell, <see cref="LaunchctlTimeout"/>). Its output lists overrides
/// inside a <c>disabled services = {</c> block as <c>"&lt;label&gt;" =&gt; disabled|enabled</c> (verified on macOS 26)
/// or <c>=&gt; true|false</c> (older releases, where <c>true</c> means disabled). No line for the label means no
/// override, i.e. not disabled. When the check fails (launchctl can't start, non-zero exit, timeout, output without
/// that block, or an unknown value) the status is <b>not enabled</b> with <see cref="UnverifiedDiagnostic"/>: DialShift
/// never reports an "enabled" it couldn't confirm. <see cref="SetEnabledAsync"/>(true) clears a launchd override with
/// <c>launchctl enable gui/&lt;uid&gt;/com.tsiger.dialshift</c>, which only removes the override (it neither loads nor
/// starts the job), then reads back.</para>
/// <para><b>Login Items (macOS 13+):</b> the "Allow in the Background" switch in System Settings → General → Login
/// Items is stored by Background Task Management, which has no public read API. Whether switching it off shows up in
/// <c>print-disabled</c> needs native confirmation (NC-10), so the "disabled" and "unverified" diagnostics point the
/// user there (<see cref="LoginItemsHint"/>).</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class MacStartupRegistration : IStartupRegistration
{
    public const string Label = "com.tsiger.dialshift";
    public const string LaunchctlTool = "/bin/launchctl";
    public const string StaleDiagnostic = "Launch at login points to an older copy of DialShift. Turn it on again to fix it.";
    public const string LoginItemsHint = "If macOS Login Items shows DialShift as not allowed, enable it there.";
    public const string DisabledDiagnostic = "macOS has launch at login turned off for DialShift. " + LoginItemsHint + " Then turn it on again here.";
    public const string PlistDisabledDiagnostic = "The launch-at-login entry is marked as disabled. Turn it on again to fix it.";
    public const string UnverifiedDiagnostic = "DialShift couldn't check with macOS whether launch at login is allowed. " + LoginItemsHint;
    private const string UnexpectedDiagnostic = "The launch-at-login entry isn't in the format DialShift expects. Turn it on again to fix it.";
    private const string OpenTool = "/usr/bin/open";

    /// <summary>The longest wait for one <c>launchctl</c> call; after it the process is killed and the state is unverified.</summary>
    public static readonly TimeSpan LaunchctlTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex OverrideLine = new(
        "^\\s*\"" + Regex.Escape(Label) + "\"\\s*=>\\s*(?<value>\\S+)\\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly IAppLog log;
    private readonly string? executablePath;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string Output)>> runTool;
    /// <summary>The current user's launchd domain, <c>gui/&lt;uid&gt;</c>.</summary>
    private readonly string launchdDomain;

    /// <param name="log">Diagnostic log.</param>
    /// <param name="launchAgentsDirectory">Test seam (HS-11); defaults to <c>~/Library/LaunchAgents</c>.</param>
    /// <param name="executablePath">Test seam; defaults to <see cref="Environment.ProcessPath"/>.</param>
    /// <param name="runTool">Test seam (HS-11): runs a <c>launchctl</c> start info and returns its exit code and standard
    /// output. Defaults to starting the process with a <see cref="LaunchctlTimeout"/> deadline (a timeout throws
    /// <see cref="TimeoutException"/>).</param>
    public MacStartupRegistration(IAppLog log, string? launchAgentsDirectory = null, string? executablePath = null,
        Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string Output)>>? runTool = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        LaunchAgentsDirectory = launchAgentsDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        PlistPath = Path.Combine(LaunchAgentsDirectory, Label + ".plist");
        this.executablePath = executablePath ?? Environment.ProcessPath;
        this.runTool = runTool ?? RunToolAsync;
        launchdDomain = "gui/" + geteuid().ToString(CultureInfo.InvariantCulture);
    }

    public string LaunchAgentsDirectory { get; }

    public string PlistPath { get; }

    public Task<StartupRegistrationStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ReadStatusAsync(ct);
    }

    public Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return enabled ? EnableAsync(ct) : DisableAsync(ct);
    }

    private async Task<StartupRegistrationStatus> EnableAsync(CancellationToken ct)
    {
        var expected = ExpectedLaunch();
        if (expected.Diagnostic != null) return new(false, expected.Diagnostic);

        var temp = Path.Combine(LaunchAgentsDirectory, $".{Label}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(LaunchAgentsDirectory);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WritePlist(stream, expected.Arguments);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, PlistPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            log.Warn("startup_registration.error", $"Couldn't write {PlistPath}.", ex);
            var actual = await ReadStatusAsync(ct).ConfigureAwait(false);
            return actual with { DiagnosticMessage = $"Couldn't turn on launch at login: {ex.Message}" };
        }

        // The new plist has no Disabled key. A launchd override outlives the file, so it is cleared separately.
        if (await ReadLaunchdStateAsync(ct).ConfigureAwait(false) == LaunchdState.Disabled)
            await TryLaunchctlEnableAsync(ct).ConfigureAwait(false);

        var verified = await ReadStatusAsync(ct).ConfigureAwait(false);
        return verified.IsEnabled
            ? verified
            : verified with { DiagnosticMessage = "Launch at login was saved but couldn't be verified. " + verified.DiagnosticMessage };
    }

    private async Task<StartupRegistrationStatus> DisableAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", $"Couldn't delete {PlistPath}.", ex);
            var actual = await ReadStatusAsync(ct).ConfigureAwait(false);
            return actual with { DiagnosticMessage = $"Couldn't turn off launch at login: {ex.Message}" };
        }
        return await ReadStatusAsync(ct).ConfigureAwait(false);
    }

    private async Task<StartupRegistrationStatus> ReadStatusAsync(CancellationToken ct)
    {
        var file = ReadFileStatus();
        if (!file.IsEnabled) return file;
        return await ReadLaunchdStateAsync(ct).ConfigureAwait(false) switch
        {
            LaunchdState.NotDisabled => file,
            LaunchdState.Disabled => new(false, DisabledDiagnostic),
            _ => new(false, UnverifiedDiagnostic),
        };
    }

    /// <summary>The plist alone: format, <c>Disabled</c> key and target (see the remarks).</summary>
    private StartupRegistrationStatus ReadFileStatus()
    {
        if (!File.Exists(PlistPath)) return new(false);

        List<string> arguments;
        bool markedDisabled;
        try
        {
            var entries = ReadPlistDictionary(PlistPath);
            if (!entries.TryGetValue("Label", out var label) || (string?)label != Label ||
                !entries.TryGetValue("RunAtLoad", out var runAtLoad) || runAtLoad.Name.LocalName != "true" ||
                !entries.TryGetValue("ProgramArguments", out var programArguments) || programArguments.Name.LocalName != "array")
                return new(false, UnexpectedDiagnostic);
            arguments = programArguments.Elements().Select(e => e.Name.LocalName == "string" ? e.Value : "\0").ToList();
            // launchd skips a job whose plist says Disabled=true; false or no key leaves it enabled.
            markedDisabled = entries.TryGetValue("Disabled", out var disabled) && disabled.Name.LocalName == "true";
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", $"Couldn't read {PlistPath}.", ex);
            return new(false, "The launch-at-login entry can't be read. Turn it on again to fix it.");
        }

        var expected = ExpectedLaunch();
        if (expected.Diagnostic != null) return new(false, expected.Diagnostic);
        if (!arguments.SequenceEqual(expected.Arguments, StringComparer.Ordinal)) return new(false, StaleDiagnostic);
        var targetExists = expected.TargetIsBundle ? Directory.Exists(expected.Target) : File.Exists(expected.Target);
        if (!targetExists) return new(false, StaleDiagnostic);
        return markedDisabled ? new(false, PlistDisabledDiagnostic) : new(true);
    }

    internal enum LaunchdState { NotDisabled, Disabled, Unknown }

    /// <summary><c>launchctl print-disabled gui/&lt;uid&gt;</c>, parsed for <see cref="Label"/>. Caller cancellation is
    /// rethrown; every other failure is logged and gives <see cref="LaunchdState.Unknown"/>.</summary>
    private async Task<LaunchdState> ReadLaunchdStateAsync(CancellationToken ct)
    {
        (int ExitCode, string Output) result;
        try
        {
            result = await runTool(LaunchctlStartInfo("print-disabled", launchdDomain), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't run launchctl print-disabled.", ex);
            return LaunchdState.Unknown;
        }

        var state = result.ExitCode == 0 ? ParsePrintDisabled(result.Output) : LaunchdState.Unknown;
        if (state == LaunchdState.Unknown)
            log.Warn("startup_registration.error",
                $"launchctl print-disabled {launchdDomain} exited {result.ExitCode.ToString(CultureInfo.InvariantCulture)} without a readable state for {Label}.");
        return state;
    }

    /// <summary>
    /// Parses <c>print-disabled</c> output: <see cref="LaunchdState.Disabled"/> for <c>disabled</c>/<c>true</c>,
    /// <see cref="LaunchdState.NotDisabled"/> for <c>enabled</c>/<c>false</c> or no line for the label, and
    /// <see cref="LaunchdState.Unknown"/> for output without the <c>disabled services = {</c> block, an unknown value or
    /// conflicting lines.
    /// </summary>
    internal static LaunchdState ParsePrintDisabled(string output)
    {
        if (!output.Contains("disabled services = {", StringComparison.Ordinal)) return LaunchdState.Unknown;
        var matches = OverrideLine.Matches(output);
        if (matches.Count == 0) return LaunchdState.NotDisabled;
        if (matches.Count > 1) return LaunchdState.Unknown;
        return matches[0].Groups["value"].Value switch
        {
            "disabled" or "true" => LaunchdState.Disabled,
            "enabled" or "false" => LaunchdState.NotDisabled,
            _ => LaunchdState.Unknown,
        };
    }

    /// <summary><c>launchctl enable gui/&lt;uid&gt;/com.tsiger.dialshift</c>: removes the disabled override only (no load,
    /// no start). A failure is logged; the read-back that follows reports the real state.</summary>
    private async Task TryLaunchctlEnableAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, _) = await runTool(LaunchctlStartInfo("enable", launchdDomain + "/" + Label), ct).ConfigureAwait(false);
            if (exitCode != 0)
                log.Warn("startup_registration.error", $"launchctl enable {launchdDomain}/{Label} exited {exitCode.ToString(CultureInfo.InvariantCulture)}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't run launchctl enable.", ex);
        }
    }

    private static ProcessStartInfo LaunchctlStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(LaunchctlTool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    /// <summary>Starts the tool and returns its exit code and standard output. After <see cref="LaunchctlTimeout"/> it
    /// kills the process and throws <see cref="TimeoutException"/>.</summary>
    private static async Task<(int ExitCode, string Output)> RunToolAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{startInfo.FileName} could not be started.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(LaunchctlTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{startInfo.FileName} didn't finish within {LaunchctlTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception) { /* it exited in the meantime */ }
        }
    }

    private (string[] Arguments, string Target, bool TargetIsBundle, string? Diagnostic) ExpectedLaunch()
    {
        if (string.IsNullOrEmpty(executablePath))
            return ([], "", false, "DialShift couldn't determine where it is installed, so launch at login can't be set.");
        var executable = Path.GetFullPath(executablePath);
        var bundle = TryGetBundlePath(executable);
        return bundle != null
            ? ([OpenTool, "-a", bundle, "--args", "--tray"], bundle, true, null)
            : ([executable, "--tray"], executable, false, null);
    }

    /// <summary><c>…/Name.app/Contents/MacOS/exe</c> → <c>…/Name.app</c>; otherwise null.</summary>
    private static string? TryGetBundlePath(string executable)
    {
        var macOsDirectory = Path.GetDirectoryName(executable);
        var contents = Path.GetDirectoryName(macOsDirectory);
        var bundle = Path.GetDirectoryName(contents);
        return Path.GetFileName(macOsDirectory) == "MacOS" && Path.GetFileName(contents) == "Contents" &&
               bundle != null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle
            : null;
    }

    private static void WritePlist(Stream stream, IEnumerable<string> programArguments)
    {
        var document = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array", programArguments.Select(a => new XElement("string", a))),
                    new XElement("key", "RunAtLoad"), new XElement("true"),
                    new XElement("key", "ProcessType"), new XElement("string", "Interactive"))));
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using var writer = XmlWriter.Create(stream, settings);
        document.Save(writer);
    }

    private static Dictionary<string, XElement> ReadPlistDictionary(string path)
    {
        // The DOCTYPE is ignored, never fetched or processed.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        var dict = XDocument.Load(reader).Root?.Element("dict")
                   ?? throw new InvalidDataException("The plist has no top-level dict.");
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var children = dict.Elements().ToList();
        for (var i = 0; i + 1 < children.Count; i += 2)
        {
            if (children[i].Name.LocalName != "key") throw new InvalidDataException("Malformed plist dict.");
            result[children[i].Value] = children[i + 1];
        }
        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* best effort cleanup of our own temp file */ }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
    private static partial uint geteuid();
}
