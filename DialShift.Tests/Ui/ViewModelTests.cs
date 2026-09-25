using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DialShift.App.Platform;
using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Core;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Ui;

/// <summary>
/// View-model suites (no Avalonia): snapshot rendering (HS-13), command routing with a save after each command (HS-04,
/// MX-04), editor validation (HS-02), settings recovery and save failure (HS-07), launch at login (BHV-59, DOD-09), and the
/// real coordinator behind the view model (HS-14 texts, MX-09). The headless suite repeats the user-facing parts through
/// the real views and dialogs.
/// </summary>
public static class ViewModelTests
{
    public static async Task RunAsync()
    {
        await FirstLaunchDefaults();
        await RestoreSettings();
        await SnapshotRendering();
        await FooterTexts();
        await CommandRouting();
        await VolumeRules();
        await StationEditorValidation();
        await StationDeleteFlow();
        await SlotEditorValidation();
        await SlotConflictAndEdit();
        await SlotDeleteFlow();
        await SlotTimeWithDottedInput();
        await SlotTimeUnderDotCultures();
        await SettingsPage();
        await LaunchAtLogin();
        await RecoveryNotice();
        await SaveFailure();
        await RealCoordinatorTexts();
        await RealCoordinatorCommands();
    }

    private static string Json(Settings settings)
    {
        using var dir = new TempDirectory("ui-json");
        new SettingsStore(dir.Path).Save(settings);
        return File.ReadAllText(Path.Combine(dir.Path, "settings.json"));
    }

    // ─── BHV-01 / BHV-02 (HS-01 view-model half) ───

    private static async Task FirstLaunchDefaults()
    {
        await using var rig = UiRig.CreateViewModels(realCoordinator: true);
        var vm = rig.ViewModel;
        await rig.Coordinator.StartScheduleAsync();
        Check("HS-01 BHV-01 no settings.json: three SomaFM starter stations",
            rig.Settings.Stations.Select(s => s.Name).SequenceEqual(["Groove Salad", "Drone Zone", "Secret Agent"])
            && rig.Settings.Stations.All(s => s.Url.StartsWith("https://ice5.somafm.com/", StringComparison.Ordinal)));
        Check("HS-01 BHV-01 Stations page lists them: \"03  SAVED FREQUENCIES\"",
            vm.Stations.CountText == "03  SAVED FREQUENCIES" && vm.Stations.Rows.Select(r => r.Name).SequenceEqual(["Groove Salad", "Drone Zone", "Secret Agent"]) && !vm.Stations.IsEmpty);
        Check("HS-01 BHV-01 status \"READY WHEN YOU ARE\", title and track are the first-launch texts",
            vm.StatusText == "READY WHEN YOU ARE" && vm.StationTitle == "Your next favorite frequency." && vm.TrackText == "Choose a station and make yourself at home.");
        Check("HS-01 BHV-01 volume 60 (\"60%\"), Play label, schedule off, no fallback, launch at login and start in tray off",
            vm.Volume == 60 && vm.VolumeLabel == "60%" && vm.PlayPauseLabel == UiText.PlayLabel && !vm.Schedule.IsScheduleEnabled
            && vm.Settings.SelectedFallback?.Name == SettingsPageViewModel.NoFallbackName && !vm.Settings.LaunchAtLogin && !vm.Settings.StartInTray);
        Check("HS-01 BHV-01 nothing plays at first launch (startup catch-up with the schedule off starts no stream)",
            rig.Engine!.Starts.Count == 0 && rig.Real!.Snapshot.Status == PlaybackStatus.Stopped && !rig.SavedToDisk);
    }

    private static async Task RestoreSettings()
    {
        var saved = new Settings
        {
            Volume = 35,
            ScheduleEnabled = true,
            StartInTray = true,
            LaunchAtLogin = false,
            Stations =
            [
                new() { Name = "Kosmos", Tag = "ERT · World", Url = "https://radio.example.org/kosmos" },
                new() { Name = "Melodia", Tag = "Pop", Url = "https://radio.example.org/melodia" },
                new() { Name = "Jazz FM", Tag = "Jazz", Url = "https://radio.example.org/jazz" },
                new() { Name = "Rock", Tag = "Rock", Url = "https://radio.example.org/rock" }
            ]
        };
        saved.FallbackStationId = saved.Stations[2].Id;
        saved.LastStationId = saved.Stations[1].Id;
        saved.Schedule.Add(new ScheduleEntry { StationId = saved.Stations[0].Id, Time = "07:30", Days = [DayOfWeek.Monday], Label = "Morning" });
        var json = Json(saved).Replace("\"Volume\"", "\"UnknownFutureProperty\": 7, \"Volume\"", StringComparison.Ordinal);

        await using var rig = UiRig.CreateViewModels(settingsJson: json);
        var vm = rig.ViewModel;
        Check("HS-01 BHV-02 MX-01 restored stations render (\"04  SAVED FREQUENCIES\"); an unknown JSON property is ignored",
            vm.Stations.CountText == "04  SAVED FREQUENCIES" && vm.Stations.Rows.Select(r => r.Name).SequenceEqual(["Kosmos", "Melodia", "Jazz FM", "Rock"]));
        Check("HS-01 BHV-02 restored fallback: picker selection and the \" · Fallback\" row subtitle",
            vm.Settings.SelectedFallback?.Id == saved.FallbackStationId && vm.Stations.Rows[2].Subtitle == "Jazz · Fallback" && vm.Stations.Rows[0].Subtitle == "ERT · World");
        Check("HS-01 BHV-02 restored volume 35, schedule on, start in tray on, LastStationId kept",
            vm.Volume == 35 && vm.VolumeLabel == "35%" && vm.Schedule.IsScheduleEnabled && vm.Settings.StartInTray && rig.Settings.LastStationId == saved.LastStationId);
        Check("HS-01 BHV-02 restored slot shows on Monday: 07:30 Kosmos \"Morning · Mon\"",
            vm.Schedule.Slots.Count == 1 && vm.Schedule.Slots[0].Time == "07:30" && vm.Schedule.Slots[0].StationName == "Kosmos" && vm.Schedule.Slots[0].Subtitle == "Morning · Mon");
    }

    // ─── HS-13: snapshot rendering ───

    private static PlaybackSnapshot Snap(PlaybackStatus status, string statusText, string track, bool active, string? current = null, int volume = 60,
        Occurrence? next = null, string? nextName = null) =>
        new(status, null, null, null, current, active, status == PlaybackStatus.Playing, false, statusText, track, null, next, nextName, volume);

    private static async Task SnapshotRendering()
    {
        await using var rig = UiRig.CreateViewModels();
        var vm = rig.ViewModel;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Fake!.Publish(Snap(PlaybackStatus.Playing, "Live broadcast", "Now: a song", active: true, current: "Groove Salad", volume: 42));
        Check("HS-13 BHV-25 status line is the snapshot's text upper-cased", vm.StatusText == "LIVE BROADCAST");
        Check("HS-13 BHV-25 title is the current station; track line follows the snapshot", vm.StationTitle == "Groove Salad" && vm.TrackText == "Now: a song");
        Check("HS-13 BHV-25 active: \"Ⅱ  Pause\" with accessible name \"Pause\"", vm.PlayPauseLabel == "Ⅱ  Pause" && vm.PlayPauseAutomationName == "Pause");
        Check("HS-13 BHV-28 volume follows the snapshot: 42 and \"42%\"", vm.Volume == 42 && vm.VolumeLabel == "42%");
        Check("HS-13 BHV-25 Playing → live styling only", vm.IsLive && !vm.IsBusy && !vm.IsProblem && vm.StatusKind == PlayerStatusKind.Live);
        Check("HS-13 BHV-25 the bound properties raise PropertyChanged", new[] { "StatusText", "StationTitle", "TrackText", "PlayPauseLabel", "VolumeLabel" }.All(changed.Contains));

        rig.Fake.Publish(Snap(PlaybackStatus.Stopped, "Paused", "Press play to return to the live broadcast.", active: false));
        Check("HS-13 BHV-25 no station: title falls back to \"Your next favorite frequency.\"", vm.StationTitle == UiText.DefaultTitle);
        Check("HS-13 BHV-25 inactive: \"▶  Play\" with accessible name \"Play\"", vm.PlayPauseLabel == "▶  Play" && vm.PlayPauseAutomationName == "Play");
        rig.Fake.Publish(Snap(PlaybackStatus.Failed, "Stream unavailable · retry in 6s", "Your next scheduled change will still run.", active: true, current: "Drone Zone"));
        Check("HS-13 BHV-25 Failed → problem styling, countdown text upper-cased", vm.IsProblem && vm.StatusText == "STREAM UNAVAILABLE · RETRY IN 6S");
        rig.Fake.Publish(Snap(PlaybackStatus.Connecting, "Connecting…", "Opening the live stream", active: true, current: "Drone Zone"));
        Check("HS-13 BHV-25 Connecting → busy styling", vm.IsBusy && !vm.IsLive && vm.StatusText == "CONNECTING…");

        Check("HS-13 BHV-23 tray tooltip: \"DialShift · Paused\" when inactive", UiText.TrayTooltip(Snap(PlaybackStatus.Stopped, "Paused", "", false)) == "DialShift · Paused");
        Check("HS-13 BHV-23 tray tooltip: \"DialShift · Connecting\" when active without a current station",
            UiText.TrayTooltip(Snap(PlaybackStatus.Connecting, "Connecting…", "", true)) == "DialShift · Connecting");
        var longName = new string('x', 100);
        var tooltip = UiText.TrayTooltip(Snap(PlaybackStatus.Playing, "Live broadcast", "", true, current: longName));
        Check("HS-13 BHV-23 tray tooltip is truncated to 63 characters", tooltip.Length == 63 && tooltip.StartsWith("DialShift · xxx", StringComparison.Ordinal));

        var assembly = typeof(DialShift.App.App).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Check($"HS-13 BHV-62 About shows the full SemVer (informational version without +build metadata), not the legacy hard-coded 0.1.0 (\"{UiRig.Version}\")",
            vm.Settings.VersionText == "DialShift  /  " + UiRig.Version && UiRig.Version != "0.1.0" && UiRig.Version != "unknown"
            && UiRig.Version == informational.Split('+')[0] && !UiRig.Version.Contains('+', StringComparison.Ordinal)
            && UiRig.Version.StartsWith(assembly.GetName().Version!.ToString(3), StringComparison.Ordinal));
        Check("HS-13 BHV-62 About keeps a pre-release suffix and drops build metadata: \"0.3.0-rc.1+4f2a9c1\" → \"0.3.0-rc.1\"",
            AppInfo.DisplayVersion("0.3.0-rc.1+4f2a9c1", new Version(0, 3, 0, 0)) == "0.3.0-rc.1"
            && AppInfo.DisplayVersion("0.3.0-rc.1", null) == "0.3.0-rc.1"
            && AppInfo.DisplayVersion("0.3.0+4f2a9c1", null) == "0.3.0");
        Check("HS-13 BHV-62 without an informational version About falls back to major.minor.patch, then \"unknown\"",
            AppInfo.DisplayVersion(null, new Version(0, 3, 0, 0)) == "0.3.0" && AppInfo.DisplayVersion(" +abc", new Version(1, 2, 3)) == "1.2.3"
            && AppInfo.DisplayVersion(null, null) == "unknown");
    }

    private static async Task FooterTexts()
    {
        await using var rig = UiRig.CreateViewModels(zone: Zones.Athens);
        var vm = rig.ViewModel;
        Check("HS-13 BHV-29 schedule off: \"SCHEDULE OFF · You're in control\"", vm.UpNextText == "SCHEDULE OFF · You're in control");
        rig.Settings.ScheduleEnabled = true;
        rig.Fake!.Publish(Snap(PlaybackStatus.ScheduledWaiting, "Waiting", "", false));
        Check("HS-13 BHV-29 schedule on, nothing next: \"SCHEDULE ON · Add your first time slot\"", vm.UpNextText == "SCHEDULE ON · Add your first time slot");
        var entry = new ScheduleEntry { Time = "11:00", Days = [DayOfWeek.Monday] };
        rig.Fake.Publish(Snap(PlaybackStatus.ScheduledWaiting, "Waiting", "", false, next: new Occurrence(entry, new DateTime(2026, 9, 14, 11, 0, 0)), nextName: "Drone Zone"));
        Check("HS-13 BHV-29 next slot: \"UP NEXT · Mon 11:00  /  Drone Zone\"", vm.UpNextText == "UP NEXT · Mon 11:00  /  Drone Zone");
        Check("HS-13 BHV-29 LOCAL TIME shows the zone's standard name even during DST (Athens in September)",
            vm.LocalTimeText == "LOCAL TIME · " + Zones.Athens.StandardName && Zones.Athens.IsDaylightSavingTime(new DateTime(2026, 9, 14, 13, 0, 0))
            && Zones.Athens.StandardName != Zones.Athens.DaylightName);
        Check("HS-13 BHV-55 the Schedule page opens on today in the injected zone (Monday)",
            vm.Schedule.SelectedDay == DayOfWeek.Monday && vm.Schedule.DayTabs.Single(t => t.IsSelected).Day == DayOfWeek.Monday);

        // 2026-09-13 23:30 UTC is already Monday 02:30 in Athens: the day tab follows the injected zone, not UTC.
        await using var late = UiRig.CreateViewModels(zone: Zones.Athens, now: new DateTimeOffset(2026, 9, 13, 23, 30, 0, TimeSpan.Zero));
        Check("HS-13 BHV-55 cross-midnight: 23:30 UTC Sunday opens Monday in Athens", late.ViewModel.Schedule.SelectedDay == DayOfWeek.Monday);
    }

    // ─── HS-04 / MX-04: every command routes to the coordinator, then saves ───

    private static async Task CommandRouting()
    {
        await using var rig = UiRig.CreateViewModels();
        var vm = rig.ViewModel;

        var mark = rig.Journal.Count;
        await vm.TogglePlayCommand.ExecuteAsync();
        Check("HS-04 MX-04 BHV-26 BHV-17 Play/Pause → ToggleAsync, then a save", rig.Since(mark) == "coordinator.ToggleAsync > settings.save");
        mark = rig.Journal.Count;
        await vm.NextStationCommand.ExecuteAsync();
        Check("HS-04 MX-04 BHV-27 BHV-17 Skip → NextStationAsync, then a save", rig.Since(mark) == "coordinator.NextStationAsync > settings.save");
        mark = rig.Journal.Count;
        var drone = vm.Stations.Rows[1];
        await drone.ListenCommand.ExecuteAsync();
        Check("HS-04 MX-04 BHV-17 Listen → PlayAsync(that station), then a save", rig.Since(mark) == $"coordinator.PlayAsync:{drone.Station.Id} > settings.save");
        Check("HS-04 MX-01 each save reached disk", rig.SavedToDisk);

        mark = rig.Journal.Count;
        vm.HideToTrayCommand.Execute(null);
        Check("HS-04 MX-04 BHV-14 the hide button asks the shell to hide the window (playback untouched)", rig.Since(mark) == "shell.HideMainWindow");

        vm.ShowScheduleCommand.Execute(null);
        Check("HS-04 MX-04 BHV-24 Schedule tab selects the Schedule page", vm.CurrentPage == vm.Schedule && vm.IsScheduleSelected && !vm.IsStationsSelected);
        vm.ShowSettingsCommand.Execute(null);
        Check("HS-04 MX-04 BHV-24 Settings tab selects the Settings page", vm.CurrentPage == vm.Settings && vm.IsSettingsSelected);
        vm.ShowStationsCommand.Execute(null);
        Check("HS-04 MX-04 BHV-24 Stations tab selects the Stations page", vm.CurrentPage == vm.Stations && vm.IsStationsSelected);

        mark = rig.Journal.Count;
        vm.Schedule.IsScheduleEnabled = true;
        Check("HS-04 MX-04 BHV-50 BHV-17 \"Follow my schedule\" on → commit(Schedule): RefreshScheduleAsync, then saved",
            rig.Since(mark) == "settings.commit:Schedule > coordinator.RefreshScheduleAsync" && rig.OnDisk().ScheduleEnabled);
        mark = rig.Journal.Count;
        vm.Schedule.IsScheduleEnabled = true;
        Check("HS-04 BHV-50 setting the same value again does nothing", rig.Journal.Count == mark);

        mark = rig.Journal.Count;
        vm.Settings.StartInTray = true;
        Check("HS-04 MX-04 BHV-60 BHV-17 \"Start in the tray\" sets the setting and saves it", rig.Since(mark) == "settings.save" && rig.OnDisk().StartInTray);

        mark = rig.Journal.Count;
        vm.Settings.SelectedFallback = vm.Settings.FallbackOptions[2];
        Check("HS-04 MX-04 BHV-61 fallback pick → FallbackStationId, commit(Stations): NotifySettingsChangedAsync, then saved",
            rig.Since(mark) == "settings.commit:Stations > coordinator.NotifySettingsChangedAsync" && rig.OnDisk().FallbackStationId == rig.Settings.Stations[1].Id);
        Check("HS-02 BHV-61 fallback picker: \"No fallback · keep retrying\" first, then each station",
            vm.Settings.FallbackOptions.Select(o => o.Name).SequenceEqual([SettingsPageViewModel.NoFallbackName, "Groove Salad", "Drone Zone", "Secret Agent"]));
        Check("HS-02 BHV-61 the Stations page marks the new fallback after the commit", vm.Stations.Rows[1].Subtitle.EndsWith(" · Fallback", StringComparison.Ordinal));
        mark = rig.Journal.Count;
        vm.Settings.SelectedFallback = vm.Settings.FallbackOptions[0];
        Check("HS-02 BHV-61 \"No fallback\" clears it", rig.Settings.FallbackStationId == null && rig.OnDisk().FallbackStationId == null && rig.Since(mark).StartsWith("settings.commit:Stations", StringComparison.Ordinal));

        await vm.Settings.OpenSettingsFolderCommand.ExecuteAsync();
        Check("HS-04 BHV-63 \"Open settings folder\" reveals the data directory", rig.Reveal.Paths.SequenceEqual([rig.Paths.DataDirectory]));
        rig.Reveal.Error = new DirectoryNotFoundException("gone");
        await vm.Settings.OpenSettingsFolderCommand.ExecuteAsync();
        Check("HS-04 BHV-63 a reveal failure is logged and shown, not swallowed",
            rig.Log.HasEvent("ui.reveal_failed") && rig.Recorder!.Messages.Any(m => m.Title == UiText.OpenFolderFailedTitle && m.Message == "gone"));
        foreach (var error in new Exception[]
                 {
                     new TimeoutException("Finder didn't respond within 10 seconds."), new TaskCanceledException("cancelled"),
                     new InvalidOperationException("Finder could not open the folder: x"), new System.ComponentModel.Win32Exception(2, "no explorer"),
                 })
        {
            rig.Reveal.Error = error;
            var shown = rig.Recorder!.Messages.Count;
            await vm.Settings.OpenSettingsFolderCommand.ExecuteAsync();
            Check($"F10 BHV-63 a reveal {error.GetType().Name} shows the error dialog with its message",
                rig.Recorder.Messages.Count == shown + 1 && rig.Recorder.Messages[^1].Title == UiText.OpenFolderFailedTitle && rig.Recorder.Messages[^1].Message == error.Message);
        }
        rig.Reveal.Error = null;
    }

    private static async Task VolumeRules()
    {
        await using var rig = UiRig.CreateViewModels();
        var vm = rig.ViewModel;
        var mark = rig.Journal.Count;
        vm.Volume = 42;
        vm.Volume = 150;
        vm.Volume = -5;
        vm.Volume = 33.6;
        Check("HS-13 BHV-28 slider changes forward clamped, rounded values: 42, 100, 0, 34",
            rig.Since(mark) == "coordinator.SetVolumeAsync:42 > coordinator.SetVolumeAsync:100 > coordinator.SetVolumeAsync:0 > coordinator.SetVolumeAsync:34");
        Check("HS-13 BHV-28 the label follows the slider", vm.VolumeLabel == "34%");
        mark = rig.Journal.Count;
        await vm.PersistVolumeCommand.ExecuteAsync();
        Check("HS-04 BHV-28 BHV-17 releasing the slider persists (a save only)", rig.Since(mark) == "settings.save");
    }

    // ─── HS-02: station editor ───

    private static async Task StationEditorValidation()
    {
        await using var rig = UiRig.CreateViewModels();
        var rec = rig.Recorder!;
        var page = rig.ViewModel.Stations;
        var focus = new List<string>();

        rec.StationScripts.Enqueue(editor =>
        {
            Check("HS-02 BHV-52 add dialog: title \"Add a frequency\", no delete", editor.Title == "Add a frequency" && !editor.CanDelete);
            editor.CancelCommand.Execute(null);
            return Task.CompletedTask;
        });
        var mark = rig.Journal.Count;
        await page.AddCommand.ExecuteAsync();
        Check("HS-02 BHV-52 Cancel adds nothing and commits nothing", rig.Settings.Stations.Count == 3 && rig.Journal.Count == mark && !rig.SavedToDisk);

        rec.StationScripts.Enqueue(editor =>
        {
            editor.FocusRequested += (_, field) => focus.Add(field);
            editor.Name = "   ";
            editor.Url = "https://ok.example.org/stream";
            editor.SaveCommand.Execute(null);
            Check("HS-02 BHV-52 missing name: \"Give this station a name.\", focus goes to Name", editor.Error == "Give this station a name." && editor.HasError && focus[^1] == "Name");
            editor.Name = "New";
            Check("HS-02 BHV-52 any edit clears the stale message", editor.Error == null && !editor.HasError);
            foreach (var bad in new[] { "ftp://x.example.org/a", "not a url", "https://", "radio.example.org/live" })
            {
                editor.Url = bad;
                editor.SaveCommand.Execute(null);
                Check($"HS-02 BHV-52 invalid URL \"{bad}\": \"Enter a valid HTTP or HTTPS stream URL.\", focus goes to Url",
                    editor.Error == "Enter a valid HTTP or HTTPS stream URL." && focus[^1] == "Url");
            }
            editor.Name = "  Kosmos  ";
            editor.Tag = "   ";
            editor.Url = "  http://radio.example.org/kosmos  ";
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        mark = rig.Journal.Count;
        await page.AddCommand.ExecuteAsync();
        var added = rig.Settings.Stations[^1];
        Check("HS-02 BHV-52 save trims values and defaults the description to \"Internet radio\"",
            added is { Name: "Kosmos", Tag: "Internet radio", Url: "http://radio.example.org/kosmos" });
        Check("HS-02 BHV-52 MX-01 add commits Stations (coordinator told), saves, and the page shows it",
            rig.Since(mark) == "settings.commit:Stations > coordinator.NotifySettingsChangedAsync"
            && rig.OnDisk().Stations.Any(s => s.Id == added.Id && s.Name == "Kosmos") && page.Rows[^1].Name == "Kosmos" && page.CountText == "04  SAVED FREQUENCIES");

        rec.StationScripts.Enqueue(editor =>
        {
            Check("HS-02 BHV-52 edit dialog: title \"Edit station\", fields prefilled, delete offered",
                editor.Title == "Edit station" && editor.Name == "Kosmos" && editor.Url == "http://radio.example.org/kosmos" && editor.CanDelete && editor.DeleteLabel == "Delete station");
            editor.Name = "Kosmos 93.6";
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await page.Rows[^1].EditCommand.ExecuteAsync();
        Check("HS-02 BHV-52 MX-01 edit keeps the station id and persists the new name",
            rig.Settings.Stations[^1].Id == added.Id && rig.OnDisk().Stations.Single(s => s.Id == added.Id).Name == "Kosmos 93.6" && page.Rows[^1].Name == "Kosmos 93.6");
        Check("HS-02 BHV-52 field limits: name 100, description 160, URL 2048",
            StationEditorViewModel.NameMaxLength == 100 && StationEditorViewModel.TagMaxLength == 160 && StationEditorViewModel.UrlMaxLength == 2048);
    }

    private static async Task StationDeleteFlow()
    {
        await using var rig = UiRig.CreateViewModels();
        var rec = rig.Recorder!;
        var page = rig.ViewModel.Stations;
        var drone = rig.Settings.Stations[1];
        rig.Settings.FallbackStationId = drone.Id;
        rig.Settings.LastStationId = drone.Id;
        rig.Settings.Schedule.Add(new ScheduleEntry { StationId = drone.Id, Time = "08:00", Days = [DayOfWeek.Monday] });
        rig.Settings.Schedule.Add(new ScheduleEntry { StationId = drone.Id, Time = "09:00", Days = [DayOfWeek.Tuesday] });
        rig.Settings.Schedule.Add(new ScheduleEntry { StationId = rig.Settings.Stations[0].Id, Time = "10:00", Days = [DayOfWeek.Monday] });
        page.Refresh();

        rec.ConfirmAnswers.Enqueue(false);
        rec.StationScripts.Enqueue(editor => editor.DeleteCommand.ExecuteAsync());
        var mark = rig.Journal.Count;
        await page.Rows[1].EditCommand.ExecuteAsync();
        Check("HS-02 BHV-53 delete asks first: \"Delete Drone Zone and its 2 schedule slot(s)?\" with Delete/Cancel",
            rec.Confirmations.Count == 1 && rec.Confirmations[0] == ("Delete station", "Delete Drone Zone and its 2 schedule slot(s)?", "Delete", "Cancel"));
        Check("HS-02 BHV-53 declining keeps the station, its slots and references", rig.Settings.Stations.Contains(drone) && rig.Settings.Schedule.Count == 3 && rig.Journal.Count == mark);

        rec.ConfirmAnswers.Enqueue(true);
        rec.StationScripts.Enqueue(editor => editor.DeleteCommand.ExecuteAsync());
        mark = rig.Journal.Count;
        await page.Rows[1].EditCommand.ExecuteAsync();
        Check("HS-02 BHV-53 confirming: ForgetStationAsync first, then one commit of Stations|Schedule (notify, refresh, save)",
            rig.Since(mark) == $"coordinator.ForgetStationAsync:{drone.Id} > settings.commit:Stations, Schedule > coordinator.NotifySettingsChangedAsync > coordinator.RefreshScheduleAsync");
        var disk = rig.OnDisk();
        Check("HS-02 BHV-53 MX-01 the station, its slots, the fallback and last-station references are gone on disk",
            disk.Stations.All(s => s.Id != drone.Id) && disk.Schedule.Count == 1 && disk.FallbackStationId == null && disk.LastStationId == null);
        Check("HS-02 BHV-53 the page re-renders without it", page.Rows.Count == 2 && page.CountText == "02  SAVED FREQUENCIES");
        Check("HS-02 BHV-53 the one-slot wording has no slot clause", UiText.DeleteStationQuestion(new Station { Name = "Solo" }, 0) == "Delete Solo?");

        // BHV-51: the empty state.
        foreach (var row in page.Rows.ToList())
        {
            rec.ConfirmAnswers.Enqueue(true);
            rec.StationScripts.Enqueue(editor => editor.DeleteCommand.ExecuteAsync());
            await row.EditCommand.ExecuteAsync();
        }
        Check("HS-01 BHV-51 no stations: empty state and \"00  SAVED FREQUENCIES\"",
            page.IsEmpty && page.Rows.Count == 0 && page.CountText == "00  SAVED FREQUENCIES" && page.EmptyText.StartsWith("Start with a station you love", StringComparison.Ordinal));
        Check("HS-01 BHV-55 no stations: \"+  Add time slot\" is disabled", !rig.ViewModel.Schedule.AddCommand.CanExecute(null));
    }

    // ─── HS-02: slot editor ───

    private static async Task SlotEditorValidation()
    {
        await using var rig = UiRig.CreateViewModels();
        var rec = rig.Recorder!;
        var page = rig.ViewModel.Schedule;
        var focus = new List<string>();
        page.SelectDay(DayOfWeek.Wednesday);

        rec.ScheduleScripts.Enqueue(editor =>
        {
            editor.FocusRequested += (_, field) => focus.Add(field);
            Check("HS-02 BHV-56 add dialog: first station, 08:00, the selected day (Wed) pre-checked, enabled",
                editor.Title == "Plan your next switch" && editor.SelectedStation == rig.Settings.Stations[0] && editor.Time == "08:00"
                && editor.Days.Where(d => d.IsChecked).Select(d => d.Day).SequenceEqual([DayOfWeek.Wednesday]) && editor.Enabled && !editor.CanDelete);
            foreach (var bad in new[] { "8", "7:05", "24:00", "08:60", "8h30", "", "08:00:00" })
            {
                editor.Time = bad;
                editor.SaveCommand.Execute(null);
                Check($"HS-02 BHV-56 invalid time \"{bad}\": \"Use a 24-hour time, such as 08:30 or 21:00.\", focus goes to Time",
                    editor.Error == "Use a 24-hour time, such as 08:30 or 21:00." && focus[^1] == "Time");
            }
            editor.Time = " 07:05 ";
            foreach (var day in editor.Days) day.IsChecked = false;
            editor.SaveCommand.Execute(null);
            Check("HS-02 BHV-56 no day: \"Choose at least one day.\"", editor.Error == "Choose at least one day.");
            editor.WeekdaysCommand.Execute(null);
            Check("HS-02 BHV-56 Weekdays preset checks Mon–Fri", editor.Days.Where(d => d.IsChecked).Select(d => d.Day).SequenceEqual(UiText.Week[..5]) && editor.Error == null);
            editor.WeekendCommand.Execute(null);
            Check("HS-02 BHV-56 Weekend preset checks Sat–Sun", editor.Days.Where(d => d.IsChecked).Select(d => d.Day).SequenceEqual(UiText.Week[5..]));
            editor.EveryDayCommand.Execute(null);
            Check("HS-02 BHV-56 Every day preset checks all seven", editor.Days.All(d => d.IsChecked));
            editor.SelectedStation = null;
            editor.SaveCommand.Execute(null);
            Check("HS-02 BHV-56 no station: \"Choose a station first.\"", editor.Error == "Choose a station first." && focus[^1] == "SelectedStation");
            editor.SelectedStation = rig.Settings.Stations[2];
            editor.Label = "  Evening  ";
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        var mark = rig.Journal.Count;
        await page.AddCommand.ExecuteAsync();
        var slot = rig.Settings.Schedule.Single();
        Check("HS-02 BHV-56 save trims the time (\" 07:05 \" → \"07:05\") and the label",
            slot is { Time: "07:05", Label: "Evening", Enabled: true } && slot.StationId == rig.Settings.Stations[2].Id && slot.Days.Count == 7);
        Check("HS-02 BHV-56 MX-01 save commits Schedule (RefreshScheduleAsync), persists and re-renders the day",
            rig.Since(mark) == "settings.commit:Schedule > coordinator.RefreshScheduleAsync" && rig.OnDisk().Schedule.Single().Time == "07:05"
            && page.Slots.Count == 1 && page.Slots[0].StationName == "Secret Agent" && page.Slots[0].Subtitle == "Evening · Mon, Tue, Wed, Thu, Fri, Sat, Sun");
        Check("HS-02 BHV-56 field limits: label 150, time 5", ScheduleEditorViewModel.LabelMaxLength == 150 && ScheduleEditorViewModel.TimeMaxLength == 5);
    }

    private static async Task SlotConflictAndEdit()
    {
        await using var rig = UiRig.CreateViewModels();
        var rec = rig.Recorder!;
        var page = rig.ViewModel.Schedule;
        var existing = new ScheduleEntry { StationId = rig.Settings.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Monday, DayOfWeek.Tuesday] };
        var disabled = new ScheduleEntry { StationId = rig.Settings.Stations[1].Id, Time = "12:00", Days = [DayOfWeek.Monday], Enabled = false };
        var missing = new ScheduleEntry { StationId = Guid.NewGuid(), Time = "06:00", Days = [DayOfWeek.Monday] };
        rig.Settings.Schedule.AddRange([existing, disabled, missing]);
        page.Refresh();
        Check("HS-01 BHV-55 Monday's slots are ordered by time", page.Slots.Select(s => s.Time).SequenceEqual(["06:00", "09:00", "12:00"]));
        Check("HS-01 BHV-55 row texts: \"Missing station\", \"Scheduled switch\", \"Disabled\" · days",
            page.Slots[0].StationName == "Missing station" && page.Slots[1].Subtitle == "Scheduled switch · Mon, Tue" && page.Slots[2].Subtitle == "Disabled · Mon"
            && page.Slots[1].IsEnabled && !page.Slots[2].IsEnabled);
        page.SelectDay(DayOfWeek.Sunday);
        Check("HS-01 BHV-55 an empty day: \"No switches on Sunday. …\"", page.IsEmpty && page.EmptyText == "No switches on Sunday. Add a time slot to tune in automatically.");
        page.SelectDay(DayOfWeek.Monday);

        rec.ScheduleScripts.Enqueue(editor =>
        {
            editor.Time = "09:00";
            editor.SaveCommand.Execute(null);
            Check("HS-02 BHV-57 conflict warning: same time, overlapping day",
                editor.Error == "Another enabled slot already starts at this time on one of those days.");
            editor.Enabled = false;
            editor.SaveCommand.Execute(null);
            Check("HS-02 BHV-57 a disabled candidate never conflicts", editor.Result == EditorResult.Saved);
            return Task.CompletedTask;
        });
        await page.AddCommand.ExecuteAsync();
        Check("HS-02 BHV-57 the disabled duplicate was saved", rig.Settings.Schedule.Count(e => e.Time == "09:00") == 2);

        rec.ScheduleScripts.Enqueue(editor =>
        {
            editor.Time = "12:00";
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await page.AddCommand.ExecuteAsync();
        Check("HS-02 BHV-57 a disabled existing slot is ignored", rig.Settings.Schedule.Count(e => e.Time == "12:00") == 2);

        var row = page.Slots.First(s => s.Entry == existing);
        rec.ScheduleScripts.Enqueue(editor =>
        {
            Check("HS-02 BHV-56 edit dialog: prefilled from the slot, delete offered",
                editor.Title == "Edit time slot" && editor.Time == "09:00" && editor.SelectedStation == rig.Settings.Stations[0]
                && editor.Days.Where(d => d.IsChecked).Select(d => d.Day).SequenceEqual([DayOfWeek.Monday, DayOfWeek.Tuesday]) && editor.DeleteLabel == "Delete slot");
            editor.SaveCommand.Execute(null);
            Check("HS-02 BHV-57 editing a slot does not conflict with itself", editor.Result == EditorResult.Saved);
            return Task.CompletedTask;
        });
        await row.EditCommand.ExecuteAsync();
        var edited = rig.Settings.Schedule.Where(e => e.Id == existing.Id).ToList();
        Check("HS-02 BHV-56 editing keeps the slot id and replaces the entry in place", edited.Count == 1 && rig.OnDisk().Schedule.Count(e => e.Id == existing.Id) == 1);
    }

    private static async Task SlotDeleteFlow()
    {
        await using var rig = UiRig.CreateViewModels();
        var rec = rig.Recorder!;
        var page = rig.ViewModel.Schedule;
        var slot = new ScheduleEntry { StationId = rig.Settings.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Monday, DayOfWeek.Friday] };
        rig.Settings.Schedule.Add(slot);
        page.Refresh();

        rec.ConfirmAnswers.Enqueue(false);
        rec.ScheduleScripts.Enqueue(editor => editor.DeleteCommand.ExecuteAsync());
        var mark = rig.Journal.Count;
        await page.Slots[0].EditCommand.ExecuteAsync();
        Check("HS-02 BHV-58 OQ-10 delete slot asks first: \"Delete the 09:00 switch to Groove Salad? It repeats on Mon, Fri.\"",
            rec.Confirmations.Single() == ("Delete slot", "Delete the 09:00 switch to Groove Salad? It repeats on Mon, Fri.", "Delete", "Cancel"));
        Check("HS-02 BHV-58 declining keeps the slot", rig.Settings.Schedule.Contains(slot) && rig.Journal.Count == mark);

        rec.ConfirmAnswers.Enqueue(true);
        rec.ScheduleScripts.Enqueue(editor => editor.DeleteCommand.ExecuteAsync());
        await page.Slots[0].EditCommand.ExecuteAsync();
        Check("HS-02 BHV-58 MX-01 confirming removes it, commits Schedule and persists",
            rig.Settings.Schedule.Count == 0 && rig.Since(mark) == "settings.commit:Schedule > coordinator.RefreshScheduleAsync" && rig.OnDisk().Schedule.Count == 0 && page.IsEmpty);
    }

    // ─── B2: start times are stored as invariant "HH:mm" whatever the culture ───

    private static async Task SlotTimeWithDottedInput()
    {
        await using var rig = UiRig.CreateViewModels();
        var rec = rig.Recorder!;
        var page = rig.ViewModel.Schedule;

        rec.ScheduleScripts.Enqueue(editor =>
        {
            foreach (var bad in new[] { "8.30", "08,30", "08.3", "24.00", "08.60" })
            {
                editor.Time = bad;
                editor.SaveCommand.Execute(null);
                Check($"B2 dotted input \"{bad}\" is still rejected", editor.Error == "Use a 24-hour time, such as 08:30 or 21:00." && editor.Result == EditorResult.Cancelled);
            }
            editor.Time = "08.30";
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await page.AddCommand.ExecuteAsync();
        Check("B2 typing \"08.30\" saves \"08:30\" (in memory and on disk)",
            rig.Settings.Schedule.Single().Time == "08:30" && rig.OnDisk().Schedule.Single().Time == "08:30");

        var legacy = new ScheduleEntry { StationId = rig.Settings.Stations[1].Id, Time = "08.45", Days = [DayOfWeek.Monday] };
        var nine = new ScheduleEntry { StationId = rig.Settings.Stations[1].Id, Time = "09:00", Days = [DayOfWeek.Monday] };
        rig.Settings.Schedule.AddRange([nine, legacy]);
        page.Refresh();
        Check("B2 a legacy \"08.45\" row sorts by its time, between 08:30 and 09:00",
            page.Slots.Select(r => r.Entry).SequenceEqual([rig.Settings.Schedule[0], legacy, nine]));

        string? shown = null;
        rec.ScheduleScripts.Enqueue(editor =>
        {
            shown = editor.Time;
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await page.Slots[1].EditCommand.ExecuteAsync();
        var resaved = rig.Settings.Schedule.Single(e => e.Id == legacy.Id);
        Check($"B2 reopening a legacy \"08.45\" slot shows \"08:45\" (was \"{shown}\")", shown == "08:45");
        Check("B2 saving it untouched writes \"08:45\" and keeps the slot id",
            resaved.Time == "08:45" && rig.OnDisk().Schedule.Single(e => e.Id == legacy.Id).Time == "08:45");
    }

    private static async Task SlotTimeUnderDotCultures()
    {
        foreach (var name in new[] { "da-DK", "fi-FI" })
        {
            var culture = CultureInfo.GetCultureInfo(name);
            if (new TimeOnly(8, 30).ToString("HH:mm", culture) != "08.30")
            {
                Skip($"B2 save under {name}", $"{name} doesn't format HH:mm with a dot here (no ICU culture data)");
                continue;
            }
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = culture;
                // Monday 08:29 UTC; the computer is on UTC, so the new Monday 08:30 slot is a minute away.
                await using var rig = UiRig.CreateViewModels(realCoordinator: true, now: new DateTimeOffset(2026, 9, 14, 8, 29, 0, TimeSpan.Zero),
                    seed: s => s.ScheduleEnabled = true);
                await rig.Coordinator.StartScheduleAsync();
                rig.Recorder!.ScheduleScripts.Enqueue(editor =>
                {
                    editor.Time = "08:30";
                    editor.SaveCommand.Execute(null);
                    return Task.CompletedTask;
                });
                await rig.ViewModel.Schedule.AddCommand.ExecuteAsync();
                var slot = rig.Settings.Schedule.Single();
                Check($"B2 under {name} the slot is stored as \"08:30\" (was \"{slot.Time}\"), in memory and on disk",
                    slot.Time == "08:30" && rig.OnDisk().Schedule.Single().Time == "08:30");
                Check($"B2 under {name} UP NEXT reads \"UP NEXT · Mon 08:30  /  Groove Salad\" (was \"{rig.ViewModel.UpNextText}\")",
                    rig.ViewModel.UpNextText == "UP NEXT · Mon 08:30  /  Groove Salad");
                Check($"B2 under {name} a zoned row's next start reads \"Next: Mon 08:30 your time\"",
                    UiText.NextStart(new DateTime(2026, 9, 14, 8, 30, 0), enabled: true) == "Next: Mon 08:30 your time");

                // Saving at 08:29 already catches up last Monday's 08:30 occurrence; the tick must fire this Monday's.
                var starts = rig.Engine!.Starts.Count;
                var fired = rig.Log.Entries.Count(e => e.EventName == "schedule.fired");
                rig.Clock.Advance(TimeSpan.FromMinutes(1));
                await rig.Real!.OnTickAsync(CancellationToken.None);
                Check($"B2 under {name} the slot fires at 08:30 and opens its station",
                    rig.Log.Entries.Count(e => e.EventName == "schedule.fired") == fired + 1 && rig.Engine.WaitForStarts(starts + 1, TimeSpan.FromSeconds(5))
                    && rig.Engine.Starts[starts].Source.Url.ToString() == rig.Settings.Stations[0].Url);
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }
    }

    // ─── Settings page, launch at login (BHV-59, HS-13, DOD-09) ───

    private static async Task SettingsPage()
    {
        await using var rig = UiRig.CreateViewModels();
        var page = rig.ViewModel.Settings;
        Check("HS-06 BHV-60 start-in-tray label and help text",
            page.StartInTrayLabel == "Start in the tray when opened normally"
            && page.StartupHelp == "Closing the window keeps your radio running. Choose Quit DialShift in the tray to exit.");
        Check("HS-02 BHV-61 fallback help text",
            page.FallbackHelp == "Retry a failed stream, then use this station as a fallback. Try the original again every 2 minutes.");
        Check("HS-13 BHV-62 About card: version line and tagline", page.VersionText == "DialShift  /  " + UiRig.Version && page.Tagline.Length > 0);
        Check("BHV-59 launch-at-login label in each platform's words: Windows \"sign in\", macOS \"log in\"",
            SettingsPageViewModel.WindowsLaunchAtLoginLabel == "Launch DialShift in the tray when I sign in"
            && SettingsPageViewModel.MacLaunchAtLoginLabel == "Launch DialShift in the tray when I log in"
            && page.LaunchAtLoginLabel == (OperatingSystem.IsWindows() ? SettingsPageViewModel.WindowsLaunchAtLoginLabel : SettingsPageViewModel.MacLaunchAtLoginLabel));
    }

    private static async Task LaunchAtLogin()
    {
        await using (var rig = UiRig.CreateViewModels(seed: s => s.LaunchAtLogin = true))
        {
            var page = rig.ViewModel.Settings;
            rig.Startup.Status = new StartupRegistrationStatus(false, "The login item points to /Old/DialShift.app, not this app.");
            var raised = 0;
            page.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(page.LaunchAtLogin)) raised++; };
            await rig.ViewModel.InitializeAsync();
            Check("HS-13 BHV-59 MX-15 the checkbox shows the verified OS state, not the stored flag (stale entry → off)",
                !page.LaunchAtLogin && raised == 1 && rig.Startup.GetCalls == 1 && rig.Startup.SetCalls.Count == 0);
            Check("HS-13 BHV-59 the stale entry's reason is shown inline", page.HasStartupDiagnostic && page.StartupDiagnostic!.Contains("/Old/DialShift.app", StringComparison.Ordinal));
            Check("HS-13 BHV-59 the stored flag is corrected to the verified state and saved", !rig.Settings.LaunchAtLogin && !rig.OnDisk().LaunchAtLogin);
            Check("HS-13 DOD-09 the startup check logs startup_registration.result (verified + stored + diagnostic)",
                rig.Log.Entries.Any(e => e.EventName == "startup_registration.result" && e.Message == "check enabled=False stored=True diagnostic=True"));
        }

        await using (var rig = UiRig.CreateViewModels())
        {
            var page = rig.ViewModel.Settings;
            await rig.ViewModel.InitializeAsync();
            rig.Startup.Hold = new TaskCompletionSource();
            page.LaunchAtLogin = true;
            Check("HS-13 BHV-59 while the write is in flight the checkbox is disabled and further clicks are ignored",
                page.IsStartupBusy && !page.IsStartupEditable && Ignore(() => page.LaunchAtLogin = false) && rig.Startup.SetCalls.SequenceEqual([true]));
            rig.Startup.Hold.SetResult();
            await Wait.Until(() => !page.IsStartupBusy);
            Check("HS-13 BHV-59 enabling succeeds: one call, checked, no diagnostic, saved",
                page.LaunchAtLogin && page.IsStartupEditable && !page.HasStartupDiagnostic && rig.Startup.SetCalls.SequenceEqual([true]) && rig.OnDisk().LaunchAtLogin);
            Check("HS-13 DOD-09 the write logs startup_registration.result",
                rig.Log.Entries.Any(e => e.EventName == "startup_registration.result" && e.Message == "requested=True enabled=True diagnostic=False"));
        }

        await using (var rig = UiRig.CreateViewModels())
        {
            var page = rig.ViewModel.Settings;
            await rig.ViewModel.InitializeAsync();
            rig.Startup.SetResult = _ => new StartupRegistrationStatus(false, "Permission denied writing the login item.");
            var values = new List<bool>();
            page.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(page.LaunchAtLogin)) values.Add(page.LaunchAtLogin); };
            page.LaunchAtLogin = true;
            await Wait.Until(() => !page.IsStartupBusy);
            Check("HS-13 BHV-59 a failed write reverts the checkbox to the verified state (on, then back off)", !page.LaunchAtLogin && values.SequenceEqual([true, false]));
            Check("HS-13 BHV-59 the revert does NOT call the registration a second time (legacy double-call bug)", rig.Startup.SetCalls.SequenceEqual([true]));
            Check("HS-13 BHV-59 the failure reason is shown inline", page.StartupDiagnostic == "Permission denied writing the login item." && page.HasStartupDiagnostic);
            Check("HS-13 BHV-59 the stored flag stays off", !rig.Settings.LaunchAtLogin);

            rig.Startup.SetResult = _ => new StartupRegistrationStatus(false);
            page.LaunchAtLogin = true;
            await Wait.Until(() => !page.IsStartupBusy);
            Check("HS-13 BHV-59 a mismatch without a reason still explains: \"Couldn't update startup.\"",
                !page.LaunchAtLogin && page.StartupDiagnostic == "Couldn't update startup." && rig.Startup.SetCalls.Count == 2);

            rig.Startup.SetResult = null;
            rig.Startup.Throw = new UnauthorizedAccessException("denied");
            page.LaunchAtLogin = true;
            await Wait.Until(() => !page.IsStartupBusy);
            Check("HS-13 BHV-59 a throwing registration is logged, shown inline and reverts",
                !page.LaunchAtLogin && page.StartupDiagnostic == "denied" && rig.Log.Entries.Any(e => e.EventName == "startup_registration.result" && e.Level == Fakes.AppLogLevel.Error));
        }
    }

    private static bool Ignore(Action action)
    {
        action();
        return true;
    }

    // ─── HS-07: settings recovered, save failed ───

    private static async Task RecoveryNotice()
    {
        await using var rig = UiRig.CreateViewModels(settingsJson: "{ this is not json");
        var backups = Directory.GetFiles(rig.Paths.DataDirectory, "settings.json.unreadable-*");
        Check("HS-07 BHV-03 a corrupt settings.json is backed up as settings.json.unreadable-* and defaults load",
            backups.Length == 1 && rig.Store.Warning != null && rig.Settings.Stations.Count == 3 && File.ReadAllText(backups[0]) == "{ this is not json");
        await rig.ViewModel.ShowSettingsRecoveredAsync(rig.Store.Warning);
        await rig.ViewModel.ShowSettingsRecoveredAsync(rig.Store.Warning);
        Check("HS-07 BHV-03 the \"DialShift · Settings recovered\" notice is shown once, naming the backup",
            rig.Recorder!.Messages.Count == 1 && rig.Recorder.Messages[0].Title == "DialShift · Settings recovered" && rig.Recorder.Messages[0].Message.Contains(Path.GetFileName(backups[0]), StringComparison.Ordinal));
        Check("HS-07 BHV-03 defaults reach disk only at the next save", File.ReadAllText(rig.Paths.SettingsFile) == "{ this is not json");

        await using var clean = UiRig.CreateViewModels();
        await clean.ViewModel.ShowSettingsRecoveredAsync(clean.Store.Warning);
        Check("HS-07 BHV-03 no warning, no notice", clean.Recorder!.Messages.Count == 0);
    }

    private static async Task SaveFailure()
    {
        await using (var rig = UiRig.CreateViewModels())
        {
            await rig.SettingsService.SaveAsync();
            var before = File.ReadAllText(rig.Paths.SettingsFile);
            // A directory where the store writes its temp file makes every save fail on both OSes, even as root.
            Directory.CreateDirectory(rig.Paths.SettingsFile + ".tmp");
            rig.Settings.Volume = 11;
            var ok = await rig.SettingsService.SaveAsync();
            Check("HS-07 BHV-16 a failed save returns false and logs settings.save_failed", !ok && rig.Log.HasEvent("settings.save_failed"));
            Check("HS-07 BHV-16 it shows \"DialShift · Save failed\" / \"Couldn't save your changes: <reason>\"",
                rig.Recorder!.Messages.Count == 1 && rig.Recorder.Messages[0].Title == "DialShift · Save failed"
                && rig.Recorder.Messages[0].Message.StartsWith("Couldn't save your changes: ", StringComparison.Ordinal) && rig.Recorder.Messages[0].Message.Length > 28);
            Check("HS-07 BHV-16 the save is atomic: the previous settings.json is untouched", File.ReadAllText(rig.Paths.SettingsFile) == before);

            var mark = rig.Journal.Count;
            await rig.ViewModel.TogglePlayCommand.ExecuteAsync();
            Check("HS-07 BHV-16 a command whose save fails still ran, and the failure is shown again (no crash)",
                rig.Since(mark) == "coordinator.ToggleAsync > settings.save" && rig.Recorder.Messages.Count == 2);
        }

        await using (var rig = UiRig.CreateViewModels())
        {
            if (!TempDirectory.TryMakeReadOnly(rig.Paths.DataDirectory))
            {
                Skip("HS-07 BHV-16 read-only data directory → \"Save failed\"", OperatingSystem.IsWindows() ? "Unix permissions only" : "running as root: the directory stays writable");
                return;
            }
            var ok = await rig.SettingsService.SaveAsync();
            Check("HS-07 BHV-16 read-only data directory → \"Save failed\" dialog, nothing written",
                !ok && rig.Recorder!.Messages.Single().Title == UiText.SaveFailedTitle && !File.Exists(rig.Paths.SettingsFile));
        }
    }

    // ─── Real coordinator behind the view model (HS-14 texts, MX-09) ───

    private static async Task RealCoordinatorTexts()
    {
        await using var rig = UiRig.CreateViewModels(realCoordinator: true);
        var vm = rig.ViewModel;
        var engine = rig.Engine!;
        var drone = vm.Stations.Rows[1];
        await drone.ListenCommand.ExecuteAsync();
        Check("HS-14 MX-09 BHV-30 Listen: engine opens Drone Zone; the card shows \"CONNECTING…\" / \"Opening the live stream\"",
            engine.Starts.Count == 1 && engine.Starts[0].Source.Url.ToString() == drone.Station.Url && vm.StatusText == "CONNECTING…"
            && vm.TrackText == "Opening the live stream" && vm.StationTitle == "Drone Zone" && vm.PlayPauseLabel == UiText.PauseLabel && vm.IsBusy);
        Check("HS-14 BHV-30 BHV-17 the manual play set LastStationId and it was saved", rig.OnDisk().LastStationId == drone.Station.Id);
        Check("HS-14 BHV-51 the playing station's row is highlighted", drone.IsCurrent && vm.Stations.Rows.Count(r => r.IsCurrent) == 1);

        engine.RaiseState(engine.LastSessionId, PlaybackEngineState.Playing);
        Check("HS-14 MX-09 BHV-31 engine Playing → \"LIVE BROADCAST\", live styling", await Wait.Until(() => vm.StatusText == "LIVE BROADCAST") && vm.IsLive);

        await vm.TogglePlayCommand.ExecuteAsync();
        Check("HS-14 MX-09 BHV-33 Pause → \"PAUSED\", \"Press play to return to the live broadcast.\", engine stopped",
            vm.StatusText == "PAUSED" && vm.TrackText == "Press play to return to the live broadcast." && vm.PlayPauseLabel == UiText.PlayLabel && engine.ActiveSessionId == null);
        Check("HS-14 BHV-33 paused: the title keeps the station, but no row is highlighted", vm.StationTitle == "Drone Zone" && !drone.IsCurrent);

        vm.Schedule.IsScheduleEnabled = true;
        await Wait.Until(() => rig.Real!.Snapshot.Status != PlaybackStatus.Stopped);
        await vm.TogglePlayCommand.ExecuteAsync();
        await vm.TogglePlayCommand.ExecuteAsync();
        Check("HS-14 BHV-33 with the schedule on: \"PAUSED · RESUMES AT THE NEXT SCHEDULED CHANGE\"", vm.StatusText == "PAUSED · RESUMES AT THE NEXT SCHEDULED CHANGE");
    }

    private static async Task RealCoordinatorCommands()
    {
        await using var rig = UiRig.CreateViewModels(realCoordinator: true, seed: s => s.LastStationId = s.Stations[2].Id);
        var vm = rig.ViewModel;
        var engine = rig.Engine!;
        await vm.TogglePlayCommand.ExecuteAsync();
        Check("HS-04 MX-09 BHV-26 Play with nothing desired resumes the last station (Secret Agent)", engine.Starts[^1].Source.Url.ToString() == rig.Settings.Stations[2].Url);
        await vm.NextStationCommand.ExecuteAsync();
        Check("HS-04 MX-09 BHV-27 Skip after the last station wraps to the first", engine.Starts[^1].Source.Url.ToString() == rig.Settings.Stations[0].Url);

        vm.Volume = 0;
        await Wait.Until(() => engine.VolumeCalls.Count > 0);
        Check("HS-13 MX-09 BHV-28 volume 0 mutes the engine (0.0) and updates Settings.Volume", engine.VolumeCalls[^1] == 0.0 && rig.Settings.Volume == 0);
        vm.Volume = 55;
        await Wait.Until(() => engine.VolumeCalls[^1] == 0.55);
        await vm.PersistVolumeCommand.ExecuteAsync();
        Check("HS-13 BHV-28 the engine receives v/100 and the release persists 55", engine.VolumeCalls[^1] == 0.55 && rig.OnDisk().Volume == 55);

        // BHV-52: editing the URL of the station on air reconnects it as a manual play.
        var groove = rig.Settings.Stations[0];
        var startsBefore = engine.Starts.Count;
        rig.Recorder!.StationScripts.Enqueue(editor =>
        {
            editor.Url = "https://moved.example.org/groove";
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await vm.Stations.Rows[0].EditCommand.ExecuteAsync();
        Check("HS-02 MX-09 BHV-52 changing the URL of the playing station reconnects to the new URL",
            engine.Starts.Count == startsBefore + 1 && engine.Starts[^1].Source.Url.ToString() == "https://moved.example.org/groove");

        // BHV-53: deleting the station on air stops it first.
        rig.Recorder.ConfirmAnswers.Enqueue(true);
        rig.Recorder.StationScripts.Enqueue(editor => editor.DeleteCommand.ExecuteAsync());
        await vm.Stations.Rows[0].EditCommand.ExecuteAsync();
        Check("HS-02 MX-09 BHV-53 deleting the playing station stops playback and clears it",
            engine.ActiveSessionId == null && !rig.Real!.Snapshot.IsActive && rig.Settings.Stations.All(s => s.Id != groove.Id) && vm.StatusText == "PAUSED");
    }
}
