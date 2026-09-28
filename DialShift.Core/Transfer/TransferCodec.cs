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

    /// <summary>Serializes <paramref name="file"/> to indented, human-readable JSON (brief 5 §5).</summary>
    public static string Serialize(TransferFile file) => throw new NotImplementedException();

    /// <summary>
    /// Parses <paramref name="json"/> into a <see cref="TransferFile"/>. This is the raw deserializer; callers that
    /// need the §5 reason text call <see cref="Validate"/> first. Throws <c>System.Text.Json.JsonException</c> when the
    /// JSON is malformed or the shape is wrong (brief 5 §5).
    /// </summary>
    public static TransferFile Parse(string json) => throw new NotImplementedException();

    /// <summary>
    /// Every brief 5 §5 refusal reason for the raw transfer JSON; an empty list means the file is accepted. The first
    /// reason is the text shown to the user. Performs its own parse, so malformed JSON and a wrong shape are reported
    /// here rather than thrown.
    /// </summary>
    public static IReadOnlyList<string> Validate(string json) => throw new NotImplementedException();
}
