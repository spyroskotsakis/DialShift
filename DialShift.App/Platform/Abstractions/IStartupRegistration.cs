namespace DialShift.App.Platform;

/// <summary>
/// The verified launch-at-login state as the OS sees it (not <c>Settings.LaunchAtLogin</c>).
/// <see cref="IsEnabled"/> is true only when the entry exists and targets the current executable
/// or bundle with <c>--tray</c>. Any failure or mismatch is explained in <see cref="DiagnosticMessage"/>.
/// </summary>
public sealed record StartupRegistrationStatus(
    bool IsEnabled,
    string? DiagnosticMessage = null);

/// <summary>
/// Launch-at-login registration (acceptance matrix §8.2.1). Windows: <c>HKCU\...\Run</c> value
/// <c>DialShift</c>. macOS: <c>~/Library/LaunchAgents/com.tsiger.dialshift.plist</c>, written atomically,
/// with no <c>launchctl bootstrap/bootout</c>.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>Reads the current OS registration state.</summary>
    Task<StartupRegistrationStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes, reads back and returns the verified status. Never throws for OS failures; the failure
    /// is reported through <see cref="StartupRegistrationStatus.DiagnosticMessage"/>. The caller updates
    /// <c>Settings.LaunchAtLogin</c> from the returned status and logs <c>startup_registration.result</c>.
    /// </summary>
    Task<StartupRegistrationStatus> SetEnabledAsync(bool enabled, CancellationToken ct = default);
}
