using System.Runtime.Versioning;
using DialShift.Core.Playback;
using Microsoft.Win32;

namespace DialShift.App.Platform.Windows;

/// <summary>
/// Windows launch at login via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, value <c>DialShift</c> =
/// <c>"&lt;exe&gt;" --tray</c> (acceptance matrix §8.2.1), the same value the WPF app wrote.
/// </summary>
/// <remarks>
/// Status is read from the registry, never from settings: enabled only when the value is a string equal to what this
/// copy of DialShift would write (paths compared case-insensitively) and that executable still exists. A value that
/// points elsewhere (moved or upgraded app) is not enabled and carries <see cref="StaleDiagnostic"/>.
/// <see cref="SetEnabledAsync"/> writes, reads back and returns the verified state; OS failures become a diagnostic.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "DialShift";
    public const string StaleDiagnostic = "Launch at login points to an older copy of DialShift. Turn it on again to fix it.";

    private readonly IAppLog log;
    private readonly string runKeyPath;
    private readonly string? executablePath;

    /// <param name="log">Diagnostic log.</param>
    /// <param name="runKeyPath">Test seam (HS-11): a subkey of <c>HKEY_CURRENT_USER</c>; defaults to the real Run key.</param>
    /// <param name="executablePath">Test seam; defaults to <see cref="Environment.ProcessPath"/>.</param>
    public WindowsStartupRegistration(IAppLog log, string runKeyPath = DefaultRunKeyPath, string? executablePath = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentException.ThrowIfNullOrWhiteSpace(runKeyPath);
        this.runKeyPath = runKeyPath;
        this.executablePath = executablePath ?? Environment.ProcessPath;
    }

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
        var expected = ExpectedValue();
        if (expected == null) return new(false, "DialShift couldn't determine where it is installed, so launch at login can't be set.");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
            key.SetValue(ValueName, expected, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't write the Run registry value.", ex);
            return ReadStatus() with { DiagnosticMessage = $"Couldn't turn on launch at login: {ex.Message}" };
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
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't delete the Run registry value.", ex);
            return ReadStatus() with { DiagnosticMessage = $"Couldn't turn off launch at login: {ex.Message}" };
        }
        return ReadStatus();
    }

    private StartupRegistrationStatus ReadStatus()
    {
        object? value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
            value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't read the Run registry value.", ex);
            return new(false, "The launch-at-login entry can't be read. Turn it on again to fix it.");
        }

        if (value == null) return new(false);
        var expected = ExpectedValue();
        if (expected == null) return new(false, "DialShift couldn't determine where it is installed.");
        return value is string text && string.Equals(text, expected, StringComparison.OrdinalIgnoreCase) && File.Exists(executablePath)
            ? new(true)
            : new(false, StaleDiagnostic);
    }

    private string? ExpectedValue() =>
        string.IsNullOrEmpty(executablePath) ? null : $"\"{Path.GetFullPath(executablePath)}\" --tray";
}
