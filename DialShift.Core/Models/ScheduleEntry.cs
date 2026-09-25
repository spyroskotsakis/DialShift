using System.Text.Json.Serialization;

namespace DialShift.Core;

public sealed class ScheduleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StationId { get; set; }
    public string Label { get; set; } = "";
    /// <summary>
    /// Start time, canonically 24-hour <c>HH:mm</c> as written by <see cref="Scheduler.FormatTime"/>. The <c>HH.mm</c>
    /// form older builds wrote on some cultures is also accepted (<see cref="Scheduler.TryTime"/>) and is canonicalized by
    /// <see cref="SettingsStore.Load"/>; a time that does not parse makes the slot inert, never a load failure (D54).
    /// </summary>
    public string Time { get; set; } = "08:00";
    public List<DayOfWeek> Days { get; set; } = [];
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// IANA time zone id (for example <c>"Europe/Athens"</c>) that <see cref="Time"/> and <see cref="Days"/> are
    /// defined in. Null, empty or whitespace means computer-local time, which is the pre-timezone behavior (QA-B1).
    /// </summary>
    /// <remarks>
    /// Any string loads: an id this computer cannot resolve falls back to local time at evaluation
    /// (<see cref="Scheduler.TryResolveZone"/> reports <see cref="ZoneResolution.Unknown"/>); it is never a settings
    /// load failure. Adding this field does not change <see cref="Settings.Version"/> (QA-N3). Null is not written, so
    /// a schedule without zones saves exactly as before.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeZone { get; set; }
}
