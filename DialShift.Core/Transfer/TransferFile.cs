using System.Text.Json.Serialization;

namespace DialShift.Core.Transfer;

/// <summary>
/// The v1 settings transfer file (brief 5 §5, docs/settings-import-export.md): a small, versioned envelope around
/// verbatim <see cref="Station"/> and <see cref="ScheduleEntry"/> JSON. Pure data — no I/O, no path, no clock, no
/// environment (Core purity, brief 5 §7).
/// </summary>
/// <remarks>
/// <para>The envelope's own keys are snake_case (<c>schema_version</c>, <c>app_version</c>, <c>exported_utc</c>,
/// <c>schedule_enabled</c>), pinned with <see cref="JsonPropertyNameAttribute"/>. <see cref="Stations"/> and
/// <see cref="Schedule"/> serialize exactly as they do in <c>settings.json</c> — same property names and casing
/// (PascalCase, the default), same null rules (<see cref="Station.Notes"/> and <see cref="ScheduleEntry.TimeZone"/>
/// are omitted when null) — so an export → import roundtrip keeps every field with no translation layer (brief 5 §5,
/// §11 #3).</para>
/// <para>The App layer fills <see cref="AppVersion"/> (from <c>AppInfo</c>) and <see cref="ExportedUtc"/>
/// (<c>DateTimeOffset.UtcNow</c>); the codec never reads a clock (brief 5 §11 #10).</para>
/// </remarks>
public sealed record TransferFile
{
    /// <summary>The only version gate (brief 5 §5): import accepts <c>1</c> and refuses a higher value.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Informational (shown in the import confirmation); it never blocks (brief 5 §5, §11 #5).</summary>
    [JsonPropertyName("app_version")]
    public string AppVersion { get; init; } = "";

    /// <summary>When the file was written, ISO-8601 UTC and parseable, for example <c>2026-09-28T10:00:00Z</c> (brief 5 §5).</summary>
    [JsonPropertyName("exported_utc")]
    public string ExportedUtc { get; init; } = "";

    /// <summary>Restored on import (brief 5 §5); the app's <c>Settings.Version</c> is never involved.</summary>
    [JsonPropertyName("schedule_enabled")]
    public bool ScheduleEnabled { get; init; }

    /// <summary>The exported stations, verbatim (brief 5 §5). Never null; an empty list is valid.</summary>
    [JsonPropertyName("stations")]
    public List<Station> Stations { get; init; } = [];

    /// <summary>The exported schedule slots, verbatim (brief 5 §5). Never null; an empty list is valid.</summary>
    [JsonPropertyName("schedule")]
    public List<ScheduleEntry> Schedule { get; init; } = [];
}
