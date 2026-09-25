using System.Runtime.Versioning;
using System.Text;
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
/// <para><b>No <c>launchctl</c></b> (open question OQ-4): <c>bootstrap</c> with <c>RunAtLoad</c> would start a second
/// instance immediately, and <c>bootout</c> could kill the running one. The entry takes effect at the next login;
/// launchd reads the directory then.</para>
/// <para><b>Status</b> is read from the file, never from settings: enabled only when the plist parses, has our label
/// and <c>RunAtLoad</c>, its program arguments equal what this copy of DialShift would write, and that target still
/// exists. Anything else is not enabled, with a diagnostic.</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacStartupRegistration : IStartupRegistration
{
    public const string Label = "com.tsiger.dialshift";
    public const string StaleDiagnostic = "Launch at login points to an older copy of DialShift. Turn it on again to fix it.";
    private const string UnexpectedDiagnostic = "The launch-at-login entry isn't in the format DialShift expects. Turn it on again to fix it.";
    private const string OpenTool = "/usr/bin/open";

    private readonly IAppLog log;
    private readonly string? executablePath;

    /// <param name="log">Diagnostic log.</param>
    /// <param name="launchAgentsDirectory">Test seam (HS-11); defaults to <c>~/Library/LaunchAgents</c>.</param>
    /// <param name="executablePath">Test seam; defaults to <see cref="Environment.ProcessPath"/>.</param>
    public MacStartupRegistration(IAppLog log, string? launchAgentsDirectory = null, string? executablePath = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        LaunchAgentsDirectory = launchAgentsDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        PlistPath = Path.Combine(LaunchAgentsDirectory, Label + ".plist");
        this.executablePath = executablePath ?? Environment.ProcessPath;
    }

    public string LaunchAgentsDirectory { get; }

    public string PlistPath { get; }

    public Task<StartupRegistrationStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ReadStatus());
    }

    public Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(enabled ? Enable() : Disable());
    }

    private StartupRegistrationStatus Enable()
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
            var actual = ReadStatus();
            return actual with { DiagnosticMessage = $"Couldn't turn on launch at login: {ex.Message}" };
        }

        var verified = ReadStatus();
        return verified.IsEnabled
            ? verified
            : verified with { DiagnosticMessage = "Launch at login was saved but couldn't be verified. " + verified.DiagnosticMessage };
    }

    private StartupRegistrationStatus Disable()
    {
        try
        {
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", $"Couldn't delete {PlistPath}.", ex);
            var actual = ReadStatus();
            return actual with { DiagnosticMessage = $"Couldn't turn off launch at login: {ex.Message}" };
        }
        return ReadStatus();
    }

    private StartupRegistrationStatus ReadStatus()
    {
        if (!File.Exists(PlistPath)) return new(false);

        List<string> arguments;
        try
        {
            var entries = ReadPlistDictionary(PlistPath);
            if (!entries.TryGetValue("Label", out var label) || (string?)label != Label ||
                !entries.TryGetValue("RunAtLoad", out var runAtLoad) || runAtLoad.Name.LocalName != "true" ||
                !entries.TryGetValue("ProgramArguments", out var programArguments) || programArguments.Name.LocalName != "array")
                return new(false, UnexpectedDiagnostic);
            arguments = programArguments.Elements().Select(e => e.Name.LocalName == "string" ? e.Value : "\0").ToList();
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
        return targetExists ? new(true) : new(false, StaleDiagnostic);
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
}
