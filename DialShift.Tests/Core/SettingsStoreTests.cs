using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DialShift.Core;
using DialShift.Tests.Fakes;
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
        WarningClearedByLaterLoad(Path.Combine(root, "CF-03"));
        SameMillisecondRecoveries(Path.Combine(root, "CF-04-collision"));
        NoBackupNameFree(Path.Combine(root, "CF-04-exhausted"));
        ReadOnlyDirectory(Path.Combine(root, "CF-04-readonly"));
        DottedTimesCanonicalized(Path.Combine(root, "dotted-times"));
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
        document["Schedule"]![0]!["FutureSlotField"] = "x";
        document["Schedule"]![0]!["TimeZone"] = "Europe/Athens";
        var store = StoreWith(directory, document);
        var loaded = store.Load();
        Check("CT-SET-03 unknown root/entry properties are ignored (forward compatibility); the known TimeZone field loads beside them",
            store.Warning == null && Backups(directory).Length == 0 && loaded.Schedule.Count == 1 && loaded.Schedule[0].Time == "08:00"
            && loaded.Schedule[0].TimeZone == "Europe/Athens");
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

    private static void WarningClearedByLaterLoad(string directory)
    {
        var store = StoreWith(directory, Baseline().Also(d => d["Version"] = 2));
        store.Load();
        var recovered = store.Warning != null;
        store.Save(Settings.Defaults());
        store.Load();
        Check("CF-03 Warning is cleared by a later successful Load on the same store", recovered && store.Warning == null);
    }

    /// <summary>A fixed clock, so the backup name is known: <c>settings.json.unreadable-&lt;local yyyyMMddHHmmssfff&gt;</c>.</summary>
    private static (SettingsStore Store, string Stem) FixedClockStore(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var clock = FakeClock.AtUtc(2026, 9, 25, 10, 30);
        var store = new SettingsStore(directory, clock);
        File.WriteAllText(store.FilePath, content);
        return (store, store.FilePath + ".unreadable-" + clock.UtcNow.ToLocalTime().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void SameMillisecondRecoveries(string directory)
    {
        const string corrupt = "{ not json";
        var (store, stem) = FixedClockStore(directory, corrupt);
        File.WriteAllText(stem + "-3", "older backup");
        var first = store.Load();
        var firstWarning = store.Warning;
        var second = store.Load();
        Check("CF-04 two recoveries in the same millisecond: no exception, defaults both times",
            first.Stations.Count == 3 && second.Stations.Count == 3);
        Check("CF-04 the first copy takes settings.json.unreadable-<timestamp>, the second -2; a taken -3 is left alone",
            File.ReadAllText(stem) == corrupt && File.ReadAllText(stem + "-2") == corrupt && File.ReadAllText(stem + "-3") == "older backup"
            && Backups(directory).Length == 3);
        Check("CF-04 each warning names its own copy",
            firstWarning != null && firstWarning.EndsWith(stem + ".", StringComparison.Ordinal)
            && store.Warning != null && store.Warning.EndsWith(stem + "-2.", StringComparison.Ordinal));
        store.Load();
        Check("CF-04 a third recovery skips the taken -3 and uses -4", File.ReadAllText(stem + "-4") == corrupt && Backups(directory).Length == 4);
    }

    /// <summary>Every backup name is taken, so no copy can be made: defaults load, and Save refuses until a copy exists.</summary>
    private static void NoBackupNameFree(string directory)
    {
        const string corrupt = "{ precious but broken";
        var (store, stem) = FixedClockStore(directory, corrupt);
        var taken = Enumerable.Range(1, 100).Select(n => n == 1 ? stem : $"{stem}-{n}").ToList();
        foreach (var name in taken) File.WriteAllText(name, "taken");
        Settings? loaded = null;
        Check("CF-04 no free backup name: Load does not throw", NoThrow(() => loaded = store.Load()));
        Check("CF-04 no free backup name: defaults and a warning that says no copy was made",
            loaded!.Stations.Count == 3 && store.Warning != null && store.Warning.Contains("a copy could not be made", StringComparison.Ordinal)
            && store.Warning.Contains(store.FilePath, StringComparison.Ordinal));
        Check("CF-04 no free backup name: Save throws IOException and leaves the original untouched",
            Throws<IOException>(() => store.Save(Settings.Defaults())) && File.ReadAllText(store.FilePath) == corrupt
            && !File.Exists(store.FilePath + ".tmp") && taken.All(name => File.ReadAllText(name) == "taken"));
        File.Delete(taken[41]);
        store.Save(Settings.Defaults());
        Check("CF-04 once a name is free, Save preserves the original first and then writes",
            File.ReadAllText(taken[41]) == corrupt && store.Load().Stations.Count == 3 && store.Warning == null);
    }

    /// <summary>The backup copy fails because the folder is read-only; the original survives until a copy can be made.</summary>
    private static void ReadOnlyDirectory(string directory)
    {
        const string name = "CF-04 read-only folder";
        if (OperatingSystem.IsWindows()) { Skip(name, "Unix permission bits; Windows folders have no read-only bit for files inside"); return; }
        const string corrupt = "{ precious but broken";
        var (store, _) = FixedClockStore(directory, corrupt);
        var writable = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (CanCreateFile(directory)) { Skip(name, "the folder stays writable for this user (running as root)"); return; }
            Settings? loaded = null;
            Check($"{name}: Load does not throw", NoThrow(() => loaded = store.Load()));
            Check($"{name}: defaults, a no-copy warning, no backup file, original intact",
                loaded!.Stations.Count == 3 && store.Warning != null && store.Warning.Contains("a copy could not be made", StringComparison.Ordinal)
                && Backups(directory).Length == 0 && File.ReadAllText(store.FilePath) == corrupt);
            Check($"{name}: Save throws IOException and the original is untouched",
                Throws<IOException>(() => store.Save(Settings.Defaults())) && File.ReadAllText(store.FilePath) == corrupt);
        }
        finally
        {
            File.SetUnixFileMode(directory, writable);
        }
        store.Save(Settings.Defaults());
        var backups = Backups(directory);
        Check($"{name}: once writable, the next Save preserves the original first, then writes defaults",
            backups.Length == 1 && File.ReadAllText(backups[0]) == corrupt && store.Load().Stations.Count == 3 && store.Warning == null);
    }

    /// <summary>
    /// CT-SET-12 (D54): earlier builds wrote <c>08.30</c> on cultures whose time separator is <c>.</c>. Load canonicalizes
    /// every parseable time in memory without rewriting the file; an unparseable time loads as it is, with no recovery.
    /// </summary>
    private static void DottedTimesCanonicalized(string directory)
    {
        var document = Baseline();
        var slots = document["Schedule"]!.AsArray();
        var first = slots[0]!.AsObject();
        first["Time"] = "08.30";
        JsonObject Copy(string time) => JsonNode.Parse(first.ToJsonString())!.AsObject().Also(n => { n["Id"] = Guid.NewGuid(); n["Time"] = time; });
        slots.Add(Copy("21:05"));
        slots.Add(Copy("8.30"));
        var store = StoreWith(directory, document);
        var before = File.ReadAllText(store.FilePath);
        var loaded = store.Load();
        Check("CT-SET-12 a stored 08.30 loads as 08:30; 21:05 and an unparseable 8.30 load unchanged; no warning, no backup",
            store.Warning == null && Backups(directory).Length == 0 && loaded.Schedule.Select(e => e.Time).SequenceEqual(["08:30", "21:05", "8.30"]));
        Check("CT-SET-12 Load does not rewrite the file", File.ReadAllText(store.FilePath) == before);
        store.Save(loaded);
        Check("CT-SET-12 the next Save persists the canonical 08:30 and keeps the unparseable time as it was",
            !File.ReadAllText(store.FilePath).Contains("08.30", StringComparison.Ordinal)
            && store.Load().Schedule.Select(e => e.Time).SequenceEqual(["08:30", "21:05", "8.30"]));
    }

    private static bool CanCreateFile(string directory)
    {
        var probe = Path.Combine(directory, "probe-" + Guid.NewGuid());
        try { File.WriteAllText(probe, ""); File.Delete(probe); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private static JsonObject Also(this JsonObject document, Action<JsonObject> change)
    {
        change(document);
        return document;
    }
}
