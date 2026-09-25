using System.Globalization;
using DialShift.Core;
using DialShift.Core.Playback;

namespace DialShift.App.ViewModels;

/// <summary>
/// Every user-visible text that is derived from state, in one place so the view models, the tray and the tests agree.
/// The wording is the legacy Windows and macOS apps' (tag <c>legacy-last-known-good</c>) unless noted. Dates and times
/// are formatted with the invariant culture: the UI is English, so day names match the day tabs ("Mon") and times read
/// 24-hour <c>HH:mm</c> like the slot rows and the editor, whatever the computer's culture.
/// </summary>
public static class UiText
{
    public const string AppName = "DialShift";
    public const int TrayTooltipMaxLength = 63;

    public const string DefaultTitle = "Your next favorite frequency.";
    public const string PlayLabel = "▶  Play";
    public const string PauseLabel = "Ⅱ  Pause";
    public const string SkipLabel = "Skip  →";

    public const string SettingsRecoveredTitle = "DialShift · Settings recovered";
    public const string SaveFailedTitle = "DialShift · Save failed";
    public const string StartupUpdateFailedTitle = "Couldn't update startup";
    public const string OpenFolderFailedTitle = "Couldn't open the settings folder";
    public const string UnexpectedErrorTitle = "DialShift · Something went wrong";

    public static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>The short English day name used by tabs and slot rows ("Mon").</summary>
    public static string ShortDay(DayOfWeek day) => day.ToString()[..3];

    /// <summary>Player title: the station the engine is opening or playing (BHV-25).</summary>
    public static string Title(PlaybackSnapshot snapshot) => snapshot.CurrentStationName ?? DefaultTitle;

    /// <summary>Status line, upper-cased (BHV-25).</summary>
    public static string Status(PlaybackSnapshot snapshot) => snapshot.StatusText.ToUpperInvariant();

    public static string PlayPause(bool isActive) => isActive ? PauseLabel : PlayLabel;

    /// <summary>Tray tooltip (BHV-23), truncated to the 63 characters Windows allows.</summary>
    public static string TrayTooltip(PlaybackSnapshot snapshot)
    {
        var text = $"{AppName} · {(snapshot.IsActive ? snapshot.CurrentStationName ?? "Connecting" : "Paused")}";
        return text.Length > TrayTooltipMaxLength ? text[..TrayTooltipMaxLength] : text;
    }

    /// <summary>
    /// Footer, left (BHV-29). <c>Next.At</c> is computer-local wall time. A zoned slot adds its own time and zone, with
    /// the zone's day when it differs from the local one (QA-N6): "UP NEXT · Sun 22:30  /  Jazz · Mon 01:00 Asia/Kolkata".
    /// </summary>
    public static string UpNext(bool scheduleEnabled, PlaybackSnapshot snapshot)
    {
        if (!scheduleEnabled) return "SCHEDULE OFF · You're in control";
        if (snapshot.Next is not { } next) return "SCHEDULE ON · Add your first time slot";
        var text = $"UP NEXT · {next.At.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}  /  {snapshot.NextStationName}";
        if (ZoneName(next.Entry.TimeZone) is not { } zone) return text;
        if (next.ZoneResolution == ZoneResolution.Unknown) return $"{text} · {TimeZoneChoices.UnknownZone(zone)}";
        var zoneTime = next.ZoneWall.ToString(next.ZoneWall.DayOfWeek == next.At.DayOfWeek ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture);
        return $"{text} · {zoneTime} {zone}";
    }

    /// <summary>
    /// A stored zone id as shown to the user: trimmed, as the user or another computer wrote it; null for local time.
    /// Never <c>TimeZoneInfo.Id</c>, whose spelling is whichever one .NET cached first for the zone.
    /// </summary>
    public static string? ZoneName(string? storedId) => string.IsNullOrWhiteSpace(storedId) ? null : storedId.Trim();

    /// <summary>A zoned slot row's next start on this computer (QA-N6): "Next: Sun 08:30 your time".</summary>
    public static string NextStart(DateTime at, bool enabled) =>
        $"{(enabled ? "Next" : "When enabled")}: {at.ToString("ddd HH:mm", CultureInfo.InvariantCulture)} your time";

    /// <summary>Footer, right (BHV-29): the standard name, even during DST, as today.</summary>
    public static string LocalTime(TimeZoneInfo localZone) => "LOCAL TIME · " + localZone.StandardName;

    public static string SaveFailed(string reason) => "Couldn't save your changes: " + reason;

    /// <summary>Startup-failure dialog (BHV-04, HS-08), the legacy Windows app's wording.</summary>
    public static string StartupFailed(string reason, string logFile) => $"DialShift couldn't start. {reason}\n\nDetails: {logFile}";

    public static string DeleteStationQuestion(Station station, int slotCount) =>
        $"Delete {station.Name}" + (slotCount > 0 ? $" and its {slotCount} schedule slot(s)?" : "?");

    public static string DeleteSlotQuestion(ScheduleEntry entry, string stationName) =>
        $"Delete the {entry.Time}{(ZoneName(entry.TimeZone) is { } zone ? " " + zone : "")} switch to {stationName}? It repeats on {Days(entry.Days)}.";

    /// <summary>"Mon, Wed, Fri" in week order.</summary>
    public static string Days(IEnumerable<DayOfWeek> days)
    {
        var set = days.ToHashSet();
        return string.Join(", ", Week.Where(set.Contains).Select(ShortDay));
    }
}
