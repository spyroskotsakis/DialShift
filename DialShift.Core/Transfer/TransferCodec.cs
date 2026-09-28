using System.Text.Json;

namespace DialShift.Core.Transfer;

/// <summary>
/// The pure transfer-file codec (brief 5 §5, §7): serialization, parsing and validation of <see cref="TransferFile"/>.
/// No I/O, no file paths, no clocks and no process environment — the App layer reads and writes the bytes and passes
/// <c>app_version</c> and <c>exported_utc</c> in (brief 5 §7, Core purity).
/// </summary>
/// <remarks>
/// <para><b>Serialize</b> writes indented, human-readable JSON (brief 5 §5). <b>Parse</b> deserializes into the model.
/// <b>Validate</b> runs the full §5 refusal set over the raw JSON and returns every reason it finds, empty when the
/// file is accepted.</para>
/// <para><b>Refusals</b> (brief 5 §5): the JSON is malformed; the shape is wrong; <c>schema_version</c> ≠ 1 (a value
/// higher than <see cref="SchemaVersion"/> uses the exact text "This transfer file was made by a newer DialShift.");
/// any station has a blank <c>Name</c> or an invalid http(s) <c>Url</c> (the rules <see cref="SettingsStore.ValidUrl"/>
/// applies); or a station <c>Id</c> is duplicated.</para>
/// <para><b>Orphan slots are never a refusal</b> (brief 5 §5, §11 #6): a schedule slot whose <c>StationId</c> is not
/// among the file's stations is dropped and counted by the App layer on import, not rejected here. Import validation
/// runs fully before any mutation.</para>
/// </remarks>
public static class TransferCodec
{
    /// <summary>The only accepted <c>schema_version</c> (brief 5 §5).</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The settings.json options (SettingsStore's <c>JsonOptions</c>): indented, default naming and null rules, so the
    /// transfer file's <c>stations</c>/<c>schedule</c> entries are byte-identical to the ones <c>settings.json</c> holds
    /// (brief 5 §5, D107).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // The brief 5 §5 refusal texts. Only the newer-version reason is frozen verbatim (D109); the rest are the
    // user-facing wording shown in the import error dialog, kept in the SettingsStore/CatalogProvider sentence style.
    private const string MalformedJson = "This is not a valid DialShift transfer file: the JSON is malformed.";
    private const string NotAnObject = "This is not a valid DialShift transfer file: the top level is not a JSON object.";
    private const string SchemaVersionMissing = "schema_version is missing (expected 1).";
    private const string SchemaVersionNotAnInteger = "schema_version is not the integer 1.";
    private const string NewerDialShift = "This transfer file was made by a newer DialShift.";
    private const string EntryWrongType = "This is not a valid DialShift transfer file: an entry has the wrong type.";
    private const string EmptyStationName = "A station has an empty name.";
    private const string InvalidStationUrl = "A station has an invalid stream URL.";
    private const string DuplicateStationId = "A station id is duplicated.";

    /// <summary>Serializes <paramref name="file"/> to indented, human-readable JSON (brief 5 §5).</summary>
    public static string Serialize(TransferFile file) => JsonSerializer.Serialize(file, JsonOptions);

    /// <summary>
    /// Parses <paramref name="json"/> into a <see cref="TransferFile"/>. This is the raw deserializer; callers that
    /// need the §5 reason text call <see cref="Validate"/> first. Throws <c>System.Text.Json.JsonException</c> when the
    /// JSON is malformed or the shape is wrong (brief 5 §5).
    /// </summary>
    public static TransferFile Parse(string json) =>
        JsonSerializer.Deserialize<TransferFile>(json, JsonOptions) ?? throw new JsonException("The transfer file is empty.");

    /// <summary>
    /// Every brief 5 §5 refusal reason for the raw transfer JSON; an empty list means the file is accepted. The first
    /// reason is the text shown to the user. Performs its own parse, so malformed JSON and a wrong shape are reported
    /// here rather than thrown.
    /// </summary>
    public static IReadOnlyList<string> Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [MalformedJson];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [MalformedJson];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [NotAnObject];

            // §5/D109: schema_version is the one version gate. A value higher than this build understands is refused
            // with the exact frozen text; a missing or lower value has its own reason.
            if (!root.TryGetProperty("schema_version", out var schemaVersion)) return [SchemaVersionMissing];
            if (schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var version))
                return [SchemaVersionNotAnInteger];
            if (version > SchemaVersion) return [NewerDialShift];
            if (version < SchemaVersion) return [$"schema_version {version} is not supported (expected {SchemaVersion})."];

            // The rest of the v1 envelope: every member the format documents must be present and of the right kind.
            if (WrongKind(root, "app_version", JsonValueKind.String) is { } appVersion) return [appVersion];
            if (WrongKind(root, "exported_utc", JsonValueKind.String) is { } exportedUtc) return [exportedUtc];
            if (WrongKind(root, "schedule_enabled", JsonValueKind.True, JsonValueKind.False) is { } scheduleEnabled)
                return [scheduleEnabled];
            if (WrongKind(root, "stations", JsonValueKind.Array) is { } stations) return [stations];
            if (WrongKind(root, "schedule", JsonValueKind.Array) is { } schedule) return [schedule];

            TransferFile file;
            try
            {
                // A member of a station/schedule entry with the wrong type (a non-Guid Id, a non-string Url, days that
                // are not a list…) fails here; Validate reports it instead of letting the JsonException escape.
                file = Parse(json);
            }
            catch (JsonException)
            {
                return [EntryWrongType];
            }

            // A null entry (["stations": [null]]) deserializes to a null element; that is a wrong shape too.
            if (file.Stations.Any(s => s is null) || file.Schedule.Any(e => e is null)) return [EntryWrongType];

            // Station rules (§5): a blank Name, an invalid http(s) Url, a duplicated Id. Orphan schedule slots are NOT
            // checked here — the App layer drops and counts them on import (D110).
            var reasons = new List<string>();
            var ids = new HashSet<Guid>();
            foreach (var station in file.Stations)
            {
                if (string.IsNullOrWhiteSpace(station.Name)) reasons.Add(EmptyStationName);
                if (!SettingsStore.ValidUrl(station.Url)) reasons.Add(InvalidStationUrl);
                if (!ids.Add(station.Id)) reasons.Add(DuplicateStationId);
            }
            return reasons;
        }
    }

    /// <summary>
    /// Null when <paramref name="root"/> holds <paramref name="name"/> as one of <paramref name="kinds"/>; otherwise the
    /// wrong-shape reason for it. Envelope members are required, so a missing member is refused too.
    /// </summary>
    private static string? WrongKind(JsonElement root, string name, params JsonValueKind[] kinds) =>
        root.TryGetProperty(name, out var member) && Array.IndexOf(kinds, member.ValueKind) >= 0
            ? null
            : $"This is not a valid DialShift transfer file: \"{name}\" is missing or has the wrong type.";
}
