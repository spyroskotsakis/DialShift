using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DialShift.Core;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>
/// <c>SettingsStore</c> persistence/recovery, <c>ValidUrl</c> and <c>Settings.Defaults</c>. <see cref="Legacy"/> holds the
/// original Program.cs checks (names unchanged); CT-SET-* are acceptance-matrix §7.4. Each scenario uses its own
/// sub-directory of one temp root, which is deleted when the suite passes and kept (path printed) when it fails.
/// </summary>
public static class SettingsStoreTests
{
    private static readonly Regex BackupName = new(@"^settings\.json\.unreadable-\d{17}$");

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DialShift-tests-" + Guid.NewGuid());
        Console.WriteLine("Test data: " + root);
        Legacy(Path.Combine(root, "legacy"));
        MissingFile(Path.Combine(root, "missing"));
        Recovery(root);
        ForwardCompatible(Path.Combine(root, "forward"));
        VolumeClamp(root);
        DefaultsShape();
        EmptyListsKept(Path.Combine(root, "empty"));
        SaveCreatesDirectory(Path.Combine(root, "nested", "a", "b"));
        FullRoundTrip(Path.Combine(root, "roundtrip"));
        StickyWarning(Path.Combine(root, "sticky"));
        Directory.Delete(root, recursive: true);
    }

    private static void Legacy(string directory)
    {
        Check("Unsafe URL scheme rejected", !SettingsStore.ValidUrl("file:///C:/test.mp3") && !SettingsStore.ValidUrl("javascript:alert(1)"));
        Check("HTTPS stream accepted", SettingsStore.ValidUrl("https://example.org/live?a=1"));
        var settings = Settings.Defaults();
        var store = new SettingsStore(directory);
        settings.Volume = 42; settings.Stations[0].Name = "Ελληνικό ραδιόφωνο";
        store.Save(settings);
        Check("Unicode/settings round-trip", store.Load().Stations[0].Name == "Ελληνικό ραδιόφωνο" && store.Load().Volume == 42);
        settings.Volume = 30; store.Save(settings);
        Check("Atomic overwrite succeeds", store.Load().Volume == 30 && !File.Exists(store.FilePath + ".tmp"));
        File.WriteAllText(store.FilePath, "broken-json");
        Check("Corrupt settings recovered and preserved", store.Load().Stations.Count > 0 && store.Warning != null && Directory.GetFiles(directory, "*.unreadable-*").Length == 1);
    }

    /// <summary>A valid baseline document (defaults + one Monday slot) as a mutable JSON tree.</summary>
    private static JsonObject Baseline()
    {
        var settings = Settings.Defaults();
        settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday] });
        return JsonSerializer.SerializeToNode(settings)!.AsObject();
    }

    private static SettingsStore StoreWith(string directory, JsonObject document)
    {
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(directory);
        File.WriteAllText(store.FilePath, document.ToJsonString());
        return store;
    }

    private static string[] Backups(string directory) => Directory.GetFiles(directory, "settings.json.unreadable-*");

    private static void MissingFile(string directory)
    {
        var store = new SettingsStore(directory);
        var loaded = store.Load();
        Check("CT-SET-01 missing file loads defaults without a warning", loaded.Stations.Count == 3 && store.Warning == null);
        Check("CT-SET-01 missing file: Load creates neither the file nor the directory", !File.Exists(store.FilePath) && !Directory.Exists(directory));
    }

    /// <summary>Every rejected document is preserved byte-for-byte, defaults load, and settings.json stays untouched until the next save.</summary>
    private static void Recovery(string root)
    {
        void Recovers(string id, string what, Action<JsonObject> corrupt)
        {
            var directory = Path.Combine(root, id);
            var document = Baseline();
            corrupt(document);
            var store = StoreWith(directory, document);
            var original = File.ReadAllText(store.FilePath);
            var loaded = store.Load();
            var backups = Backups(directory);
            Check($"{id} {what}: .unreadable-* backup + defaults + warning",
                backups.Length == 1 && File.ReadAllText(backups[0]) == original
                && loaded.Stations.Count == 3 && loaded.Schedule.Count == 0 && loaded.Stations[0].Name == "Groove Salad"
                && store.Warning != null && store.Warning.Contains(backups[0], StringComparison.Ordinal));
            Check($"{id} {what}: settings.json left as-is (defaults reach disk only on the next save)", File.ReadAllText(store.FilePath) == original);
        }

        Recovers("CT-SET-02", "Version 2", d => d["Version"] = 2);
        Recovers("CT-SET-05", "ftp:// station URL", d => d["Stations"]![0]!["Url"] = "ftp://example.org/stream");
        Recovers("CT-SET-06", "schedule entry with Days null", d => d["Schedule"]![0]!["Days"] = null);

        var directory = Path.Combine(root, "CT-SET-10");
        var store = StoreWith(directory, Baseline().Also(d => d["Version"] = 0));
        store.Load();
        var backups = Backups(directory);
        Check("CT-SET-10 backup name matches settings.json.unreadable-<17 digits>", backups.Length == 1 && BackupName.IsMatch(Path.GetFileName(backups[0])));
    }

    private static void ForwardCompatible(string directory)
    {
        var document = Baseline();
        document["Future"] = 1;
        document["Schedule"]![0]!["TimeZone"] = "Europe/Athens";
        var store = StoreWith(directory, document);
        var loaded = store.Load();
        Check("CT-SET-03 unknown root/entry properties are ignored (forward compatibility)",
            store.Warning == null && Backups(directory).Length == 0 && loaded.Schedule.Count == 1 && loaded.Schedule[0].Time == "08:00");
    }

    private static void VolumeClamp(string root)
    {
        int LoadVolume(string name, int volume)
        {
            var store = StoreWith(Path.Combine(root, name), Baseline().Also(d => d["Volume"] = volume));
            var loaded = store.Load();
            return store.Warning == null ? loaded.Volume : int.MinValue;
        }
        Check("CT-SET-04 Volume 150 loads as 100", LoadVolume("volume-high", 150) == 100);
        Check("CT-SET-04 Volume -5 loads as 0", LoadVolume("volume-low", -5) == 0);
    }

    private static void DefaultsShape()
    {
        var d = Settings.Defaults();
        Check("CT-SET-07 defaults: Groove Salad, Drone Zone, Secret Agent over https://ice5.somafm.com",
            d.Stations.Select(s => s.Name).SequenceEqual(["Groove Salad", "Drone Zone", "Secret Agent"])
            && d.Stations.All(s => s.Url.StartsWith("https://ice5.somafm.com/", StringComparison.Ordinal) && SettingsStore.ValidUrl(s.Url))
            && d.Stations.Select(s => s.Id).Distinct().Count() == 3 && d.Stations.All(s => s.Id != Guid.Empty));
        Check("CT-SET-07 defaults: Version 1, Volume 60, schedule off and empty, no fallback/last, no login/tray",
            d.Version == 1 && d.Volume == 60 && !d.ScheduleEnabled && d.Schedule.Count == 0
            && d.FallbackStationId == null && d.LastStationId == null && !d.LaunchAtLogin && !d.StartInTray);
    }

    private static void EmptyListsKept(string directory)
    {
        var document = Baseline();
        document["Stations"] = new JsonArray();
        document["Schedule"] = new JsonArray();
        var store = StoreWith(directory, document);
        var loaded = store.Load();
        Check("CT-SET-08 empty Stations/Schedule load as-is: no recovery, no warning, no defaults",
            loaded.Stations.Count == 0 && loaded.Schedule.Count == 0 && store.Warning == null && Backups(directory).Length == 0);
    }

    private static void SaveCreatesDirectory(string directory)
    {
        var store = new SettingsStore(directory);
        store.Save(Settings.Defaults());
        Check("CT-SET-09 Save into a missing directory creates it and leaves no .tmp",
            File.Exists(store.FilePath) && !File.Exists(store.FilePath + ".tmp") && Directory.GetFiles(directory).Length == 1);
    }

    private static void FullRoundTrip(string directory)
    {
        var settings = Settings.Defaults();
        var entry = new ScheduleEntry { StationId = settings.Stations[2].Id, Label = "Evening", Time = "21:30", Days = [DayOfWeek.Friday, DayOfWeek.Sunday], Enabled = false };
        settings.Schedule.Add(entry);
        settings.ScheduleEnabled = settings.LaunchAtLogin = settings.StartInTray = true;
        settings.FallbackStationId = settings.Stations[1].Id;
        settings.LastStationId = settings.Stations[2].Id;
        var store = new SettingsStore(directory);
        store.Save(settings);
        var loaded = store.Load();
        var e = loaded.Schedule.Single();
        Check("Round-trip preserves ids, slot fields and flags",
            loaded.Stations.Select(s => (s.Id, s.Name, s.Url, s.Tag)).SequenceEqual(settings.Stations.Select(s => (s.Id, s.Name, s.Url, s.Tag)))
            && e.Id == entry.Id && e.StationId == entry.StationId && e.Label == "Evening" && e.Time == "21:30"
            && e.Days.SequenceEqual([DayOfWeek.Friday, DayOfWeek.Sunday]) && !e.Enabled
            && loaded.ScheduleEnabled && loaded.LaunchAtLogin && loaded.StartInTray
            && loaded.FallbackStationId == settings.Stations[1].Id && loaded.LastStationId == settings.Stations[2].Id);
    }

    private static void StickyWarning(string directory)
    {
        var store = StoreWith(directory, Baseline().Also(d => d["Version"] = 2));
        store.Load();
        store.Save(Settings.Defaults());
        store.Load();
        Check("[quirk] Warning is never cleared by a later successful Load on the same store", store.Warning != null);
    }

    private static JsonObject Also(this JsonObject document, Action<JsonObject> change)
    {
        change(document);
        return document;
    }
}
