using DialShift.Core.Transfer;

namespace DialShift.App.Services;

/// <summary>
/// Export and import of the stations and the schedule through a versioned transfer file (brief 5 §6, §7). One
/// boundary: the caller chooses the path (a picker, or a test), this service does the file I/O, validation,
/// confirmation and the one <c>ISettingsService.CommitAsync</c> path.
/// </summary>
/// <remarks>
/// <para><b>Export</b> never touches the live <c>Settings</c>: it builds a <see cref="TransferFile"/> from the current
/// stations, schedule and <c>ScheduleEnabled</c>, fills <c>app_version</c> and <c>exported_utc</c>, and writes the file
/// atomically (temp + move, the <c>SettingsStore</c> pattern).</para>
/// <para><b>Import</b> reads and validates fully before any mutation; on confirmation it replaces the stations and the
/// schedule, drops and counts orphan slots, restores <c>schedule_enabled</c>, nulls <c>FallbackStationId</c> and
/// <c>LastStationId</c> when they no longer point at an imported station, then commits
/// <c>SettingsChange.Stations | SettingsChange.Schedule</c>. It never touches <c>Volume</c>, <c>LaunchAtLogin</c>,
/// <c>StartInTray</c>, the station catalog or <c>Settings.Version</c>.</para>
/// <para>Every failure returns <see cref="TransferOutcome.Failed"/> with the reason and changes nothing; a cancelled
/// confirmation returns <see cref="TransferOutcome.Cancelled"/> and changes nothing. Implementations never throw.</para>
/// </remarks>
public interface ISettingsTransferService
{
    /// <summary>
    /// Writes a transfer file to <paramref name="path"/> and shows the success dialog with counts. Never throws; any
    /// read/write failure is returned as <see cref="TransferOutcome.Failed"/> and shown as an error dialog.
    /// </summary>
    Task<TransferResult> ExportAsync(string path);

    /// <summary>
    /// Reads and validates <paramref name="path"/>, confirms, then replaces the stations and schedule and commits.
    /// Never throws; a validation failure is returned as <see cref="TransferOutcome.Failed"/> and shown as an error
    /// dialog, a cancelled confirmation as <see cref="TransferOutcome.Cancelled"/>.
    /// </summary>
    Task<TransferResult> ImportAsync(string path);
}
