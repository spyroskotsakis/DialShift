using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DialShift.App;
using DialShift.App.Platform;
using DialShift.App.ViewModels;
using DialShift.App.Views.Dialogs;
using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.Headless;
using static DialShift.Tests.Ui.HeadlessUiTests;

namespace DialShift.Tests.Ui;

/// <summary>
/// Brief 2 Phase 2 UI: the per-slot time zone in the slot editor, the schedule rows, the footer and the composition root.
/// View-model checks first (picker list, search, "Local time" sentinel, Windows ids, UP NEXT, log wiring), then headless
/// checks through the real views and dialogs (round trip, migration, validation, unknown zones, cross-day rows, conflicts,
/// the compact layout, the App's own tray). Each check is named with its brief 2 row or QA id.
/// </summary>
/// <remarks>
/// Machine-independent (QA-B2): every scenario injects <see cref="UiRig.DefaultNow"/> (Monday 2026-09-14 10:00 UTC) and the
/// computer's zone; nothing reads <see cref="TimeZoneInfo.Local"/>. IANA ids resolve on macOS and, through ICU, on Windows.
/// The picker offers IANA ids on both OSes (on Windows every id CLDR maps to each Windows zone), so "athens" picks
/// Europe/Athens everywhere; the full lists still differ (Windows reaches fewer ids), and the checks print their size.
/// </remarks>
public static class TimeZoneUiTests
{
    private static readonly DateTimeOffset Now = UiRig.DefaultNow;

    public static async Task RunAsync()
    {
        await LocalTimeSentinel();
        PickerListAndSearch();
        WindowsIdsBecomeIana();
        WindowsZoneCoverage();
        await UpNextShowsTheZone();
        await SchedulerLogWiring();
        await Headless.RunAsync(RoundTripThroughThePicker);
        await Headless.RunAsync(MigrationKeepsStoredValues);
        await Headless.RunAsync(NoMatchBlocksSave);
        await Headless.RunAsync(UnknownZoneRow);
        await Headless.RunAsync(CrossDayRowAndFooter);
        await Headless.RunAsync(ZoneAwareConflicts);
        await Headless.RunAsync(CompactZonedLayout);
        await Headless.RunAsync(AppTrayAfterZoneEdits);
    }

    // ─── helpers ───

    private static bool Available(params string[] ids) => ids.All(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _));

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    /// <summary>The system list the picker offers now (the editor builds the same one).</summary>
    private static IReadOnlyList<TimeZoneOption> SystemList() => TimeZoneChoices.Build(Now, null, out _);

    /// <summary>The single picker entry "athens" finds: Europe/Athens on every OS (checked in <see cref="PickerListAndSearch"/>).</summary>
    private static TimeZoneOption AthensPick() => SystemList().Single(o => o.Matches("athens"));

    private static ScheduleEditorViewModel Editor(Settings settings, ScheduleEntry? original = null) =>
        new(settings, original, DayOfWeek.Monday, Now, new RecordingDialogService(), ex => throw ex);

    private static AutoCompleteBox Picker(Window editor) => ByName<AutoCompleteBox>(editor, ScheduleEditorViewModel.TimeZoneLabel);

    private static TextBox PickerText(Window editor) => Find<TextBox>(Picker(editor)).Single();

    private static ScheduleEditorViewModel Model(Window editor) => (ScheduleEditorViewModel)editor.DataContext!;

    /// <summary>Types <paramref name="query"/> in the picker, then Down and Enter, as a keyboard user chooses a zone.</summary>
    private static async Task ChooseZoneAsync(Window editor, string query)
    {
        await TypeAsync(PickerText(editor), query);
        await PressAsync(editor, Key.Down);
        await PressAsync(editor, Key.Enter);
    }

    private static async Task<ScheduleEditorDialog> OpenAsync(Func<Task> open)
    {
        var count = OpenedWindows.Count;
        await open();
        return await WaitForWindowAsync<ScheduleEditorDialog>(count);
    }

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static Color Resource(Visual anchor, string key) =>
        ((StyledElement)anchor).TryFindResource(key, out var value) && value is ISolidColorBrush brush ? brush.Color : throw new KeyNotFoundException(key);

    private static Border ZoneTag(Visual root, ScheduleEntry entry) =>
        Find<Border>(root).Single(b => b.Classes.Contains("zoneTag") && b.DataContext is SlotRowViewModel row && row.Entry.Id == entry.Id && b.IsEffectivelyVisible);

    private static TextBlock NextStartLine(Visual root, ScheduleEntry entry) =>
        Find<TextBlock>(root).Single(t => t.DataContext is SlotRowViewModel row && row.Entry.Id == entry.Id && t.Parent is WrapPanel);

    /// <summary>The <c>TimeZone</c> values of settings.json exactly as written (null when the key is absent), by slot id.</summary>
    private static Dictionary<Guid, string?> RawZones(string settingsFile) =>
        JsonNode.Parse(File.ReadAllText(settingsFile))!["Schedule"]!.AsArray()
            .ToDictionary(e => e!["Id"]!.GetValue<Guid>(), e => e!.AsObject().TryGetPropertyValue("TimeZone", out var z) ? z?.GetValue<string>() : null);

    // ─── "Local time" sentinel (brief 2 §4.5) ───

    private static async Task LocalTimeSentinel()
    {
        await using var rig = UiRig.CreateViewModels();
        var rec = rig.Recorder!;
        rec.ScheduleScripts.Enqueue(editor =>
        {
            Check("§4.5 \"Local time\" sentinel: a new slot's picker starts on \"Local time\", the list's first entry, with no id",
                editor.SelectedTimeZone == editor.TimeZones[0] && editor.SelectedTimeZone is { Id: null, Name: TimeZoneChoices.LocalLabel, Label: TimeZoneChoices.LocalLabel }
                && !editor.IsTimeZoneUnknown && editor.TimeZoneHint == "The start time and days follow this computer's clock.");
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await rig.ViewModel.Schedule.AddCommand.ExecuteAsync();
        var saved = rig.Settings.Schedule.Single();
        Check("§4.5 QA-B1 saving with \"Local time\" stores null, and settings.json has no TimeZone key (a zone-less schedule saves as before)",
            saved.TimeZone == null && rig.OnDisk().Schedule.Single().TimeZone == null && !File.ReadAllText(rig.Paths.SettingsFile).Contains("TimeZone", StringComparison.Ordinal));

        var athens = AthensPick();
        saved.TimeZone = athens.Id;
        rig.ViewModel.Schedule.Refresh();
        rec.ScheduleScripts.Enqueue(editor =>
        {
            Check($"§4.5 editing a {athens.Name} slot selects its list entry", editor.SelectedTimeZone?.Id == athens.Id && !editor.IsTimeZoneUnknown);
            editor.SelectedTimeZone = editor.TimeZones[0];
            Check("§4.5 choosing \"Local time\" updates the hint", editor.TimeZoneHint == "The start time and days follow this computer's clock.");
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await rig.ViewModel.Schedule.Slots.Single().EditCommand.ExecuteAsync();
        Check("§4.5 switching a zoned slot back to \"Local time\" stores null (the TimeZone key disappears from settings.json)",
            rig.Settings.Schedule.Single().TimeZone == null && RawZones(rig.Paths.SettingsFile).Values.Single() == null);
    }

    // ─── Picker list and search (§4.5, QA-B4) ───

    private static void PickerListAndSearch()
    {
        var editor = Editor(Settings.Defaults());
        var zones = editor.TimeZones;
        Check("§4.5 the picker lists \"Local time\" first, then the system zones by current offset (Pacific/… before Europe/…)",
            zones[0].Id == null && zones.Count > 100 && zones.Skip(1).All(z => z.Id != null && z.Resolution == ZoneResolution.Resolved)
            && zones.Skip(1).Select(z => z.UtcOffset).SequenceEqual(zones.Skip(1).Select(z => z.UtcOffset).Order()));
        Check("QA-B4 every picker entry is an IANA id, never a Windows id (on this OS's real zone list)",
            zones.Skip(1).All(z => !TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id!, out _)) && zones.Select(z => z.Id).Distinct().Count() == zones.Count);

        List<TimeZoneOption> Search(string query) => zones.Where(z => editor.MatchesTimeZone(query, z)).ToList();
        var athens = Search("athens");
        Check($"§4.5 search \"athens\" finds exactly Europe/Athens on every OS (found {athens.Count}: {string.Join(", ", athens.Select(z => z.Id))}; list has {zones.Count - 1} zones)",
            athens.Select(z => z.Id).SequenceEqual(["Europe/Athens"]) && Search("ATHENS").SequenceEqual(athens));
        Check("UI-D3 §4.5 search \"bucharest\" finds only Europe/Bucharest (a Windows name \"Athens, Bucharest\" names each city on its own entry)",
            Search("bucharest").Select(z => z.Id).SequenceEqual(["Europe/Bucharest"]));
        Check("§4.5 search \"new york\" (a space for the underscore) finds America/New_York",
            Search("new york").Any(z => z.Id == "America/New_York") && Search("new_york").Any(z => z.Id == "America/New_York"));
        var plus2 = Search("UTC+2");
        Check($"§4.5 search \"UTC+2\" finds the zones at UTC+02:00 now, and only those ({plus2.Count})",
            plus2.Count > 0 && plus2.All(z => z.Detail == "UTC+02:00") && Search("UTC+02:00").Any(z => z.Detail == "UTC+02:00"));
        Check("§4.5 search \"UTC+5:30\" finds Asia/Kolkata (a non-whole-hour offset)", Search("UTC+5:30").Any(z => z.Id == "Asia/Kolkata"));
        var la = Search("pacific us").Select(z => z.Id).ToList();
        Check($"UI-D3 §4.5 search \"pacific us\" finds America/Los_Angeles on every OS (the country, not only the Windows name; found {la.Count}: {string.Join(", ", la)})",
            la.Contains("America/Los_Angeles"));
        Check("UI-D3 §4.5 search by country (region mapping): \"greece\" finds Europe/Athens, \"finland\" finds Europe/Helsinki",
            Search("greece").Select(z => z.Id).SequenceEqual(["Europe/Athens"]) && Search("finland").Select(z => z.Id).SequenceEqual(["Europe/Helsinki"]));
        const string countryOfDefaults = "UI-D3 §4.5 search by country (zone.tab): \"united states\" finds America/New_York and America/Los_Angeles, \"romania\" finds Europe/Bucharest";
        if (OperatingSystem.IsWindows())
            Skip(countryOfDefaults, "Windows has no zone.tab; a Windows zone's default id (America/New_York for Eastern) gets no country from the region mapping, only its Windows name (\"Eastern Time (US & Canada)\")");
        else
            Check(countryOfDefaults, Search("united states").Select(z => z.Id).Intersect(["America/New_York", "America/Los_Angeles"]).Count() == 2
                && Search("romania").Select(z => z.Id).SequenceEqual(["Europe/Bucharest"]));
        Check("UI-D3 §4.5 search by Windows id: \"gtb standard\" finds Europe/Athens and Europe/Bucharest, \"romance\" finds Europe/Paris",
            Search("gtb standard").Select(z => z.Id).Intersect(["Europe/Athens", "Europe/Bucharest"]).Count() == 2 && Search("romance").Any(z => z.Id == "Europe/Paris"));
        Check("§4.5 search \"local\" finds the \"Local time\" entry", Search("local").Contains(zones[0]));
        Check("§4.5 an empty search, and the exact label of the current choice, show the whole list",
            Search("").Count == zones.Count && Search("  ").Count == zones.Count && Search(athens[0].Label).Count == zones.Count);
        Check("§4.5 a word that matches nothing leaves the list empty", Search("atlantis").Count == 0 && Search("athens zzz").Count == 0);

        editor.SelectedTimeZone = null;
        var focus = new List<string>();
        editor.FocusRequested += (_, field) => focus.Add(field);
        editor.SaveCommand.Execute(null);
        Check("§4.5 no zone chosen (the text matched nothing): Save is blocked with \"Choose a time zone from the list, or Local time.\" and focuses the picker",
            editor.Error == "Choose a time zone from the list, or Local time." && focus.SequenceEqual([nameof(ScheduleEditorViewModel.SelectedTimeZone)])
            && editor.Result == EditorResult.Cancelled && editor.TimeZoneHint == "Choose a time zone from the list, or Local time.");
    }

    // ─── QA-B4: Windows ids become every IANA id mapped to them; a stored id is selected as it is ───

    private static void WindowsIdsBecomeIana()
    {
        TimeZoneInfo Windows(string id, int hours, string display) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(hours), display, id);
        TimeZoneInfo[] windowsZones =
        [
            Windows("GTB Standard Time", 2, "(UTC+02:00) Athens, Bucharest"),
            Windows("Pacific Standard Time", -8, "(UTC-08:00) Pacific Time (US & Canada)"),
            Windows("Eastern Standard Time", -5, "(UTC-05:00) Eastern Time (US & Canada)"),
            Windows("Romance Standard Time", 1, "(UTC+01:00) Brussels, Copenhagen, Madrid, Paris")
        ];
        var options = TimeZoneChoices.Build(windowsZones, Now, "Europe/Athens", out var selected);
        var ids = options.Select(o => o.Id).ToList();
        Console.WriteLine($"  from 4 Windows ids, {ids.Count - 1} IANA ids: " + string.Join(", ", ids.Select(id => id ?? "(local)")));
        Check("QA-B4 a Windows zone is listed under every IANA id mapped to it (GTB → Europe/Bucharest, Europe/Athens, Asia/Nicosia; Pacific → America/Los_Angeles, America/Vancouver; Romance → Europe/Paris, Europe/Brussels, …)",
            new[] { "Europe/Bucharest", "Europe/Athens", "Asia/Nicosia", "America/Los_Angeles", "America/Vancouver", "America/New_York", "America/Toronto", "Europe/Paris", "Europe/Brussels", "Europe/Madrid" }
                .All(ids.Contains)
            && ids.Distinct().Count() == ids.Count && ids.Skip(1).All(id => TimeZoneInfo.TryFindSystemTimeZoneById(id!, out _)));
        Check("QA-B4 no Windows id is offered, so none can be persisted by choosing an entry",
            ids.All(id => id == null || (!id.Contains("Standard Time", StringComparison.Ordinal) && !TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out _))));
        Check("QA-B4 a stored Europe/Athens selects its entry in a Windows-derived list (no second entry)",
            selected.Id == "Europe/Athens" && selected.Resolution == ZoneResolution.Resolved && ids.Count(id => id == "Europe/Athens") == 1);
        Check("QA-B4 the Windows display names stay searchable under the IANA ids, each city on its own entry (\"pacific us\" → America/Los_Angeles, \"athens\" → Europe/Athens, \"bucharest\" → Europe/Bucharest, \"madrid\" → Europe/Madrid)",
            options.Where(o => o.Matches("pacific us")).Select(o => o.Id).Contains("America/Los_Angeles")
            && options.Where(o => o.Matches("athens")).Select(o => o.Id).SequenceEqual(["Europe/Athens"])
            && options.Where(o => o.Matches("bucharest")).Select(o => o.Id).SequenceEqual(["Europe/Bucharest"])
            && options.Where(o => o.Matches("madrid")).Select(o => o.Id).SequenceEqual(["Europe/Madrid"]));

        var outside = TimeZoneChoices.Build(windowsZones, Now, "America/Detroit", out var detroit);
        Check("QA-B4 a stored id outside a Windows-derived list (America/Detroit: CLDR maps Eastern/US to America/New_York only) is kept as its own entry after \"Local time\"",
            detroit is { Id: "America/Detroit", Resolution: ZoneResolution.Resolved } && ReferenceEquals(outside[1], detroit) && outside.Count(o => o.Id == "America/Detroit") == 1);
        TimeZoneChoices.Build(windowsZones, Now, " Europe/Bucharest ", out var listed);
        Check("QA-B4 a stored id with spaces that names a listed zone selects that entry (no duplicate entry)",
            listed.Id == "Europe/Bucharest" && listed.Resolution == ZoneResolution.Resolved);
        var unknown = TimeZoneChoices.Build(windowsZones, Now, "Europe/Foo", out var foo);
        Check("Row 8 QA-B4 an unresolvable stored id is kept as its own \"(unknown zone)\" entry",
            foo is { Id: "Europe/Foo", Name: "Europe/Foo", IsUnknown: true, Detail: TimeZoneChoices.UnknownZoneText, Label: "Europe/Foo (unknown zone)" } && unknown[1] == foo);
    }

    // ─── QA-B4 Windows coverage: every IANA id of every Windows zone, the same list on macOS ───

    private static void WindowsZoneCoverage()
    {
        var system = TimeZoneInfo.GetSystemTimeZones();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var catalog = TimeZoneCatalog.Load(system);
        var buildMs = watch.Elapsed.TotalMilliseconds;
        var list = SystemList();
        var athens = list.Where(o => o.Matches("athens")).Select(o => o.Id).ToList();
        Check($"QA-B4 this OS ({(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "other")}): {system.Count} system zones give {list.Count - 1} picker zones; \"athens\" → {string.Join(", ", athens)}; catalog built in {buildMs:0} ms",
            athens.SequenceEqual(["Europe/Athens"]) && catalog.Count == list.Count - 1 && buildMs < 1000);

        Check("QA-B4 the region-aware mapping reaches the cities the default mapping hides (GTB+GR → Europe/Athens, FLE+FI → Europe/Helsinki, GTB → Europe/Bucharest)",
            TimeZoneInfo.TryConvertWindowsIdToIanaId("GTB Standard Time", "GR", out var gr) && gr == "Europe/Athens"
            && TimeZoneInfo.TryConvertWindowsIdToIanaId("FLE Standard Time", "FI", out var fi) && fi == "Europe/Helsinki"
            && TimeZoneInfo.TryConvertWindowsIdToIanaId("GTB Standard Time", out var gtb) && gtb == "Europe/Bucharest"
            && TimeZoneCatalog.Regions.Count > 100);

        // The Windows zone list, as Windows would give it: one zone per Windows id of this computer's zones.
        var windowsIds = system
            .Select(z => TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out _) ? z.Id : TimeZoneInfo.TryConvertIanaIdToWindowsId(z.Id, out var w) ? w : null)
            .OfType<string>().Distinct().ToList();
        var windowsZones = windowsIds
            .Select(w => TimeZoneInfo.FindSystemTimeZoneById(w))
            .Select(z => TimeZoneInfo.CreateCustomTimeZone(z.Id, z.BaseUtcOffset, z.DisplayName, z.StandardName)).ToList();
        watch.Restart();
        var simulated = TimeZoneChoices.Build(windowsZones, Now, null, out _);
        var simulatedMs = watch.Elapsed.TotalMilliseconds;
        var simulatedIds = simulated.Skip(1).Select(o => o.Id!).ToList();
        List<string?> Find(string query) => simulated.Where(o => o.Matches(query)).Select(o => o.Id).ToList();
        Check($"QA-B4 a Windows zone list ({windowsIds.Count} Windows ids) gives {simulatedIds.Count} IANA picker zones in {simulatedMs:0} ms: all distinct, resolvable, none a Windows id",
            simulatedIds.Count > 2 * windowsIds.Count && simulatedIds.Distinct().Count() == simulatedIds.Count
            && simulatedIds.All(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _) && !TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out _)));
        Check("QA-B4 from Windows ids the picker offers Europe/Athens, Europe/Helsinki and Europe/Bucharest, and \"athens\"/\"helsinki\" pick them",
            new[] { "Europe/Athens", "Europe/Helsinki", "Europe/Bucharest" }.All(simulatedIds.Contains)
            && Find("athens").SequenceEqual(["Europe/Athens"]) && Find("helsinki").SequenceEqual(["Europe/Helsinki"]));
        Check("QA-B4 ids CLDR still spells the old way are offered under current tzdata names (Asia/Kolkata, Europe/Kyiv, not Asia/Calcutta, Europe/Kiev)",
            simulatedIds.Contains("Asia/Kolkata") && simulatedIds.Contains("Europe/Kyiv") && !simulatedIds.Contains("Asia/Calcutta") && !simulatedIds.Contains("Europe/Kiev")
            && simulatedIds.All(id => !TimeZoneCatalog.Renamed.ContainsKey(id)));

        if (!OperatingSystem.IsMacOS())
        {
            Skip("QA-B4 macOS: the picker is the system list as it is (nothing added or dropped)", "macOS only");
            Skip("QA-B4 macOS: the renamed-id table matches CLDR and tzdata", "macOS only (needs tzdata's zone list)");
            return;
        }
        var systemIds = system.Select(z => TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id).ToHashSet(StringComparer.Ordinal);
        var raw = windowsIds
            .SelectMany(w => TimeZoneCatalog.Regions.Select(r => TimeZoneInfo.TryConvertWindowsIdToIanaId(w, r, out var iana) ? iana : null)
                .Append(TimeZoneInfo.TryConvertWindowsIdToIanaId(w, out var fallback) ? fallback : null))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var notListed = raw.Where(id => !systemIds.Contains(id)).Order(StringComparer.Ordinal).ToList();
        Check($"QA-B4 macOS: the picker is the system list as it is ({systemIds.Count} ids, nothing added or dropped; a plain union with the region mapping would add {notListed.Count} aliases such as Asia/Calcutta)",
            catalog.Select(z => z.Id).ToHashSet(StringComparer.Ordinal).SetEquals(systemIds) && catalog.Count == systemIds.Count);
        var unexplained = notListed.Where(id => !id.StartsWith("Etc/", StringComparison.Ordinal) && !TimeZoneCatalog.Renamed.ContainsKey(id)).ToList();
        Check($"QA-B4 macOS: the renamed-id table matches CLDR and tzdata (every mapped id outside the system list is Etc/… or renamed; each new name is listed with the same Windows id){(unexplained.Count > 0 ? ": missing " + string.Join(", ", unexplained) : "")}",
            unexplained.Count == 0 && TimeZoneCatalog.Renamed.All(p => systemIds.Contains(p.Value)
                && TimeZoneInfo.TryConvertIanaIdToWindowsId(p.Key, out var oldWindows) && TimeZoneInfo.TryConvertIanaIdToWindowsId(p.Value, out var newWindows) && oldWindows == newWindows));
    }

    // ─── QA-N6: UP NEXT names the zone, and the zone's day when it differs ───

    private static async Task UpNextShowsTheZone()
    {
        var athens = AthensPick();
        async Task<string> UpNext(string? zone, string time, DayOfWeek day, TimeZoneInfo computer)
        {
            await using var rig = UiRig.CreateViewModels(realCoordinator: true, zone: computer, seed: s =>
            {
                s.ScheduleEnabled = true;
                s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[0].Id, Time = time, Days = [day], TimeZone = zone });
            });
            await rig.Coordinator.StartScheduleAsync();
            return rig.ViewModel.UpNextText;
        }

        Check("QA-N6 UP NEXT for a local slot is unchanged: \"UP NEXT · Tue 08:00  /  Groove Salad\"",
            await UpNext(null, "08:00", DayOfWeek.Tuesday, TimeZoneInfo.Utc) == "UP NEXT · Tue 08:00  /  Groove Salad");
        var sameDay = await UpNext(athens.Id, "08:00", DayOfWeek.Monday, TimeZoneInfo.Utc);
        Check($"QA-N6 same day: UP NEXT adds the slot's own time and zone: \"UP NEXT · Mon 05:00  /  Groove Salad · 08:00 {athens.Name}\" (was \"{sameDay}\")",
            sameDay == $"UP NEXT · Mon 05:00  /  Groove Salad · 08:00 {athens.Name}");
        var unknown = await UpNext("Europe/Foo", "08:00", DayOfWeek.Monday, TimeZoneInfo.Utc);
        Check($"Row 8 QA-N6 an unknown zone: local time plus \"Europe/Foo (unknown zone)\" (was \"{unknown}\")",
            unknown == "UP NEXT · Mon 08:00  /  Groove Salad · Europe/Foo (unknown zone)");
        if (!Available("Asia/Kolkata", "Pacific/Pago_Pago"))
        {
            Skip("QA-N6 cross-day UP NEXT (Kolkata slot on a Pago Pago computer)", "Asia/Kolkata or Pacific/Pago_Pago is not available here");
            return;
        }
        var crossDay = await UpNext("Asia/Kolkata", "01:00", DayOfWeek.Monday, Zone("Pacific/Pago_Pago"));
        Check($"QA-N6 cross day: Mon 01:00 Asia/Kolkata on a Pacific/Pago_Pago computer is \"UP NEXT · Sun 08:30  /  Groove Salad · Mon 01:00 Asia/Kolkata\" (was \"{crossDay}\")",
            crossDay == "UP NEXT · Sun 08:30  /  Groove Salad · Mon 01:00 Asia/Kolkata");
    }

    // ─── Log wiring: the composition root gives Core's scheduler the app log ───

    private static async Task SchedulerLogWiring()
    {
        using var directory = new TempDirectory("tz-log");
        var paths = AppPaths.Resolve(name => name == AppPaths.DataDirectoryOverrideVariable ? directory.Path : null, smokeTest: false);
        var log = new RecordingAppLog();
        var saved = Scheduler.Log;
        try
        {
            await using (var provider = AppComposition.BuildServiceProvider(paths, s => s.AddSingleton<IAppLog>(log)))
            {
                provider.GetRequiredService<IPlaybackCoordinator>();
                Check("Row 8 §6 resolving the real IPlaybackCoordinator registration sets Scheduler.Log to the app's IAppLog",
                    ReferenceEquals(Scheduler.Log, log) && ReferenceEquals(provider.GetRequiredService<IAppLog>(), log));
                var id = "Test/Unknown-" + Guid.NewGuid().ToString("N");
                var slot = new ScheduleEntry { StationId = Guid.NewGuid(), Time = "08:00", Days = [DayOfWeek.Monday], TimeZone = id };
                Scheduler.TryResolveZone(id, out _);
                Scheduler.TryResolveZone(" " + id + " ", out _);
                Scheduler.NextFor(slot, Now.UtcDateTime, TimeZoneInfo.Utc);
                _ = new SlotRowViewModel(slot, null, _ => Task.CompletedTask, ex => throw ex);
                var warnings = log.Entries.Where(e => e.EventName == "schedule.zone_unknown" && e.Message.Contains(id, StringComparison.Ordinal)).ToList();
                Check("Row 8 an unknown fresh id logs schedule.zone_unknown once, as a warning, through the app log (resolve ×2, NextFor, a schedule row)",
                    warnings.Count == 1 && warnings[0].Level == AppLogLevel.Warn);
            }
        }
        finally { Scheduler.Log = saved; }
        Check("Row 8 Scheduler.Log is restored after the check", ReferenceEquals(Scheduler.Log, saved));
    }

    // ─── Row 13: choose a zone by keyboard, save, reload ───

    private static async Task RoundTripThroughThePicker()
    {
        var athens = AthensPick();
        await using var rig = await UiRig.CreateHeadlessAsync(tray: true);
        var window = rig.Window!;
        var tray = new TrayProbe(rig);
        await ShowPage(rig, "Schedule");

        var editor = await OpenAsync(() => ClickAsync(ButtonWithText(window, "+  Add time slot")));
        var picker = Picker(editor);
        Check("Row 13 §4.5 the picker shows \"Local time\" and its hint",
            PickerText(editor).Text == TimeZoneChoices.LocalLabel && Shows(editor, "The start time and days follow this computer's clock.")
            && picker.PlaceholderText == ScheduleEditorViewModel.TimeZonePlaceholder);
        await TypeAsync(PickerText(editor), "athens");
        Check("Row 13 typing \"athens\" opens the suggestions", picker.IsDropDownOpen);
        await PressAsync(editor, Key.Down);
        await PressAsync(editor, Key.Enter);
        Check($"Row 13 Down then Enter chooses {athens.Name}, and Enter in the open list does not press Save",
            Model(editor).SelectedTimeZone?.Id == athens.Id && editor.IsVisible && !picker.IsDropDownOpen && rig.Settings.Schedule.Count == 0);
        Check($"Row 13 the picker shows \"{athens.Label}\" and the zone hint",
            PickerText(editor).Text == athens.Label && Shows(editor, $"The start time and days are in {athens.Name} time. DialShift switches at the matching moment on this computer."));
        Console.WriteLine("  PNG: " + Screenshot(editor, "schedule-editor-zone"));
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check("Row 13 Save closes the editor", await WaitAsync(() => !editor.IsVisible));

        var fresh = new SettingsStore(rig.Paths.DataDirectory).Load();
        Check("Row 13 a fresh SettingsStore load returns TimeZone == \"Europe/Athens\" (on every OS)",
            fresh.Schedule.Single().TimeZone == "Europe/Athens" && athens.Id == "Europe/Athens");
        var entry = rig.Settings.Schedule.Single();
        await PumpAsync();
        Layout(window);
        var tag = ZoneTag(window, entry);
        Check($"Row 13 QA-N6 the row shows the zone tag \"{athens.Name}\" and \"Next: Mon 05:00 your time\" (Mon 08:00 in Athens, EEST, on a UTC computer after 10:00)",
            Find<TextBlock>(tag).Single().Text == athens.Name && !tag.Classes.Contains("unknown") && NextStartLine(window, entry).Text == "Next: Mon 05:00 your time"
            && ToolTip.GetTip(tag) as string == $"The start time and days are in {athens.Name} time.");
        Check($"Row 13 BHV-65 the row's edit button names the zone: \"Edit the 08:00 {athens.Name} slot\"",
            ByName<Button>(window, $"Edit the 08:00 {athens.Name} slot") != null);
        Console.WriteLine("  PNG: " + Screenshot(window, "schedule-zoned-row"));
        await tray.CheckAfter("a zoned slot add (Row 13)");

        editor = await OpenAsync(() => ClickAsync(ByName<Button>(window, $"Edit the 08:00 {athens.Name} slot")));
        Check("Row 13 reopening the slot shows the saved zone", Model(editor).SelectedTimeZone?.Id == athens.Id && PickerText(editor).Text == athens.Label);
        var count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(editor, "Delete slot"));
        var confirm = await WaitForWindowAsync<MessageDialog>(count);
        Check($"Row 13 BHV-58 the delete question names the zone: \"Delete the 08:00 {athens.Name} switch to Groove Salad? It repeats on Mon.\"",
            Shows(confirm, $"Delete the 08:00 {athens.Name} switch to Groove Salad? It repeats on Mon."));
        await PressAsync(confirm, Key.Escape);
        await PressAsync(editor, Key.Escape);
        await tray.CheckAfter("a zoned slot edit, cancelled");
    }

    // ─── Row 14: stored values survive an untouched save byte for byte ───

    private static async Task MigrationKeepsStoredValues()
    {
        ScheduleEntry Slot(Settings s, string time, string zone) =>
            new() { StationId = s.Stations[0].Id, Time = time, Days = [DayOfWeek.Monday], TimeZone = zone, Label = "Stored " + zone.Trim() };
        var stored = new[] { "Europe/Foo", " europe/Athens ", " Europe/Athens ", "Etc/GMT-3" };
        await using var rig = await UiRig.CreateHeadlessAsync(seed: s =>
        {
            for (var i = 0; i < stored.Length; i++) s.Schedule.Add(Slot(s, $"0{6 + i}:00", stored[i]));
        });
        // Written once, as a previous version or another computer would have: the raw values are the baseline.
        rig.Store.Save(rig.Settings);
        var before = RawZones(rig.Paths.SettingsFile);
        var window = rig.Window!;
        await ShowPage(rig, "Schedule");

        foreach (var entry in rig.Settings.Schedule.ToList())
        {
            var original = entry.TimeZone!;
            var name = original.Trim();
            var editor = await OpenAsync(() => ClickAsync(ByName<Button>(window, $"Edit the {entry.Time} {name} slot")));
            var selected = Model(editor).SelectedTimeZone;
            Check($"Row 14 QA-B4 \"{original}\": the editor opens on the stored value, shown trimmed (\"{PickerText(editor).Text}\")",
                selected != null && selected.Name == name && PickerText(editor).Text == selected.Label && editor.IsVisible);
            await ClickAsync(ButtonWithText(editor, "Save"));
            await WaitAsync(() => !editor.IsVisible);
            var after = RawZones(rig.Paths.SettingsFile);
            Check($"Row 14 QA-B4 \"{original}\": Save without touching the picker keeps the value byte-identical (no silent rewrite)",
                !editor.IsVisible && after[entry.Id] == original && rig.Settings.Schedule.Single(e => e.Id == entry.Id).TimeZone == original);
        }
        Check("Row 14 after four untouched saves every stored TimeZone is exactly as it was", RawZones(rig.Paths.SettingsFile).OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)));
        Check("Row 14 the unknown id is shown as \"Europe/Foo (unknown zone)\" on its row",
            Find<TextBlock>(ZoneTag(window, rig.Settings.Schedule.Single(e => e.TimeZone == "Europe/Foo"))).Single().Text == "Europe/Foo (unknown zone)");

        var foo = rig.Settings.Schedule.Single(e => e.TimeZone == "Europe/Foo");
        var editorFoo = await OpenAsync(() => ClickAsync(ByName<Button>(window, "Edit the 06:00 Europe/Foo slot")));
        await TypeAsync(ByName<TextBox>(editorFoo, ScheduleEditorViewModel.LabelLabel), "Renamed, zone untouched");
        await ClickAsync(ButtonWithText(editorFoo, "Save"));
        await WaitAsync(() => !editorFoo.IsVisible);
        Check("Row 14 QA-B4 editing another field and saving still keeps the unknown id as stored",
            RawZones(rig.Paths.SettingsFile)[foo.Id] == "Europe/Foo" && rig.OnDisk().Schedule.Single(e => e.Id == foo.Id).Label == "Renamed, zone untouched");
    }

    // ─── Typed text that matches nothing blocks Save and focuses the picker ───

    private static async Task NoMatchBlocksSave()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        var window = rig.Window!;
        await ShowPage(rig, "Schedule");
        var editor = await OpenAsync(() => ClickAsync(ButtonWithText(window, "+  Add time slot")));
        var text = PickerText(editor);
        await TypeAsync(text, "Atlantis");
        Check("§4.5 text that matches no zone clears the choice (the hint asks for one)",
            Model(editor).SelectedTimeZone == null && Shows(editor, "Choose a time zone from the list, or Local time."));
        await ClickAsync(ByName<TextBox>(editor, ScheduleEditorViewModel.LabelLabel));
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check("§4.5 Save is blocked with \"Choose a time zone from the list, or Local time.\" and the picker's text box has focus",
            editor.IsVisible && VisibleError(editor)?.Text == "Choose a time zone from the list, or Local time." && Focused(editor) == text
            && rig.Settings.Schedule.Count == 0 && !rig.SavedToDisk);
        Console.WriteLine("  PNG: " + Screenshot(editor, "schedule-editor-zone-required"));
        await ChooseZoneAsync(editor, "local");
        Check("§4.5 choosing \"Local time\" again clears the error", Model(editor).SelectedTimeZone?.Id == null && Model(editor).SelectedTimeZone != null && VisibleError(editor) == null);
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check("§4.5 then Save stores a local slot", await WaitAsync(() => !editor.IsVisible) && rig.OnDisk().Schedule.Single().TimeZone == null);
    }

    // ─── Row 8, UI side: an unknown zone is flagged, not hidden ───

    private static async Task UnknownZoneRow()
    {
        var athens = AthensPick();
        ScheduleEntry? unknown = null, zoned = null, local = null;
        await using var rig = await UiRig.CreateHeadlessAsync(seed: s =>
        {
            s.Schedule.Add(unknown = new ScheduleEntry { StationId = s.Stations[0].Id, Time = "07:00", Days = [DayOfWeek.Monday], TimeZone = "Europe/Foo" });
            s.Schedule.Add(zoned = new ScheduleEntry { StationId = s.Stations[1].Id, Time = "08:00", Days = [DayOfWeek.Monday], TimeZone = athens.Id });
            s.Schedule.Add(local = new ScheduleEntry { StationId = s.Stations[2].Id, Time = "09:00", Days = [DayOfWeek.Monday] });
        });
        var window = rig.Window!;
        await ShowPage(rig, "Schedule");
        var warning = Resource(window, "DsWarningBrush");
        var tint = Resource(window, "DsWarningTintBrush");
        Check("Row 8 the warning palette is amber (#F2D478 on #3A3622), distinct from the error red",
            warning == Color.Parse("#F2D478") && tint == Color.Parse("#3A3622") && warning != Resource(window, "DsErrorBrush"));

        var tag = ZoneTag(window, unknown!);
        var tagText = Find<TextBlock>(tag).Single();
        var note = NextStartLine(window, unknown!);
        Check("Row 8 the unknown zone's tag reads \"Europe/Foo (unknown zone)\" in amber on the amber tint",
            tagText.Text == "Europe/Foo (unknown zone)" && tag.Classes.Contains("unknown") && ColorOf(tag.Background) == tint && ColorOf(tagText.Foreground) == warning);
        Check("Row 8 its next-start line says \"Runs on local time\", in amber",
            note.Text == SlotRowViewModel.UnknownZoneNote && note.Classes.Contains("warning") && ColorOf(note.Foreground) == warning);
        Check("Row 8 the tag's tooltip explains the fallback", ToolTip.GetTip(tag) as string == SlotRowViewModel.UnknownZoneToolTip
            && SlotRowViewModel.UnknownZoneToolTip == "This computer doesn't recognize this time zone, so the slot runs on local time. Edit the slot to choose a zone.");
        var zonedTag = ZoneTag(window, zoned!);
        Check("Row 8 a resolved zone's tag is not amber (tile background, regular text)",
            !zonedTag.Classes.Contains("unknown") && ColorOf(zonedTag.Background) == Resource(window, "DsTileBrush")
            && ColorOf(Find<TextBlock>(zonedTag).Single().Foreground) == Resource(window, "DsTextBrush") && ColorOf(NextStartLine(window, zoned!).Foreground) != warning);
        Check("Row 8 a local slot has no zone line", !Find<Border>(window).Any(b => b.Classes.Contains("zoneTag") && b.IsEffectivelyVisible && b.DataContext is SlotRowViewModel r && r.Entry.Id == local!.Id));
        Console.WriteLine("  PNG: " + Screenshot(window, "schedule-unknown-zone"));

        var editor = await OpenAsync(() => ClickAsync(ByName<Button>(window, "Edit the 07:00 Europe/Foo slot")));
        var hint = Find<TextBlock>(editor).Single(t => t.Classes.Contains("hint") && t.IsEffectivelyVisible && t.Text?.Contains("Europe/Foo", StringComparison.Ordinal) == true);
        Check("Row 8 the editor keeps the unknown id selected, in amber, with the fallback explained",
            Model(editor).IsTimeZoneUnknown && Picker(editor).Classes.Contains("unknown") && PickerText(editor).Text == "Europe/Foo (unknown zone)"
            && ColorOf(PickerText(editor).Foreground) == warning && ColorOf(hint.Foreground) == warning
            && hint.Text == "This computer doesn't recognize Europe/Foo, so the slot runs on local time. Choose a zone to change it, or keep it as it is.");
        Console.WriteLine("  PNG: " + Screenshot(editor, "schedule-editor-unknown-zone"));
        await ChooseZoneAsync(editor, "athens");
        Check("Row 8 choosing a listed zone clears the amber state", !Model(editor).IsTimeZoneUnknown && !Picker(editor).Classes.Contains("unknown") && ColorOf(hint.Foreground) != warning);
        await PressAsync(editor, Key.Escape);
        Check("Row 8 cancelling keeps the stored unknown id", !editor.IsVisible && unknown!.TimeZone == "Europe/Foo");
    }

    // ─── QA-N6: a cross-day row shows its local start; the footer shows both days ───

    private static async Task CrossDayRowAndFooter()
    {
        if (!Available("Asia/Kolkata", "Pacific/Pago_Pago"))
        {
            Skip("QA-N6 cross-day row (Kolkata slot on a Pago Pago computer)", "Asia/Kolkata or Pacific/Pago_Pago is not available here");
            return;
        }
        ScheduleEntry? slot = null;
        await using var rig = await UiRig.CreateHeadlessAsync(realCoordinator: true, zone: Zone("Pacific/Pago_Pago"), seed: s =>
        {
            s.ScheduleEnabled = true;
            s.Schedule.Add(slot = new ScheduleEntry { StationId = s.Stations[0].Id, Time = "01:00", Days = [DayOfWeek.Monday], TimeZone = "Asia/Kolkata", Label = "Delhi morning" });
        });
        var window = rig.Window!;
        await rig.Coordinator.StartScheduleAsync();
        await ShowPage(rig, "Schedule");
        var page = rig.ViewModel.Schedule;
        Check("QA-N6 on a Pago Pago computer (Sun 23:00 local at Mon 10:00 UTC) the page opens on Sunday, which has no slot",
            page.SelectedDay == DayOfWeek.Sunday && page.Slots.Count == 0 && ButtonWithText(window, "Sun").Classes.Contains("selected"));
        await ClickAsync(ByName<Button>(window, "Show Monday slots"));
        Layout(window);
        Check("QA-N6 the slot sits under its own zone's day (Mon) and shows its next local start, a Sunday: \"Next: Sun 08:30 your time\"",
            page.Slots.Single().Entry == slot && Find<TextBlock>(ZoneTag(window, slot!)).Single().Text == "Asia/Kolkata"
            && NextStartLine(window, slot!).Text == "Next: Sun 08:30 your time" && Shows(window, "Delhi morning · Mon"));
        Check("QA-N6 the footer's UP NEXT shows the local time and the zone's own day and time: \"UP NEXT · Sun 08:30  /  Groove Salad · Mon 01:00 Asia/Kolkata\"",
            Shows(window, "UP NEXT · Sun 08:30  /  Groove Salad · Mon 01:00 Asia/Kolkata") && Shows(window, "LOCAL TIME · " + Zone("Pacific/Pago_Pago").StandardName));
        Console.WriteLine("  PNG: " + Screenshot(window, "schedule-cross-day"));

        slot!.Enabled = false;
        page.Refresh();
        await PumpAsync();
        Check("QA-N6 a disabled zoned row says \"When enabled: Sun 08:30 your time\"", NextStartLine(window, slot).Text == "When enabled: Sun 08:30 your time");
    }

    // ─── Rows 9 and 10 through the editor: zone-aware conflict hint ───

    private static async Task ZoneAwareConflicts()
    {
        var athens = AthensPick();
        await using var rig = await UiRig.CreateHeadlessAsync(seed: s =>
        {
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday], TimeZone = athens.Id });
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Monday], TimeZone = " " });
        });
        var window = rig.Window!;
        await ShowPage(rig, "Schedule");

        var editor = await OpenAsync(() => ClickAsync(ButtonWithText(window, "+  Add time slot")));
        await ChooseZoneAsync(editor, "athens");
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check($"Row 9 QA-N5 same zone, time and day: \"Another enabled slot in {athens.Name} already starts at this time on one of those days.\", time focused",
            editor.IsVisible && VisibleError(editor)?.Text == $"Another enabled slot in {athens.Name} already starts at this time on one of those days."
            && Focused(editor) == ByName<TextBox>(editor, ScheduleEditorViewModel.TimeLabel) && rig.Settings.Schedule.Count == 2);
        Console.WriteLine("  PNG: " + Screenshot(editor, "schedule-editor-zone-conflict"));

        await ChooseZoneAsync(editor, "local");
        Check("Row 10 changing the zone clears the conflict message", VisibleError(editor) == null && Model(editor).SelectedTimeZone?.Id == null);
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check($"Row 10 QA-N5 the same 08:00 on local time is not a conflict with the {athens.Name} slot: saved",
            await WaitAsync(() => !editor.IsVisible) && rig.OnDisk().Schedule.Count(e => e.Time == "08:00") == 2);

        editor = await OpenAsync(() => ClickAsync(ButtonWithText(window, "+  Add time slot")));
        await TypeAsync(ByName<TextBox>(editor, ScheduleEditorViewModel.TimeLabel), "09:00");
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check("Row 9 QA-N5 a local slot conflicts with a stored \" \" zone (whitespace is local): the local wording, without a zone",
            editor.IsVisible && VisibleError(editor)?.Text == "Another enabled slot already starts at this time on one of those days.");
        await PressAsync(editor, Key.Escape);
        Check("Row 9 nothing extra was saved", !editor.IsVisible && rig.OnDisk().Schedule.Count == 3);
    }

    // ─── Compact 780×650 with zoned and unknown rows ───

    private static async Task CompactZonedLayout()
    {
        var athens = AthensPick();
        await using var rig = await UiRig.CreateHeadlessAsync(realCoordinator: true, width: 780, height: 650, seed: s =>
        {
            s.ScheduleEnabled = true;
            s.Stations[0].Name = "Groove Salad Classic Ambient";
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[0].Id, Time = "08:00", Days = [.. UiText.Week], TimeZone = athens.Id, Label = "Breakfast with the Athens morning show" });
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[1].Id, Time = "12:00", Days = [.. UiText.Week], TimeZone = "Europe/Foo", Label = "Lunch" });
            // The longest common IANA id, and the next start (Mon 11:30 UTC), so the footer carries it too.
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[2].Id, Time = "08:30", Days = [.. UiText.Week], TimeZone = "America/Argentina/Buenos_Aires", Label = "Buenos Aires morning show" });
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[2].Id, Time = "18:00", Days = [.. UiText.Week], Label = "Local evening" });
        });
        var window = rig.Window!;
        await rig.Coordinator.StartScheduleAsync();
        await ShowPage(rig, "Schedule");
        Check("compact 780×650: the window is at its minimum", window.ClientSize == new Size(780, 650));
        var clipped = ClippedTexts(window);
        if (clipped.Count > 0) Console.WriteLine("  clipped on Schedule: " + string.Join("; ", clipped));
        var tags = Find<Border>(window).Count(b => b.Classes.Contains("zoneTag") && b.IsEffectivelyVisible);
        Check($"§4.5 compact 780×650 Schedule page with {tags} zone tags (one unknown) and the zoned footer: nothing trimmed or clipped",
            clipped.Count == 0 && tags == 3 && Find<Border>(window).Any(b => b.Classes.Contains("zoneTag") && b.Classes.Contains("unknown") && b.IsEffectivelyVisible));
        Console.WriteLine("  PNG: " + Screenshot(window, "compact-schedule-zones"));
        Check("QA-N6 compact: the footer's UP NEXT carries the zone (and fits, per the check above)",
            Shows(window, "UP NEXT · Mon 11:30  /  Secret Agent · 08:30 America/Argentina/Buenos_Aires"));

        foreach (var (time, name, file) in new[] { ("12:00", "Europe/Foo", "compact-editor-unknown"), ("08:00", athens.Name, "compact-editor-zoned"), ("08:30", "America/Argentina/Buenos_Aires", "compact-editor-long-zone") })
        {
            var editor = await OpenAsync(() => ClickAsync(ByName<Button>(window, $"Edit the {time} {name} slot")));
            Layout(editor);
            var editorClipped = ClippedTexts(editor);
            if (editorClipped.Count > 0) Console.WriteLine($"  clipped in the {name} editor: " + string.Join("; ", editorClipped));
            var save = ButtonWithText(editor, "Save");
            var bottom = save.TranslatePoint(new Point(0, save.Bounds.Height), editor)?.Y ?? double.MaxValue;
            Check($"§4.5 compact: the {name} slot editor shows every label, the zone hint and the picker unclipped, Save inside the dialog",
                editorClipped.Count == 0 && bottom <= editor.ClientSize.Height && Picker(editor).Bounds.Width >= 280);
            Console.WriteLine("  PNG: " + Screenshot(editor, file));
            await PressAsync(editor, Key.Escape);
        }
    }

    // ─── HS-03 on the App's own tray (App.Tray) after zone edits ───

    private static async Task AppTrayAfterZoneEdits()
    {
        Settings? live = null;
        await using var app = await AppHarness.StartAsync(seed: s => live = s);
        var window = app.MainWindow!;
        var tray = app.App.Tray!;
        var probe = new TrayProbe(tray, live!);
        await probe.CheckAfter("App startup");
        await ShowPage(window, "Schedule");

        var editor = await OpenAsync(() => ClickAsync(ButtonWithText(window, "+  Add time slot")));
        await ChooseZoneAsync(editor, "athens");
        await ClickAsync(ButtonWithText(editor, "Save"));
        await WaitAsync(() => !editor.IsVisible);
        Check("HS-03 Row 13 through the real App: the zoned slot is saved", live!.Schedule.Single().TimeZone == AthensPick().Id && !editor.IsVisible);
        await probe.CheckAfter("a zoned slot add (App)");

        editor = await OpenAsync(() => ClickAsync(ByName<Button>(window, $"Edit the 08:00 {AthensPick().Name} slot")));
        await ChooseZoneAsync(editor, "local");
        await ClickAsync(ButtonWithText(editor, "Save"));
        await WaitAsync(() => !editor.IsVisible);
        Check("HS-03 the zone edit back to local time is saved (the editor replaces the entry)", live.Schedule.Single().TimeZone == null && !editor.IsVisible);
        await probe.CheckAfter("a zone change to local time (App)");

        live.Schedule[0].TimeZone = "Europe/Foo";
        ((MainWindowViewModel)window.DataContext!).Schedule.Refresh();
        await PumpAsync();
        Layout(window);
        editor = await OpenAsync(() => ClickAsync(ByName<Button>(window, "Edit the 08:00 Europe/Foo slot")));
        await ClickAsync(ButtonWithText(editor, "Save"));
        await WaitAsync(() => !editor.IsVisible);
        Check("HS-03 Row 14 through the real App: an untouched save keeps the unknown id", live.Schedule.Single().TimeZone == "Europe/Foo" && !editor.IsVisible);
        await probe.CheckAfter("an untouched save of an unknown zone (App)");
        Check("HS-03 the App's tray is still the one created at startup, the only registered icon",
            ReferenceEquals(app.App.Tray, tray) && TrayIcon.GetIcons(app.App) is { Count: 1 } icons && ReferenceEquals(icons[0], tray.TrayIcon));
    }
}
