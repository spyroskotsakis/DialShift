using System.Globalization;
using System.Text;
using System.Text.Json;
using DialShift.App.ViewModels;
using DialShift.Core.Playback;
using DialShift.Core.Transfer;

namespace DialShift.App.Services;

/// <summary>
/// <see cref="ISettingsTransferService"/> over explicit paths (brief 5 §5, §6, §7). The one boundary that does the
/// transfer file I/O, validation, confirmation and the single <see cref="ISettingsService.CommitAsync(SettingsChange)"/>
/// mutation path.
/// </summary>
/// <remarks>
/// <para><b>Export</b> reads the current stations, schedule and <c>ScheduleEnabled</c>, builds a
/// <see cref="TransferFile"/> with <c>app_version</c> and <c>exported_utc</c>, and writes it atomically (temp file +
/// move, the <c>SettingsStore</c> pattern). It never touches the live settings.</para>
/// <para><b>Import</b> reads and fully validates before any mutation; on confirmation it replaces the stations and the
/// schedule, drops and counts orphan slots, restores <c>schedule_enabled</c>, nulls <c>FallbackStationId</c> and
/// <c>LastStationId</c> when they no longer point at an imported station, then commits
/// <c>SettingsChange.Stations | SettingsChange.Schedule</c>. It never touches <c>Volume</c>, <c>LaunchAtLogin</c>,
/// <c>StartInTray</c>, the station catalog or <c>Settings.Version</c>.</para>
/// <para><b>Never throws:</b> every failure returns <see cref="TransferOutcome.Failed"/> with the reason and shows an
/// error dialog; a cancelled confirmation returns <see cref="TransferOutcome.Cancelled"/> with no mutation and no
/// dialog. Logs (<c>transfer.export</c>/<c>transfer.import</c>) carry counts, outcome and the validation reason only —
/// never station names or URLs (redaction rule).</para>
/// </remarks>
public sealed class SettingsTransferService(
    ISettingsService settings,
    IDialogService dialogs,
    IAppLog log,
    string appVersion) : ISettingsTransferService
{
    /// <summary>UTF-8 without a byte-order mark: the transfer file is plain, human-readable JSON (brief 5 §5).</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public async Task<TransferResult> ExportAsync(string path)
    {
        try
        {
            var current = settings.Settings;
            var file = new TransferFile
            {
                SchemaVersion = TransferCodec.SchemaVersion,
                AppVersion = appVersion,
                ExportedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ScheduleEnabled = current.ScheduleEnabled,
                Stations = [.. current.Stations],
                Schedule = [.. current.Schedule]
            };

            WriteAtomically(path, TransferCodec.Serialize(file));

            var stationCount = file.Stations.Count;
            var slotCount = file.Schedule.Count;
            log.Info("transfer.export", $"outcome=completed stations={stationCount} slots={slotCount}");
            await ShowMessageSafelyAsync(TransferText.ExportCompleteTitle, TransferText.Exported(stationCount, slotCount));
            return new TransferResult
            {
                Outcome = TransferOutcome.Completed,
                StationCount = stationCount,
                ScheduleCount = slotCount
            };
        }
        catch (Exception ex)
        {
            log.Error("transfer.export", "outcome=failed", ex);
            await ShowMessageSafelyAsync(UiText.UnexpectedErrorTitle, ex.Message);
            return new TransferResult { Outcome = TransferOutcome.Failed, ErrorReason = ex.Message };
        }
    }

    public async Task<TransferResult> ImportAsync(string path)
    {
        try
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(path, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
            {
                return await FailImportAsync(ex.Message);
            }

            // Validation runs fully before any mutation: the first reason is the one shown to the user (brief 5 §5).
            var errors = TransferCodec.Validate(json);
            if (errors.Count > 0) return await FailImportAsync(errors[0]);

            TransferFile file;
            try
            {
                file = TransferCodec.Parse(json);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return await FailImportAsync(ex.Message);
            }

            var live = settings.Settings;
            var importedIds = file.Stations.Select(station => station.Id).ToHashSet();
            var keptSlots = file.Schedule.Where(entry => importedIds.Contains(entry.StationId)).ToList();
            var droppedOrphans = file.Schedule.Count - keptSlots.Count;

            var confirmed = await dialogs.ConfirmAsync(
                TransferText.ImportConfirmTitle,
                TransferText.ImportQuestion(live.Stations.Count, live.Schedule.Count, file.Stations.Count, file.Schedule.Count),
                TransferText.ImportConfirmButton,
                TransferText.ImportCancelButton);
            if (!confirmed)
            {
                log.Info("transfer.import", "outcome=cancelled");
                return new TransferResult { Outcome = TransferOutcome.Cancelled };
            }

            // Replace both lists; drop orphan slots; clear only the dangling references; restore the schedule switch.
            live.Stations = [.. file.Stations];
            live.Schedule = keptSlots;
            live.ScheduleEnabled = file.ScheduleEnabled;
            if (live.FallbackStationId is { } fallback && !importedIds.Contains(fallback)) live.FallbackStationId = null;
            if (live.LastStationId is { } last && !importedIds.Contains(last)) live.LastStationId = null;

            await settings.CommitAsync(SettingsChange.Stations | SettingsChange.Schedule);

            log.Info("transfer.import", $"outcome=completed stations={file.Stations.Count} slots={keptSlots.Count} dropped={droppedOrphans}");
            await ShowMessageSafelyAsync(
                TransferText.ImportCompleteTitle,
                TransferText.Imported(file.Stations.Count, keptSlots.Count, droppedOrphans));
            return new TransferResult
            {
                Outcome = TransferOutcome.Completed,
                StationCount = file.Stations.Count,
                ScheduleCount = keptSlots.Count,
                DroppedOrphanCount = droppedOrphans
            };
        }
        catch (Exception ex)
        {
            // The contract says this method never throws; a failure outside the handled paths still ends here.
            log.Error("transfer.import", "outcome=failed", ex);
            await ShowMessageSafelyAsync(UiText.UnexpectedErrorTitle, ex.Message);
            return new TransferResult { Outcome = TransferOutcome.Failed, ErrorReason = ex.Message };
        }
    }

    /// <summary>Logs a refused import (counts-free reason is the §5 wording), shows the error dialog and returns <see cref="TransferOutcome.Failed"/>.</summary>
    private async Task<TransferResult> FailImportAsync(string reason)
    {
        log.Warn("transfer.import", $"outcome=failed reason={reason}");
        await ShowMessageSafelyAsync(UiText.UnexpectedErrorTitle, reason);
        return new TransferResult { Outcome = TransferOutcome.Failed, ErrorReason = reason };
    }

    /// <summary>Shows a dialog without ever letting a dialog failure escape the service (the never-throws contract).</summary>
    private async Task ShowMessageSafelyAsync(string title, string message)
    {
        try
        {
            await dialogs.ShowMessageAsync(title, message);
        }
        catch (Exception ex)
        {
            log.Warn("transfer.dialog_failed", "Couldn't show the transfer dialog.", ex);
        }
    }

    /// <summary>Writes <paramref name="json"/> as UTF-8 next to the target and moves it into place, the <c>SettingsStore</c> pattern.</summary>
    private static void WriteAtomically(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
