using System.Globalization;
using System.Text.Json;
using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Transfer;
using DialShift.Tests.Ui;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Transfer;

/// <summary>
/// The Transfer suite (brief 5 §8, docs/settings-import-export.md): the pure <see cref="TransferCodec"/> contract and the
/// real <c>SettingsTransferService</c> over the <c>UiRig</c>'s journaling settings, recording dialogs and log — the same
/// wiring the app uses, with the file path injected instead of an OS picker. Deterministic: no host clock, zone, culture or
/// audio device is consulted (QA-B2).
/// </summary>
public static class TransferTests
{
    /// <summary>The frozen §5 refusal text for a file made by a newer build (D109); asserted verbatim.</summary>
    private const string NewerDialShiftText = "This transfer file was made by a newer DialShift.";

    private const string SampleUrl = "https://ice5.somafm.com/groovesalad-128-aac";

    public static async Task RunAsync()
    {
        Roundtrip();
        Envelope();
        ValidationRefusals();
        await ExportAsync();
        await OrphansAndDanglingReferencesAsync();
        await SettingsDisciplineAsync();
        await ConfirmCancelAsync();
        await RefusedImportAsync();
        await ConfirmationCopyAsync();
    }

    // ─── Pure codec: roundtrip, null omission, byte stability (IE-01, IE-02) ───

    private static void Roundtrip()
    {
        var file = SampleFile();

        var first = TransferCodec.Serialize(file);
        var parsed = TransferCodec.Parse(first);
        var second = TransferCodec.Serialize(parsed);

        Check("Transfer IE-01 IE-02 serialize → parse → serialize is byte-stable",
            first == second && !string.IsNullOrWhiteSpace(first));

        Check("Transfer IE-01 the envelope carries the v1 keys (indented JSON, snake_case members)",
            first.Contains("\"schema_version\": 1", StringComparison.Ordinal)
            && first.Contains("\"app_version\":", StringComparison.Ordinal)
            && first.Contains("\"exported_utc\":", StringComparison.Ordinal)
            && first.Contains("\"schedule_enabled\":", StringComparison.Ordinal)
            && first.Contains("\"stations\":", StringComparison.Ordinal)
            && first.Contains("\"schedule\":", StringComparison.Ordinal)
            && first.Contains('\n'));

        // The stations/schedule entries are the verbatim settings.json shape, ids and every field preserved.
        var original = file.Stations[0];
        var reloaded = parsed.Stations.Single(s => s.Id == original.Id);
        Check("Transfer IE-02 station ids, name, url, tag and notes survive the roundtrip",
            reloaded.Name == original.Name && reloaded.Url == original.Url && reloaded.Tag == original.Tag && reloaded.Notes == original.Notes);
        var originalSlot = file.Schedule[0];
        var reloadedSlot = parsed.Schedule.Single(e => e.Id == originalSlot.Id);
        Check("Transfer IE-02 schedule ids, station id, label, time, days, enabled and time zone survive the roundtrip",
            reloadedSlot.StationId == originalSlot.StationId && reloadedSlot.Label == originalSlot.Label && reloadedSlot.Time == originalSlot.Time
            && reloadedSlot.Days.SequenceEqual(originalSlot.Days) && reloadedSlot.Enabled == originalSlot.Enabled && reloadedSlot.TimeZone == originalSlot.TimeZone);

        // A null Notes/TimeZone is omitted, exactly like settings.json (the byte-stable claim).
        var bare = TransferCodec.Serialize(new TransferFile
        {
            SchemaVersion = TransferCodec.SchemaVersion,
            AppVersion = "0.5.0",
            ExportedUtc = "2026-09-28T10:00:00Z",
            ScheduleEnabled = false,
            Stations = [new Station { Name = "Bare", Url = SampleUrl }],
            Schedule = [new ScheduleEntry { StationId = Guid.NewGuid(), Time = "08:00", Days = [DayOfWeek.Monday] }]
        });
        Check("Transfer IE-01 a null Notes and a null TimeZone appear nowhere in the JSON",
            !bare.Contains("Notes", StringComparison.Ordinal) && !bare.Contains("TimeZone", StringComparison.Ordinal));
    }

    // ─── Pure codec: the envelope (IE-01) ───

    private static void Envelope()
    {
        var file = new TransferFile
        {
            SchemaVersion = TransferCodec.SchemaVersion,
            AppVersion = "0.5.0",
            ExportedUtc = "2026-09-28T10:00:00Z",
            ScheduleEnabled = true,
            Stations = [],
            Schedule = []
        };

        var parsed = TransferCodec.Parse(TransferCodec.Serialize(file));
        Check("Transfer IE-01 app_version and schedule_enabled roundtrip through the envelope",
            parsed.AppVersion == "0.5.0" && parsed.ScheduleEnabled && parsed.SchemaVersion == TransferCodec.SchemaVersion);

        Check("Transfer IE-01 exported_utc parses as UTC (offset 0, the DateTimeOffset the service writes)",
            DateTimeOffset.TryParse(file.ExportedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            && when.Offset == TimeSpan.Zero && when.UtcDateTime == new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc));
    }

    // ─── Pure codec: every §5 refusal (IE-04) ───

    private static void ValidationRefusals()
    {
        Check("Transfer IE-04 malformed JSON is refused", TransferCodec.Validate("{ \"schema_version\": 1, ").Count > 0);
        Check("Transfer IE-04 a top-level array is refused", TransferCodec.Validate("[]").Count > 0);

        const string missingStations =
            "{\"schema_version\": 1, \"app_version\": \"0.5.0\", \"exported_utc\": \"2026-09-28T10:00:00Z\", \"schedule_enabled\": true, \"schedule\": []}";
        const string missingSchedule =
            "{\"schema_version\": 1, \"app_version\": \"0.5.0\", \"exported_utc\": \"2026-09-28T10:00:00Z\", \"schedule_enabled\": true, \"stations\": []}";
        Check("Transfer IE-04 a missing \"stations\" member is refused", TransferCodec.Validate(missingStations).Count > 0);
        Check("Transfer IE-04 a missing \"schedule\" member is refused", TransferCodec.Validate(missingSchedule).Count > 0);
        Check("Transfer IE-04 a station entry of the wrong type is refused",
            TransferCodec.Validate(Envelope(stations: "[{\"Id\": 123, \"Name\": \"x\", \"Url\": \"" + SampleUrl + "\"}]")).Count > 0);

        var version0 = TransferCodec.Validate(Envelope(schemaVersion: 0));
        Check("Transfer IE-04 schema_version 0 is refused (and not as a newer-DialShift file)", version0.Count > 0 && version0[0] != NewerDialShiftText);

        var version2 = TransferCodec.Validate(Envelope(schemaVersion: 2));
        Check("Transfer IE-04 schema_version 2 is refused with the exact text \"This transfer file was made by a newer DialShift.\"",
            version2.Count == 1 && version2[0] == NewerDialShiftText);

        Check("Transfer IE-04 a blank station Name is refused",
            TransferCodec.Validate(Envelope(stations: "[" + StationJson(Guid.NewGuid(), "   ", SampleUrl) + "]")).Count > 0);

        Check("Transfer IE-04 an invalid station Url is refused (the SettingsStore.ValidUrl rule)",
            TransferCodec.Validate(Envelope(stations: "[" + StationJson(Guid.NewGuid(), "Bad", "not-a-url") + "]")).Count > 0);

        var duplicateId = Guid.NewGuid();
        Check("Transfer IE-04 a duplicated station Id is refused",
            TransferCodec.Validate(Envelope(stations: "[" + StationJson(duplicateId, "One", SampleUrl) + ", " + StationJson(duplicateId, "Two", SampleUrl) + "]")).Count > 0);

        // A healthy file is accepted: the refusal set above is not refusing everything.
        Check("Transfer IE-04 a well-formed transfer file validates with no reasons",
            TransferCodec.Validate(TransferCodec.Serialize(SampleFile())).Count == 0);
    }

    // ─── Service-level: export (IE-01, IE-10) ───

    private static async Task ExportAsync()
    {
        await using var rig = UiRig.CreateViewModels();
        using var dir = new TempDirectory("transfer-export");
        var path = dir.Combine("export.json");
        var before = JsonSerializer.Serialize(rig.Settings);

        var result = await rig.Transfer.ExportAsync(path);

        var written = File.ReadAllText(path);
        Check("Transfer IE-01 export writes a parseable schema_version=1 transfer file with the right counts",
            result.Succeeded && result.StationCount == rig.Settings.Stations.Count && result.ScheduleCount == rig.Settings.Schedule.Count
            && TransferCodec.Validate(written).Count == 0 && TransferCodec.Parse(written).SchemaVersion == 1);

        Check("Transfer IE-07 export never mutates the live settings", JsonSerializer.Serialize(rig.Settings) == before);
        Check("Transfer IE-10 export shows the counts dialog and logs counts only (no URL, no station name)",
            rig.Recorder!.Messages.Count == 1 && rig.Recorder.Messages[0].Message == $"Exported {rig.Settings.Stations.Count} stations and {rig.Settings.Schedule.Count} schedule slots."
            && rig.Log.HasEvent("transfer.export") && rig.Log.NoEntryContains(rig.Settings.Stations[0].Url, rig.Settings.Stations[0].Name));
    }

    // ─── Service-level: orphan slots and dangling references (IE-05, IE-06, IE-11) ───

    private static async Task OrphansAndDanglingReferencesAsync()
    {
        Guid keptStationId = Guid.Empty;
        await using var rig = UiRig.CreateViewModels(seed: s =>
        {
            s.Stations =
            [
                new Station { Name = "Keep me", Url = SampleUrl }
            ];
            keptStationId = s.Stations[0].Id;
            s.FallbackStationId = Guid.NewGuid();   // not among the imported stations → must be cleared
            s.LastStationId = s.Stations[0].Id;     // among the imported stations → must be kept
            s.Schedule = [new ScheduleEntry { StationId = s.Stations[0].Id, Time = "06:00", Days = [DayOfWeek.Monday] }];
        });

        using var dir = new TempDirectory("transfer-import");
        var path = dir.Combine("import.json");
        var imported = new TransferFile
        {
            SchemaVersion = 1,
            AppVersion = "0.5.0",
            ExportedUtc = "2026-09-28T10:00:00Z",
            ScheduleEnabled = true,
            Stations =
            [
                new Station { Id = keptStationId, Name = "Keep me", Url = SampleUrl },
                new Station { Name = "Second", Url = "https://ice5.somafm.com/dronezone-128-aac" }
            ],
            Schedule =
            [
                new ScheduleEntry { StationId = keptStationId, Time = "07:00", Days = [DayOfWeek.Monday] },
                new ScheduleEntry { StationId = Guid.NewGuid(), Time = "08:00", Days = [DayOfWeek.Tuesday] } // orphan
            ]
        };
        File.WriteAllText(path, TransferCodec.Serialize(imported));
        var mark = rig.Journal.Count;

        rig.Recorder!.ConfirmAnswers.Enqueue(true);
        var result = await rig.Transfer.ImportAsync(path);

        Check("Transfer IE-05 an orphan slot is dropped and counted, never fatal",
            result.Succeeded && result.StationCount == 2 && result.ScheduleCount == 1 && result.DroppedOrphanCount == 1);
        Check("Transfer IE-06 a dangling FallbackStationId is nulled and a valid LastStationId is kept",
            rig.Settings.FallbackStationId == null && rig.Settings.LastStationId == keptStationId);
        Check("Transfer IE-02 import replaces both lists with the file's stations and schedule (ids preserved)",
            rig.Settings.Stations.Select(s => s.Id).SequenceEqual(imported.Stations.Select(s => s.Id))
            && rig.Settings.Schedule.Select(e => e.Id).SequenceEqual([imported.Schedule[0].Id])
            && rig.Settings.ScheduleEnabled);
        Check("Transfer IE-11 import commits SettingsChange.Stations | SettingsChange.Schedule and saves to disk",
            rig.Journal.Since(mark).Contains("settings.commit:" + (SettingsChange.Stations | SettingsChange.Schedule))
            && rig.OnDisk().Stations.Select(s => s.Id).SequenceEqual(imported.Stations.Select(s => s.Id)));
        var confirmation = rig.Recorder.Confirmations.Single();
        Check("Transfer IE-10 the confirmation buttons are \"Import\"/\"Cancel\" and the success dialog shows counts plus the orphan notice",
            confirmation.Confirm == "Import" && confirmation.Cancel == "Cancel"
            && rig.Recorder.Messages.Count == 1
            && rig.Recorder.Messages[0].Message == "Imported 2 stations and 1 schedule slot. 1 schedule slot referenced missing stations and were not imported.");
    }

    // ─── Service-level: Settings.Version and preferences are untouched (IE-07) ───

    private static async Task SettingsDisciplineAsync()
    {
        await using var rig = UiRig.CreateViewModels(seed: s =>
        {
            s.Volume = 35;
            s.LaunchAtLogin = true;
            s.StartInTray = true;
        });

        using var dir = new TempDirectory("transfer-discipline");
        var path = dir.Combine("import.json");
        var imported = new TransferFile
        {
            SchemaVersion = 1,
            AppVersion = "0.5.0",
            ExportedUtc = "2026-09-28T10:00:00Z",
            ScheduleEnabled = true,
            Stations = [new Station { Name = "Imported", Url = SampleUrl }],
            Schedule = []
        };
        File.WriteAllText(path, TransferCodec.Serialize(imported));

        rig.Recorder!.ConfirmAnswers.Enqueue(true);
        var result = await rig.Transfer.ImportAsync(path);

        Check("Transfer IE-07 import leaves Volume/LaunchAtLogin/StartInTray untouched and Settings.Version at 1",
            result.Succeeded && rig.Settings.Volume == 35 && rig.Settings.LaunchAtLogin && rig.Settings.StartInTray && rig.Settings.Version == 1);
        Check("Transfer IE-07 import restores schedule_enabled from the file",
            rig.Settings.ScheduleEnabled && rig.OnDisk().Version == 1 && rig.OnDisk().Volume == 35);
    }

    // ─── Service-level: a cancelled confirmation changes nothing (IE-03) ───

    private static async Task ConfirmCancelAsync()
    {
        await using var rig = UiRig.CreateViewModels();
        using var dir = new TempDirectory("transfer-cancel");
        var path = dir.Combine("import.json");
        File.WriteAllText(path, TransferCodec.Serialize(SampleFile()));
        var before = JsonSerializer.Serialize(rig.Settings);
        var mark = rig.Journal.Count;

        rig.Recorder!.ConfirmAnswers.Enqueue(false);
        var result = await rig.Transfer.ImportAsync(path);

        Check("Transfer IE-03 a cancelled confirmation mutates nothing, saves nothing and shows no success dialog",
            result.Outcome == TransferOutcome.Cancelled && JsonSerializer.Serialize(rig.Settings) == before
            && rig.Since(mark) == "" && rig.Recorder.Messages.Count == 0 && rig.Recorder.Confirmations.Count == 1);
    }

    // ─── Service-level: a refused file changes nothing and shows the reason (IE-04, IE-10) ───

    private static async Task RefusedImportAsync()
    {
        await using var rig = UiRig.CreateViewModels();
        using var dir = new TempDirectory("transfer-refused");
        var invalidUrl = dir.Combine("invalid-url.json");
        File.WriteAllText(invalidUrl, Envelope(stations: "[" + StationJson(Guid.NewGuid(), "Bad", "not-a-url") + "]"));
        var before = JsonSerializer.Serialize(rig.Settings);
        var mark = rig.Journal.Count;

        var refused = await rig.Transfer.ImportAsync(invalidUrl);
        var recorder = rig.Recorder!;

        Check("Transfer IE-04 an import with an invalid station Url fails, shows one error dialog and mutates nothing",
            refused.Outcome == TransferOutcome.Failed && !string.IsNullOrWhiteSpace(refused.ErrorReason)
            && recorder.Messages.Count == 1 && recorder.Messages[0].Title == UiText.UnexpectedErrorTitle
            && JsonSerializer.Serialize(rig.Settings) == before && rig.Since(mark) == "" && recorder.Confirmations.Count == 0);

        var newer = dir.Combine("newer.json");
        File.WriteAllText(newer, Envelope(schemaVersion: 2));
        var newerResult = await rig.Transfer.ImportAsync(newer);
        Check("Transfer IE-04 a schema_version 2 file is refused with the exact newer-DialShift text in the dialog",
            newerResult.Outcome == TransferOutcome.Failed && newerResult.ErrorReason == NewerDialShiftText
            && recorder.Messages[^1].Message == NewerDialShiftText && rig.Since(mark) == "");
    }

    // ─── Service-level: the confirmation copy matches §6 exactly (IE-03, IE-05, IE-10) ───
    // SPEC (docs/settings-import-export.md §6 + §5): the file side says "Y slots" (not "schedule slots"), and the source
    // app_version is shown on a second line as "This file was made by DialShift {appVersion}.". Deliberately asserted
    // verbatim — it must fail while the shipped copy deviates, never be weakened to match it.

    private static async Task ConfirmationCopyAsync()
    {
        // Both sides carry plural counts, so the copy is unambiguous about singular/plural: 3 your stations / 2 your
        // slots against the file's 2 stations / 2 slots.
        await using var rig = UiRig.CreateViewModels(seed: s => s.Schedule =
        [
            new ScheduleEntry { StationId = s.Stations[0].Id, Time = "06:00", Days = [DayOfWeek.Monday] },
            new ScheduleEntry { StationId = s.Stations[1].Id, Time = "07:00", Days = [DayOfWeek.Monday] }
        ]);
        using var dir = new TempDirectory("transfer-copy");
        var path = dir.Combine("import.json");
        File.WriteAllText(path, TransferCodec.Serialize(new TransferFile
        {
            SchemaVersion = 1,
            AppVersion = "0.5.0",
            ExportedUtc = "2026-09-28T10:00:00Z",
            ScheduleEnabled = true,
            Stations =
            [
                new Station { Name = "One", Url = SampleUrl },
                new Station { Name = "Two", Url = "https://ice5.somafm.com/dronezone-128-aac" }
            ],
            Schedule =
            [
                new ScheduleEntry { StationId = Guid.NewGuid(), Time = "08:00", Days = [DayOfWeek.Tuesday] },
                new ScheduleEntry { StationId = Guid.NewGuid(), Time = "09:00", Days = [DayOfWeek.Tuesday] }
            ]
        }));

        rig.Recorder!.ConfirmAnswers.Enqueue(true);
        await rig.Transfer.ImportAsync(path);

        var message = rig.Recorder.Confirmations.Single().Message;
        var question = message.Contains("Replace your 3 stations and 2 schedule slots with the file's 2 stations and 2 slots?", StringComparison.Ordinal);
        var versionLine = message.Contains("This file was made by DialShift 0.5.0.", StringComparison.Ordinal);
        if (!question || !versionLine)
            Console.WriteLine($"  the confirmation's actual copy: \"{message.Replace("\n", "\\n", StringComparison.Ordinal)}\" " +
                $"(§6 question present: {question}; app_version line present: {versionLine})");
        Check("Transfer IE-03 IE-05 IE-10 the import confirmation matches §6: the question (file side \"Y slots\") and the \"This file was made by DialShift {appVersion}.\" line",
            question && versionLine);
    }

    // ─── Fixtures ───

    private static TransferFile SampleFile()
    {
        var stationId = Guid.NewGuid();
        return new TransferFile
        {
            SchemaVersion = TransferCodec.SchemaVersion,
            AppVersion = "0.5.0",
            ExportedUtc = "2026-09-28T10:00:00Z",
            ScheduleEnabled = true,
            Stations =
            [
                new Station { Id = stationId, Name = "Groove Salad", Url = SampleUrl, Tag = "SomaFM · Ambient / downtempo", Notes = "From the catalog" },
                new Station { Name = "Bare", Url = "https://ice5.somafm.com/dronezone-128-aac" }
            ],
            Schedule =
            [
                new ScheduleEntry { StationId = stationId, Label = "Morning", Time = "07:30", Days = [DayOfWeek.Monday, DayOfWeek.Friday], Enabled = true, TimeZone = "Europe/Athens" }
            ]
        };
    }

    private static string Envelope(int schemaVersion = 1, string stations = "[]", string schedule = "[]") =>
        "{\"schema_version\": " + schemaVersion.ToString(CultureInfo.InvariantCulture)
        + ", \"app_version\": \"0.5.0\", \"exported_utc\": \"2026-09-28T10:00:00Z\", \"schedule_enabled\": true"
        + ", \"stations\": " + stations + ", \"schedule\": " + schedule + "}";

    private static string StationJson(Guid id, string name, string url) =>
        "{\"Id\": \"" + id.ToString() + "\", \"Name\": " + JsonSerializer.Serialize(name) + ", \"Url\": " + JsonSerializer.Serialize(url) + "}";
}
