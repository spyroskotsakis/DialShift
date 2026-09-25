using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DialShift.Core;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// CAT-15: <see cref="Station.Notes"/> is optional and version-safe (docs/catalog-contracts.md §6, D61, the QA-N3 recipe).
/// Everything goes through the real <see cref="SettingsStore"/> in a <see cref="TempDirectory"/>.
/// </summary>
internal static class CatalogSettingsTests
{
    /// <summary>
    /// A settings file exactly as a pre-brief build wrote it: produced by <c>SettingsStore.Save</c> of commit
    /// <c>78b122e</c> (before <c>Station.Notes</c> existed) for these values, then pasted here unchanged. It has a
    /// Greek name and an <c>&amp;</c> in a URL (both escaped by the default encoder), a zone-less and a zoned slot,
    /// and a null <c>LastStationId</c>. Line ends are the writer's <see cref="Environment.NewLine"/>; see <see cref="PreBrief"/>.
    /// </summary>
    private const string PreBriefTemplate = """
        {
          "Version": 1,
          "Stations": [
            {
              "Id": "0a4f6c2e-3b1d-4e8a-9c75-1f2e3d4c5b6a",
              "Name": "\u03A1\u03AC\u03B4\u03B9\u03BF \u0388\u03BD\u03B1",
              "Url": "https://radio.example.org/one?format=aac\u0026bitrate=128",
              "Tag": "Talk \u00B7 News"
            },
            {
              "Id": "7d9e8f10-2a3b-4c5d-8e6f-708192a3b4c5",
              "Name": "Caf\u00E9 Musique",
              "Url": "http://stream.example.net:8000/live",
              "Tag": "Internet radio"
            }
          ],
          "Schedule": [
            {
              "Id": "c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f",
              "StationId": "0a4f6c2e-3b1d-4e8a-9c75-1f2e3d4c5b6a",
              "Label": "Morning",
              "Time": "07:30",
              "Days": [
                1,
                5
              ],
              "Enabled": true
            },
            {
              "Id": "f5e4d3c2-b1a0-4f9e-8d7c-6b5a49382716",
              "StationId": "7d9e8f10-2a3b-4c5d-8e6f-708192a3b4c5",
              "Label": "",
              "Time": "21:00",
              "Days": [
                0
              ],
              "Enabled": false,
              "TimeZone": "Europe/Athens"
            }
          ],
          "Volume": 42,
          "ScheduleEnabled": true,
          "LaunchAtLogin": false,
          "StartInTray": true,
          "FallbackStationId": "7d9e8f10-2a3b-4c5d-8e6f-708192a3b4c5",
          "LastStationId": null
        }
        """;

    /// <summary>The golden bytes on this OS: System.Text.Json writes <see cref="Environment.NewLine"/>, so a pre-brief build
    /// on Windows wrote CRLF and on macOS LF. The source file's own line ends never matter.</summary>
    private static byte[] PreBrief => Encoding.UTF8.GetBytes(PreBriefTemplate.ReplaceLineEndings(Environment.NewLine));

    private const string NotesText = "Line one\nΓραμμή δύο — \"quoted\" & <b>tags</b>\ttab";

    public static void Run()
    {
        using var temp = new TempDirectory("catalog-settings");
        PreBriefFile(temp.Combine("pre-brief"));
        NotesRoundTrip(temp.Combine("notes"));
        NotesEdgeValues(temp.Combine("edges"));
        NumericNotesHazard(temp.Combine("hazard"));
    }

    private static SettingsStore StoreWith(string directory, byte[] content)
    {
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(directory);
        File.WriteAllBytes(store.FilePath, content);
        return store;
    }

    private static string[] Backups(string directory) => Directory.GetFiles(directory, "settings.json.unreadable-*");

    private static void PreBriefFile(string directory)
    {
        var store = StoreWith(directory, PreBrief);
        var loaded = store.Load();
        Check("CAT-15 a pre-brief settings file (no Notes) loads untouched: no warning, no .unreadable-* copy, every Notes null",
            store.Warning is null && Backups(directory).Length == 0 && loaded.Stations.Count == 2 && loaded.Stations.All(s => s.Notes is null));
        Check("CAT-15 the pre-brief file loads with its values (names, URLs, tags, slots, zone, flags)",
            loaded.Stations[0].Name == "Ράδιο Ένα" && loaded.Stations[0].Url == "https://radio.example.org/one?format=aac&bitrate=128"
            && loaded.Stations[1].Name == "Café Musique" && loaded.Stations[0].Tag == "Talk · News" && loaded.Schedule.Count == 2
            && loaded.Schedule[1].TimeZone == "Europe/Athens" && loaded.Volume == 42 && loaded.FallbackStationId == loaded.Stations[1].Id);
        Check("CAT-15 Load does not rewrite the pre-brief file", File.ReadAllBytes(store.FilePath).AsSpan().SequenceEqual(PreBrief));
        Check("CAT-15 Settings.Version is 1 in memory after loading the pre-brief file", loaded.Version == 1);
        store.Save(loaded);
        var saved = File.ReadAllBytes(store.FilePath);
        if (!saved.AsSpan().SequenceEqual(PreBrief)) Console.WriteLine("  saved:\n" + Encoding.UTF8.GetString(saved));
        Check("CAT-15 saving the loaded pre-brief settings is byte-for-byte identical to the pre-brief file (null Notes writes nothing)",
            saved.AsSpan().SequenceEqual(PreBrief));
        Check("CAT-15 Settings.Defaults: Version 1 and every default station without Notes",
            Settings.Defaults() is { Version: 1 } d && d.Stations.All(s => s.Notes is null) && new Station().Notes is null);
    }

    private static void NotesRoundTrip(string directory)
    {
        var store = StoreWith(directory, PreBrief);
        var settings = store.Load();
        settings.Stations[0].Notes = NotesText;
        store.Save(settings);
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        var stations = json["Stations"]!.AsArray();
        Check("CAT-15 a set Notes is written, after Tag (declaration order), as exactly the string",
            stations[0]!.AsObject().Select(p => p.Key).SequenceEqual(["Id", "Name", "Url", "Tag", "Notes"])
            && stations[0]!["Notes"]!.GetValue<string>() == NotesText);
        Check("CAT-15 a station without Notes still has no Notes key next to one that has it", !stations[1]!.AsObject().ContainsKey("Notes"));
        Check("CAT-15 Settings.Version stays 1 on disk when Notes is saved", json["Version"]!.GetValue<int>() == 1);
        var loaded = store.Load();
        Check("CAT-15 Notes round-trips through SettingsStore (string kept, null stays null, Version 1, no warning, no backup)",
            store.Warning is null && Backups(directory).Length == 0 && loaded.Version == 1
            && loaded.Stations.Select(s => s.Notes).SequenceEqual([NotesText, null]));
        loaded.Stations[0].Notes = null;
        store.Save(loaded);
        Check("CAT-15 clearing Notes back to null restores the pre-brief bytes exactly", File.ReadAllBytes(store.FilePath).AsSpan().SequenceEqual(PreBrief));
    }

    /// <summary>Values the UI never writes but a hand-edited or future file may hold: SettingsStore validates only name and URL.</summary>
    private static void NotesEdgeValues(string directory)
    {
        var store = StoreWith(directory, PreBrief);
        var settings = store.Load();
        var longNotes = string.Concat(Enumerable.Repeat("Ω notes ", 2_000));
        settings.Stations[0].Notes = "";
        settings.Stations[1].Notes = longNotes;
        store.Save(settings);
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        var loaded = store.Load();
        Check("CAT-15 an empty-string Notes is written (only null is omitted) and loads back as \"\", not null",
            json["Stations"]![0]!["Notes"]!.GetValue<string>() == "" && loaded.Stations[0].Notes == "" && store.Warning is null);
        Check("CAT-15 a 16,000-character Notes loads unchanged (no length validation)", loaded.Stations[1].Notes == longNotes);

        var explicitNull = JsonNode.Parse(Encoding.UTF8.GetString(PreBrief))!.AsObject();
        explicitNull["Stations"]![0]!["Notes"] = null;
        File.WriteAllText(store.FilePath, explicitNull.ToJsonString());
        var withNull = store.Load();
        store.Save(withNull);
        Check("CAT-15 an explicit \"Notes\": null loads as null with no warning, and the next save omits it (pre-brief bytes)",
            withNull.Stations[0].Notes is null && store.Warning is null && Backups(directory).Length == 0
            && File.ReadAllBytes(store.FilePath).AsSpan().SequenceEqual(PreBrief));

        var model = JsonSerializer.Serialize(new Station { Id = Guid.Empty, Name = "n", Url = "https://example.org/" });
        Check("CAT-15 a Station serialized directly has no Notes key when Notes is null", !model.Contains("Notes", StringComparison.Ordinal));
    }

    private static void NumericNotesHazard(string directory)
    {
        var document = JsonNode.Parse(Encoding.UTF8.GetString(PreBrief))!.AsObject();
        document["Stations"]![0]!["Notes"] = 123;
        var store = StoreWith(directory, Encoding.UTF8.GetBytes(document.ToJsonString()));
        var original = File.ReadAllBytes(store.FilePath);
        var loaded = store.Load();
        var backups = Backups(directory);
        Check("CAT-15 [hazard] \"Notes\": 123 resets to defaults with a .unreadable-* copy (D61, like CT-SET-11; pinned, not fixed)",
            backups.Length == 1 && File.ReadAllBytes(backups[0]).AsSpan().SequenceEqual(original) && loaded.Stations.Count == 3
            && loaded.Stations[0].Name == "Groove Salad" && store.Warning is not null && store.Warning.Contains(backups[0], StringComparison.Ordinal));
    }
}
