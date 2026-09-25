using System.Runtime.Versioning;
using DialShift.Core.Playback;
using Microsoft.Win32;

namespace DialShift.App.Platform.Windows;

/// <summary>
/// Windows launch at login via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, value <c>DialShift</c> =
/// <c>"&lt;exe&gt;" --tray</c> (acceptance matrix §8.2.1), the same value earlier Windows releases wrote, gated by the
/// user's switch in Task Manager's Startup apps (<c>...\Explorer\StartupApproved\Run</c>, value <c>DialShift</c>).
/// </summary>
/// <remarks>
/// <para><b>Run value:</b> status is read from the registry, never from settings: enabled only when the value is a
/// string equal to what this copy of DialShift would write (paths compared case-insensitively) and that executable
/// still exists. A value that points elsewhere (moved or upgraded app) is not enabled and carries
/// <see cref="StaleDiagnostic"/>.</para>
/// <para><b>StartupApproved:</b> turning DialShift off in Task Manager → Startup apps (or Settings → Apps → Startup)
/// leaves the Run value in place and writes a <c>REG_BINARY</c> <c>DialShift</c> value under
/// <see cref="DefaultStartupApprovedKeyPath"/>; Explorer then skips the entry at sign-in. The format is not documented
/// by Microsoft. The semantics relied on here are the observed behavior of Task Manager and Settings: 12 bytes, byte 0
/// is a flag whose low bit means <i>disabled</i> (<c>0x03</c>, <c>0x07</c>) and clear means <i>enabled</i>
/// (<c>0x02</c>, <c>0x06</c>); bytes 4–11 hold the FILETIME of the last disable (zero when enabled). A missing value
/// (or a missing key) means enabled: that is the state of every Run entry that was never toggled. A value that is not a
/// non-empty <c>REG_BINARY</c> can't be interpreted and is reported as not enabled. Native confirmation: NC-04.</para>
/// <para><b>Writes:</b> <see cref="SetEnabledAsync"/> writes the Run value, then replaces any disabled or unreadable
/// StartupApproved value with <see cref="EnabledMarker"/> (what Task Manager writes when the user turns an entry back
/// on), then reads back and returns the verified state. Turning it off deletes both values. OS failures become a
/// diagnostic.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultStartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "DialShift";
    public const string StaleDiagnostic = "Launch at login points to an older copy of DialShift. Turn it on again to fix it.";
    public const string DisabledInTaskManagerDiagnostic = "Turned off in Task Manager's Startup apps. Turn it on again here to fix it.";
    private const string ApprovalUnreadableDiagnostic = "Windows' Startup apps setting for DialShift can't be read. Turn it on again to fix it.";

    /// <summary>The StartupApproved value for "enabled": flag <c>0x02</c>, no disable timestamp.</summary>
    public static ReadOnlySpan<byte> EnabledMarker => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly IAppLog log;
    private readonly string runKeyPath;
    private readonly string startupApprovedKeyPath;
    private readonly string? executablePath;

    /// <param name="log">Diagnostic log.</param>
    /// <param name="runKeyPath">Test seam (HS-11): a subkey of <c>HKEY_CURRENT_USER</c>; defaults to the real Run key.</param>
    /// <param name="executablePath">Test seam; defaults to <see cref="Environment.ProcessPath"/>.</param>
    /// <param name="startupApprovedKeyPath">Test seam (HS-11): a subkey of <c>HKEY_CURRENT_USER</c>; defaults to the
    /// real <c>StartupApproved\Run</c> key.</param>
    public WindowsStartupRegistration(IAppLog log, string runKeyPath = DefaultRunKeyPath, string? executablePath = null,
        string startupApprovedKeyPath = DefaultStartupApprovedKeyPath)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentException.ThrowIfNullOrWhiteSpace(runKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(startupApprovedKeyPath);
        this.runKeyPath = runKeyPath;
        this.startupApprovedKeyPath = startupApprovedKeyPath;
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
            using (var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true))
                key.SetValue(ValueName, expected, RegistryValueKind.String);

            // Clear a Task Manager "Disabled" (or a value we can't interpret); an enabled or missing value stays as it is.
            if (ReadApproval() != Approval.Enabled)
            {
                using var approved = Registry.CurrentUser.CreateSubKey(startupApprovedKeyPath, writable: true);
                approved.SetValue(ValueName, EnabledMarker.ToArray(), RegistryValueKind.Binary);
            }
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't write the Run or StartupApproved registry value.", ex);
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

        // Without the Run value the approval flag has no effect; removing it only avoids leaving residue behind.
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(startupApprovedKeyPath, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't delete the StartupApproved registry value.", ex);
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
        if (value is not string text || !string.Equals(text, expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(executablePath))
            return new(false, StaleDiagnostic);

        return ReadApproval() switch
        {
            Approval.Enabled => new(true),
            Approval.Disabled => new(false, DisabledInTaskManagerDiagnostic),
            _ => new(false, ApprovalUnreadableDiagnostic),
        };
    }

    private enum Approval { Enabled, Disabled, Unreadable }

    /// <summary>The Task Manager / Settings "Startup apps" switch for <see cref="ValueName"/> (see the remarks).</summary>
    private Approval ReadApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(startupApprovedKeyPath, writable: false);
            var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value switch
            {
                null => Approval.Enabled,
                byte[] { Length: > 0 } flags => (flags[0] & 0x01) != 0 ? Approval.Disabled : Approval.Enabled,
                _ => Approval.Unreadable,
            };
        }
        catch (Exception ex)
        {
            log.Warn("startup_registration.error", "Couldn't read the StartupApproved registry value.", ex);
            return Approval.Unreadable;
        }
    }

    private string? ExpectedValue() =>
        string.IsNullOrEmpty(executablePath) ? null : $"\"{Path.GetFullPath(executablePath)}\" --tray";
}
