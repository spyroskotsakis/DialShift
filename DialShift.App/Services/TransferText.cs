using System.Globalization;

namespace DialShift.App.Services;

/// <summary>
/// The user-visible wording of the settings transfer (brief 5 §6): the success dialogs with counts and the import
/// confirmation. It lives here, in the service lane, because the transfer wording is produced only by
/// <see cref="SettingsTransferService"/> and <see cref="TransferFilePicker"/>; the failure dialogs reuse
/// <c>UiText.UnexpectedErrorTitle</c> (D112). Counts are grouped the invariant way whatever the computer's culture,
/// like <c>UiText.Count</c>.
/// </summary>
internal static class TransferText
{
    internal const string ExportCompleteTitle = "DialShift · Export complete";
    internal const string ImportCompleteTitle = "DialShift · Import complete";
    internal const string ImportConfirmTitle = "Import stations & schedule";
    internal const string ImportConfirmButton = "Import";
    internal const string ImportCancelButton = "Cancel";

    /// <summary>The Save dialog title (brief 5 §6); the buttons carry the same wording.</summary>
    internal const string ExportPickerTitle = "Export stations & schedule…";

    /// <summary>The Open dialog title (brief 5 §6).</summary>
    internal const string ImportPickerTitle = "Import stations & schedule…";

    /// <summary>"Exported N stations and M schedule slots." (brief 5 §6).</summary>
    internal static string Exported(int stations, int slots) => $"Exported {Stations(stations)} and {Slots(slots)}.";

    /// <summary>"Imported N stations and M schedule slots." plus the orphan notice when any slot was dropped (brief 5 §6).</summary>
    internal static string Imported(int stations, int slots, int droppedOrphans)
    {
        var message = $"Imported {Stations(stations)} and {Slots(slots)}.";
        return droppedOrphans > 0
            ? message + $" {Slots(droppedOrphans)} referenced missing stations and were not imported."
            : message;
    }

    /// <summary>"Replace your N stations and M schedule slots with the file's X stations and Y slots?" (brief 5 §6).</summary>
    internal static string ImportQuestion(int yourStations, int yourSlots, int fileStations, int fileSlots) =>
        $"Replace your {Stations(yourStations)} and {Slots(yourSlots)} with the file's {Stations(fileStations)} and {Slots(fileSlots)}?";

    private static string Stations(int count) => Count(count) + (count == 1 ? " station" : " stations");

    private static string Slots(int count) => Count(count) + (count == 1 ? " schedule slot" : " schedule slots");

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
