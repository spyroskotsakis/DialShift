using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using DialShift.App.Services;
using DialShift.App.Tray;
using DialShift.App.ViewModels;
using DialShift.App.Views;
using DialShift.App.Views.Dialogs;
using DialShift.Core;
using DialShift.Core.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace DialShift.App.Smoke;

/// <summary>
/// The native UI smoke run (<c>--smoke-test [--recovery-test] [--output &lt;dir&gt;]</c>; brief 1 §4.5 level 3, §7.7). It
/// replaces the WPF <c>SmokeChecks</c> and adopts upstream's <c>EditorSmokeChecks</c> (acceptance matrix §4): the real
/// app, window, tray, dialogs and playback engine, in an isolated data folder (D21). Every check is recorded as
/// <c>{name, passed, detail}</c> in <c>&lt;output&gt;/results.json</c>; the exit code is 0 only when all of them pass.
/// </summary>
/// <remarks>
/// <para>Runs on the UI thread, after startup, from <see cref="App"/>. Playback runs at volume 0 so a CI machine stays
/// silent; the engine must still report Playing. A machine whose engine can't play (for example no audio device) fails
/// the live checks with the engine's own diagnostics in the detail. Nothing is passed on its behalf.</para>
/// <para><b>Never hangs.</b> A watchdog (<see cref="Watchdog"/>, or <see cref="RecoveryWatchdog"/> with
/// <c>--recovery-test</c>) records "watchdog timeout" and hands a non-zero exit code to the quit path. If the UI thread
/// is blocked and the quit path can't run, a thread-pool timer ends the process <see cref="HardExitGrace"/> later.</para>
/// </remarks>
public sealed class SmokeRunner
{
    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan RecoveryWatchdog = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan HardExitGrace = TimeSpan.FromSeconds(30);

    /// <summary>A local port nothing listens on: every attempt fails at once (the legacy recovery check's URL).</summary>
    public const string UnavailableStreamUrl = "http://127.0.0.1:1/unavailable";

    private const string StationName = "Smoke test station";
    private const string EditedStationName = "Smoke test station (edited)";

    private static readonly TimeSpan PlayTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PlayHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SecondInstanceExitTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Lets posted UI work (coalesced snapshot refreshes of the tray and the window) run before asserting.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    /// <summary>Roots the hard-exit timer until the process ends.</summary>
    private static Timer? hardExit;

    private readonly MainWindow window;
    private readonly TrayMenuController tray;
    private readonly LaunchOptions launch;
    private readonly SmokeResults results;
    private readonly IPlaybackCoordinator coordinator;
    private readonly ISettingsService settings;
    private readonly MainWindowViewModel viewModel;
    private readonly IAppShell shell;
    private readonly AppPaths paths;
    private readonly string engine;
    private readonly NativeMenu initialRootMenu;
    private readonly TrayIcon initialTrayIcon;
    private readonly NativeMenu? initialTrayMenu;
    private readonly object statusGate = new();
    private readonly List<PlaybackStatus> statusChanges = [];
    private ScheduleEntry? slot;
    private Station? smokeStation;
    private Guid? smokeSlotId;

    private SmokeRunner(IServiceProvider services, MainWindow window, TrayMenuController tray, LaunchOptions launch, SmokeResults results, string engine)
    {
        this.window = window;
        this.tray = tray;
        this.launch = launch;
        this.results = results;
        this.engine = engine;
        coordinator = services.GetRequiredService<IPlaybackCoordinator>();
        settings = services.GetRequiredService<ISettingsService>();
        viewModel = services.GetRequiredService<MainWindowViewModel>();
        shell = services.GetRequiredService<IAppShell>();
        paths = services.GetRequiredService<AppPaths>();
        // Captured once: every later tray check compares against these instances (HS-03).
        initialRootMenu = tray.RootMenu;
        initialTrayIcon = tray.TrayIcon;
        initialTrayMenu = tray.TrayIcon.Menu;
    }

    private Settings Settings => settings.Settings;

    private PlaybackSnapshot Snapshot => coordinator.Snapshot;

    /// <summary>
    /// Runs every check and returns the process exit code: <see cref="ExitCodes.Success"/> when all passed, otherwise
    /// <see cref="ExitCodes.SmokeTestFailed"/>. Never throws. UI thread only.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, MainWindow window, TrayMenuController tray, LaunchOptions launch)
    {
        var log = services.GetRequiredService<IAppLog>();
        SmokeResults results;
        string engine;
        try
        {
            engine = services.GetRequiredService<PlaybackEngineFactory>().EngineName;
            results = CreateResults(services, launch, engine);
        }
        catch (Exception ex)
        {
            log.Error("smoke.output_failed", "Couldn't create the smoke output folder.", ex);
            return ExitCodes.SmokeTestFailed;
        }

        var limit = launch.RecoveryTest ? RecoveryWatchdog : Watchdog;
        hardExit = new Timer(_ => ForceExit(results, limit, log), null, limit + HardExitGrace, Timeout.InfiniteTimeSpan);
        log.Info("smoke.start", $"recovery={launch.RecoveryTest} output={results.OutputDirectory} watchdog={limit.TotalSeconds:0}s");

        try
        {
            var run = new SmokeRunner(services, window, tray, launch, results, engine).RunChecksAsync();
            if (await Task.WhenAny(run, Task.Delay(limit)) == run)
            {
                await run;
                results.Complete();
            }
            else
            {
                results.Complete(new SmokeCheck("Watchdog", false, $"watchdog timeout: the smoke run did not finish within {limit.TotalSeconds:0} s"));
            }
        }
        catch (Exception ex)
        {
            results.Complete(new SmokeCheck("Unhandled smoke error", false, ex.ToString()));
        }
        CopyLog(services.GetRequiredService<AppPaths>(), results);
        return results.ExitCode;
    }

    /// <summary>
    /// A smoke run whose startup failed: records the failure as the only check instead of showing the startup-failure
    /// dialog, which nobody would dismiss (HS-08 still logs <c>app.startup_failed</c>). Never throws.
    /// </summary>
    public static void RecordStartupFailure(IServiceProvider services, LaunchOptions launch, string reason)
    {
        try
        {
            var engine = services.GetService<PlaybackEngineFactory>()?.EngineName ?? "unknown";
            var results = CreateResults(services, launch, engine);
            results.Complete(new SmokeCheck("Startup", false, "startup failed: " + reason));
            CopyLog(services.GetRequiredService<AppPaths>(), results);
        }
        catch (Exception ex)
        {
            services.GetService<IAppLog>()?.Error("smoke.output_failed", "Couldn't record the smoke startup failure.", ex);
        }
    }

    private static SmokeResults CreateResults(IServiceProvider services, LaunchOptions launch, string engine)
    {
        var paths = services.GetRequiredService<AppPaths>();
        var output = Path.GetFullPath(launch.SmokeOutputDirectory ?? Path.Combine(paths.DataDirectory, "smoke"));
        return new SmokeResults(output, engine, paths.DataDirectory, services.GetRequiredService<IAppLog>());
    }

    /// <summary>The app log goes next to results.json, so a CI artifact carries the engine and coordinator diagnostics.</summary>
    private static void CopyLog(AppPaths paths, SmokeResults results)
    {
        try
        {
            if (File.Exists(paths.LogFile)) File.Copy(paths.LogFile, Path.Combine(results.OutputDirectory, "dialshift.log"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void ForceExit(SmokeResults results, TimeSpan limit, IAppLog log)
    {
        results.Complete(new SmokeCheck("Watchdog", false,
            $"watchdog timeout: the app had not quit {(limit + HardExitGrace).TotalSeconds:0} s after the smoke run started (the UI thread may be blocked)"));
        log.Error("smoke.watchdog", "The smoke run did not quit in time; forcing the process to exit.");
        Environment.Exit(ExitCodes.SmokeTestFailed);
    }

    private async Task RunChecksAsync()
    {
        coordinator.SnapshotChanged += OnSnapshotChanged;
        try
        {
            // Silent run: the engine is still asked to play and must report Playing.
            await coordinator.SetVolumeAsync(0);
            Check("Launch: window and tray", Launch());

            var stations = Settings.Stations.ToList();
            foreach (var station in stations)
                await CheckAsync("Live playback: " + station.Name, () => LivePlaybackAsync(station));
            await CheckAsync("Pause stops playback", PauseStopsPlaybackAsync);

            if (stations.Count >= 2)
            {
                await CheckAsync("Schedule catch-up selects current station", () => ScheduleCatchUpAsync(stations[0]));
                await CheckAsync("Pause holds within current slot", PauseHoldsAsync);
                await CheckAsync("Manual selection holds within current slot", () => ManualHoldsAsync(stations[1]));
                await CheckAsync("New schedule occurrence overrides manual station", NewOccurrenceOverridesAsync);
                await CheckAsync("Resume preserves pause within same slot", WakePreservesPauseAsync);
            }
            else
            {
                results.Record("Schedule checks", false, $"need at least two stations; the settings have {stations.Count}");
            }

            await TrayAndEditorChecksAsync();

            await CheckAsync("Hide to tray", HideToTrayAsync);
            await CheckAsync("Restore from tray", RestoreFromTrayAsync);
            await CheckAsync("Persistence", PersistenceAsync);
            await ScreenshotChecksAsync();

            if (launch.RecoveryTest)
            {
                await CheckAsync("Failed stream retries and plays fallback", FailedStreamPlaysFallbackAsync);
                await CheckAsync("Pause cancels fallback and retry", PauseCancelsRecoveryAsync);
            }

            Check("No error dialogs left open", NoStrayDialogs());
            await CheckAsync("Second launch activates the running instance", SecondInstanceActivatesAsync);
        }
        finally
        {
            coordinator.SnapshotChanged -= OnSnapshotChanged;
        }
    }

    private readonly record struct Outcome(bool Passed, string Detail);

    private void Check(string name, Outcome outcome) => results.Record(name, outcome.Passed, outcome.Detail);

    private async Task CheckAsync(string name, Func<Task<Outcome>> check)
    {
        Outcome outcome;
        try { outcome = await check(); }
        catch (Exception ex) { outcome = new Outcome(false, "threw " + ex); }
        Check(name, outcome);
    }

    // ---- 1. Launch -------------------------------------------------------------------------------------------------

    private Outcome Launch()
    {
        var expectVisible = !launch.StartInTray;
        var icons = TrayIcon.GetIcons(Application.Current!);
        var registered = icons is { Count: 1 } && ReferenceEquals(icons[0], initialTrayIcon);
        var menuBound = ReferenceEquals(initialTrayMenu, initialRootMenu);
        var passed = window.IsVisible == expectVisible && registered && initialTrayIcon.IsVisible && menuBound;
        return new Outcome(passed,
            $"main window visible={window.IsVisible} (expected {expectVisible}); tray icon registered={registered}, visible={initialTrayIcon.IsVisible}, " +
            $"menu is RootMenu={menuBound}; engine={engine}; data folder={paths.DataDirectory} ({paths.Source})");
    }

    // ---- 2-3. Live playback, pause ---------------------------------------------------------------------------------

    private async Task<Outcome> LivePlaybackAsync(Station station)
    {
        var logMark = LogLength();
        var row = viewModel.Stations.Rows.FirstOrDefault(r => r.Station.Id == station.Id)
            ?? throw new InvalidOperationException("The Stations page has no row for this station.");
        // The Stations page "Listen" button.
        await row.ListenCommand.ExecuteAsync();
        var (reached, elapsed) = await SmokeUi.WaitUntilAsync(() => IsLiveOn(station.Id), PlayTimeout, 250);
        if (!reached)
            return new Outcome(false, $"not Playing within {PlayTimeout.TotalSeconds:0} s ({engine}): {Describe(Snapshot)}{PlaybackLogSince(logMark)}");
        await Task.Delay(PlayHold);
        var held = IsLiveOn(station.Id);
        return new Outcome(held, held
            ? $"Playing after {elapsed.TotalSeconds:0.0} s and still Playing after a {PlayHold.TotalSeconds:0} s hold; volume {Snapshot.Volume}"
            : $"Playing after {elapsed.TotalSeconds:0.0} s but not after the {PlayHold.TotalSeconds:0} s hold: {Describe(Snapshot)}{PlaybackLogSince(logMark)}");
    }

    private async Task<Outcome> PauseStopsPlaybackAsync()
    {
        // The player card's Pause button; toggling while inactive would start playback instead, so stop directly then.
        var viaButton = Snapshot.IsActive;
        if (viaButton) await viewModel.TogglePlayCommand.ExecuteAsync();
        else await coordinator.StopAsync();
        await Task.Delay(1200);
        var s = Snapshot;
        var passed = !s.IsActive && !s.IsPlaying && viewModel.PlayPauseAutomationName == "Play";
        return new Outcome(passed, $"{(viaButton ? "Pause button" : "StopAsync (nothing was active)")}; {Describe(s)}; player button reads {viewModel.PlayPauseAutomationName}");
    }

    // ---- 4-8. Schedule ---------------------------------------------------------------------------------------------

    private async Task<Outcome> ScheduleCatchUpAsync(Station station)
    {
        var minuteAgo = DateTime.Now.AddMinutes(-1);
        slot = new ScheduleEntry
        {
            StationId = station.Id,
            Time = minuteAgo.ToString("HH:mm", CultureInfo.InvariantCulture),
            Days = [minuteAgo.DayOfWeek],
            Label = "Smoke catch-up"
        };
        Settings.Schedule.Add(slot);
        Settings.ScheduleEnabled = true;
        await settings.CommitAsync(SettingsChange.Schedule);
        var s = Snapshot;
        return new Outcome(s.DesiredStationId == station.Id && s.IsActive, $"slot {slot.Days[0]} {slot.Time} → {station.Name}; {Describe(s)}");
    }

    private async Task<Outcome> PauseHoldsAsync()
    {
        RequireSlot();
        await coordinator.StopAsync();
        await Task.Delay(1600);
        return new Outcome(!Snapshot.IsActive, $"1.6 s after pause, {Describe(Snapshot)}");
    }

    private async Task<Outcome> ManualHoldsAsync(Station manual)
    {
        RequireSlot();
        await coordinator.PlayAsync(manual.Id);
        await Task.Delay(1500);
        var s = Snapshot;
        return new Outcome(s.DesiredStationId == manual.Id && s.IsActive, $"1.5 s after playing {manual.Name}, desired={s.DesiredStationName}; {Describe(s)}");
    }

    private async Task<Outcome> NewOccurrenceOverridesAsync()
    {
        var entry = RequireSlot();
        // As the legacy check: move the slot to this minute, so the next tick sees a new occurrence.
        var now = DateTime.Now;
        entry.Time = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        entry.Days = [now.DayOfWeek];
        var (met, elapsed) = await SmokeUi.WaitUntilAsync(() => Snapshot.DesiredStationId == entry.StationId, TimeSpan.FromSeconds(3));
        return new Outcome(met, $"slot moved to {entry.Days[0]} {entry.Time}; {(met ? $"switched after {elapsed.TotalSeconds:0.0} s" : "no switch within 3 s")}; {Describe(Snapshot)}");
    }

    private async Task<Outcome> WakePreservesPauseAsync()
    {
        RequireSlot();
        await coordinator.StopAsync();
        await coordinator.NotifyWakeAsync();
        // Longer than the 2 s wake settle, so a wrongly started recovery would show.
        await Task.Delay(2500);
        var s = Snapshot;
        return new Outcome(!s.IsActive && s.Status != PlaybackStatus.SuspendedBySystem, $"2.5 s after NotifyWakeAsync while paused: {Describe(s)}");
    }

    private ScheduleEntry RequireSlot() => slot ?? throw new InvalidOperationException("The catch-up slot was not created.");

    // ---- 9. Editors and tray menu identity (HS-03) -----------------------------------------------------------------

    /// <summary>
    /// Every editor operation, each followed by the identity assertion. Order keeps the run silent and independent of the
    /// clock: the schedule is switched off first (so no edit replays the current slot), the new slot belongs to the new
    /// station, and the station is deleted last. Volume changes happen while stopped.
    /// </summary>
    private async Task TrayAndEditorChecksAsync()
    {
        await CheckAsync("Tray menu identity: follow schedule off", () => ToggleFollowScheduleAsync(expected: false));
        await CheckAsync("Station editor: cancel leaves settings unchanged", CancelStationEditorAsync);
        await CheckAsync("Tray menu identity: add station", AddStationAsync);
        await CheckAsync("Tray menu identity: edit station", EditStationAsync);
        await CheckAsync("Tray menu identity: add slot", AddSlotAsync);
        await CheckAsync("Tray menu identity: edit slot", EditSlotAsync);
        await CheckAsync("Tray menu identity: delete slot", DeleteSlotAsync);
        await CheckAsync("Tray menu identity: delete station", DeleteStationAsync);
        await CheckAsync("Tray menu identity: volume +10", () => ChangeVolumeAsync(up: true, expected: 10));
        await CheckAsync("Tray menu identity: volume −10", () => ChangeVolumeAsync(up: false, expected: 0));
        await CheckAsync("Tray menu identity: follow schedule on", () => ToggleFollowScheduleAsync(expected: true));
        // Following the schedule again replays the current slot (muted); later checks start from stopped.
        await coordinator.StopAsync();
    }

    private async Task<Outcome> ToggleFollowScheduleAsync(bool expected)
    {
        await SmokeUi.InvokeAsync(TrayItem("Follow schedule"));
        await Task.Delay(Settle);
        var item = TrayItem("Follow schedule");
        var ok = Settings.ScheduleEnabled == expected && item.IsChecked == expected && viewModel.Schedule.IsScheduleEnabled == expected;
        return TrayOutcome(ok, $"ScheduleEnabled={Settings.ScheduleEnabled}, tray check={item.IsChecked}, page toggle={viewModel.Schedule.IsScheduleEnabled} (expected {expected})");
    }

    private async Task<Outcome> CancelStationEditorAsync()
    {
        var count = Settings.Stations.Count;
        var command = viewModel.Stations.AddCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<StationEditorDialog>();
        var editor = (StationEditorViewModel)dialog.DataContext!;
        SmokeUi.Type(dialog.NameField, "Unsaved station");
        SmokeUi.Click(dialog, editor.CancelCommand);
        await SmokeUi.CompleteAsync(command, "Cancelled station editor");
        var ok = editor.Result == EditorResult.Cancelled && Settings.Stations.Count == count && !TrayStationNames().Contains("Unsaved station");
        return TrayOutcome(ok, $"result={editor.Result}, stations {count} → {Settings.Stations.Count}");
    }

    private async Task<Outcome> AddStationAsync()
    {
        var count = Settings.Stations.Count;
        var command = viewModel.Stations.AddCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<StationEditorDialog>();
        var editor = (StationEditorViewModel)dialog.DataContext!;

        SmokeUi.Click(dialog, editor.SaveCommand);
        var rejectsEmptyName = editor.HasError && dialog.IsVisible;
        SmokeUi.Type(dialog.NameField, StationName);
        SmokeUi.Type(dialog.UrlField, "not a URL");
        SmokeUi.Click(dialog, editor.SaveCommand);
        var rejectsBadUrl = editor.HasError && dialog.IsVisible;
        SmokeUi.Type(dialog.UrlField, Settings.Stations[0].Url);
        SmokeUi.Click(dialog, editor.SaveCommand);
        await SmokeUi.CompleteAsync(command, "Add station");
        await Task.Delay(Settle);

        smokeStation = Settings.Stations.FirstOrDefault(s => s.Name == StationName);
        var inTray = TrayStationNames().Contains(StationName);
        var onPage = viewModel.Stations.Rows.Any(r => r.Name == StationName);
        var ok = rejectsEmptyName && rejectsBadUrl && smokeStation != null && Settings.Stations.Count == count + 1 && inTray && onPage;
        return TrayOutcome(ok, $"empty name rejected={rejectsEmptyName}, invalid URL rejected={rejectsBadUrl}, saved={smokeStation != null}, " +
            $"in tray Stations menu={inTray}, on Stations page={onPage}");
    }

    private async Task<Outcome> EditStationAsync()
    {
        var station = smokeStation ?? throw new InvalidOperationException("The smoke station was not added.");
        var command = StationRow(station).EditCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<StationEditorDialog>();
        var editor = (StationEditorViewModel)dialog.DataContext!;
        SmokeUi.Type(dialog.NameField, EditedStationName);
        SmokeUi.Click(dialog, editor.SaveCommand);
        await SmokeUi.CompleteAsync(command, "Edit station");
        await Task.Delay(Settle);

        var names = TrayStationNames();
        var ok = station.Name == EditedStationName && names.Contains(EditedStationName) && !names.Contains(StationName);
        return TrayOutcome(ok, $"name now \"{station.Name}\"; tray Stations menu: {string.Join(", ", names)}");
    }

    private async Task<Outcome> AddSlotAsync()
    {
        var station = smokeStation ?? throw new InvalidOperationException("The smoke station was not added.");
        var day = slot?.Days[0] ?? DateTime.Now.DayOfWeek;
        viewModel.Schedule.SelectDay(day);
        var count = Settings.Schedule.Count;
        var command = viewModel.Schedule.AddCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<ScheduleEditorDialog>();
        var editor = (ScheduleEditorViewModel)dialog.DataContext!;

        dialog.StationField.SelectedItem = editor.Stations.Single(s => s.Id == station.Id);
        SmokeUi.Type(dialog.TimeField, "25:99");
        SmokeUi.Click(dialog, editor.SaveCommand);
        var rejectsBadTime = editor.HasError && dialog.IsVisible;
        // The new slot's day is preselected from the Schedule page; the catch-up slot starts at the same time that day.
        var rejectsConflict = true;
        if (slot != null)
        {
            SmokeUi.Type(dialog.TimeField, slot.Time);
            SmokeUi.Click(dialog, editor.SaveCommand);
            rejectsConflict = editor.HasError && dialog.IsVisible;
        }
        var time = FreeTime(day);
        SmokeUi.Type(dialog.LabelField, "Smoke slot");
        SmokeUi.Type(dialog.TimeField, time);
        SmokeUi.Click(dialog, editor.SaveCommand);
        await SmokeUi.CompleteAsync(command, "Add slot");
        await Task.Delay(Settle);

        smokeSlotId = Settings.Schedule.FirstOrDefault(e => e.StationId == station.Id && e.Time == time && e.Days.Contains(day))?.Id;
        var onPage = viewModel.Schedule.Slots.Any(r => r.Entry.Id == smokeSlotId);
        var ok = rejectsBadTime && rejectsConflict && smokeSlotId != null && Settings.Schedule.Count == count + 1 && onPage;
        return TrayOutcome(ok, $"invalid time rejected={rejectsBadTime}, conflict rejected={rejectsConflict}, saved {day} {time}={smokeSlotId != null}, on Schedule page={onPage}");
    }

    private async Task<Outcome> EditSlotAsync()
    {
        var (entry, dialog, editor, command) = await OpenSlotEditorAsync();
        var time = FreeTime(entry.Days[0]);
        SmokeUi.Type(dialog.TimeField, time);
        SmokeUi.Click(dialog, editor.SaveCommand);
        await SmokeUi.CompleteAsync(command, "Edit slot");
        await Task.Delay(Settle);

        var saved = Settings.Schedule.Where(e => e.Id == entry.Id).ToList();
        var ok = saved.Count == 1 && saved[0].Time == time && viewModel.Schedule.Slots.Any(r => r.Entry.Id == entry.Id && r.Time == time);
        return TrayOutcome(ok, $"time {entry.Time} → {(saved.Count == 1 ? saved[0].Time : $"{saved.Count} entries")} (expected {time}), same Id kept={saved.Count == 1}");
    }

    private async Task<Outcome> DeleteSlotAsync()
    {
        var (entry, dialog, editor, command) = await OpenSlotEditorAsync();
        SmokeUi.Click(dialog, editor.DeleteCommand);
        await ConfirmAsync();
        await SmokeUi.CompleteAsync(command, "Delete slot");
        await Task.Delay(Settle);

        var ok = Settings.Schedule.All(e => e.Id != entry.Id) && viewModel.Schedule.Slots.All(r => r.Entry.Id != entry.Id);
        return TrayOutcome(ok, $"slot {entry.Time} removed={Settings.Schedule.All(e => e.Id != entry.Id)}");
    }

    private async Task<Outcome> DeleteStationAsync()
    {
        var station = smokeStation ?? throw new InvalidOperationException("The smoke station was not added.");
        var command = StationRow(station).EditCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<StationEditorDialog>();
        var editor = (StationEditorViewModel)dialog.DataContext!;
        SmokeUi.Click(dialog, editor.DeleteCommand);
        await ConfirmAsync();
        await SmokeUi.CompleteAsync(command, "Delete station");
        await Task.Delay(Settle);

        var names = TrayStationNames();
        var ok = Settings.Stations.All(s => s.Id != station.Id) && !names.Contains(station.Name) && viewModel.Stations.Rows.All(r => r.Station.Id != station.Id);
        return TrayOutcome(ok, $"removed from settings={Settings.Stations.All(s => s.Id != station.Id)}; tray Stations menu: {string.Join(", ", names)}");
    }

    private async Task<Outcome> ChangeVolumeAsync(bool up, int expected)
    {
        var item = RootItems().FirstOrDefault(i => i.Header is { } h && h.StartsWith("Volume", StringComparison.Ordinal) && h.Contains('+') == up)
            ?? throw new InvalidOperationException($"The tray menu has no volume {(up ? "up" : "down")} item.");
        await SmokeUi.InvokeAsync(item);
        await Task.Delay(Settle);
        var ok = Settings.Volume == expected && Snapshot.Volume == expected;
        return TrayOutcome(ok, $"\"{item.Header}\": settings volume={Settings.Volume}, engine volume={Snapshot.Volume} (expected {expected})");
    }

    private async Task<(ScheduleEntry Entry, ScheduleEditorDialog Dialog, ScheduleEditorViewModel Editor, Task Command)> OpenSlotEditorAsync()
    {
        var entry = Settings.Schedule.FirstOrDefault(e => e.Id == smokeSlotId) ?? throw new InvalidOperationException("The smoke slot was not added.");
        viewModel.Schedule.SelectDay(entry.Days[0]);
        var row = viewModel.Schedule.Slots.FirstOrDefault(r => r.Entry.Id == entry.Id) ?? throw new InvalidOperationException("The Schedule page has no row for the smoke slot.");
        var command = row.EditCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<ScheduleEditorDialog>();
        return (entry, dialog, (ScheduleEditorViewModel)dialog.DataContext!, command);
    }

    /// <summary>Answers the delete confirmation dialog with its confirm button.</summary>
    private static async Task ConfirmAsync()
    {
        var confirmation = await SmokeUi.WaitForWindowAsync<MessageDialog>();
        var model = (MessageDialogViewModel)confirmation.DataContext!;
        SmokeUi.Click(confirmation, model.ConfirmCommand);
    }

    /// <summary>A start time no slot uses on <paramref name="day"/>.</summary>
    private string FreeTime(DayOfWeek day)
    {
        var used = Settings.Schedule.Where(e => e.Days.Contains(day)).Select(e => e.Time).ToHashSet();
        return Enumerable.Range(0, 24).Select(h => $"{(h + 3) % 24:00}:17").First(t => !used.Contains(t));
    }

    private StationRowViewModel StationRow(Station station) =>
        viewModel.Stations.Rows.FirstOrDefault(r => r.Station.Id == station.Id) ?? throw new InvalidOperationException($"The Stations page has no row for {station.Name}.");

    private IEnumerable<NativeMenuItem> RootItems() => tray.RootMenu.Items.OfType<NativeMenuItem>();

    private NativeMenuItem TrayItem(string header) =>
        RootItems().FirstOrDefault(i => i.Header == header) ?? throw new InvalidOperationException($"The tray menu has no \"{header}\" item.");

    private List<string> TrayStationNames() =>
        TrayItem("Stations").Menu?.Items.OfType<NativeMenuItem>().Select(i => i.Header ?? "").ToList() ?? [];

    /// <summary>The HS-03 identity assertion plus the operation's own expectation.</summary>
    private Outcome TrayOutcome(bool changeApplied, string detail)
    {
        var problems = new List<string>();
        if (!ReferenceEquals(tray.RootMenu, initialRootMenu)) problems.Add("RootMenu was replaced");
        if (!ReferenceEquals(tray.TrayIcon, initialTrayIcon)) problems.Add("the TrayIcon was replaced");
        if (!ReferenceEquals(tray.TrayIcon.Menu, initialTrayMenu)) problems.Add("TrayIcon.Menu was reassigned");
        var icons = TrayIcon.GetIcons(Application.Current!);
        if (icons is not { Count: 1 } || !ReferenceEquals(icons[0], initialTrayIcon)) problems.Add("the registered tray icons changed (SetIcons called again)");
        var identity = problems.Count == 0 ? "tray icon, TrayIcon.Menu and RootMenu are the original instances" : string.Join("; ", problems);
        return new Outcome(problems.Count == 0 && changeApplied, identity + "; " + detail);
    }

    // ---- 10-11. Hide/restore, persistence --------------------------------------------------------------------------

    private async Task<Outcome> HideToTrayAsync()
    {
        // The window's "Hide to tray" button.
        viewModel.HideToTrayCommand.Execute(null);
        var (hidden, _) = await SmokeUi.WaitUntilAsync(() => !window.IsVisible, TimeSpan.FromSeconds(2));
        return new Outcome(hidden && initialTrayIcon.IsVisible, $"window visible={window.IsVisible}, tray icon visible={initialTrayIcon.IsVisible}");
    }

    private async Task<Outcome> RestoreFromTrayAsync()
    {
        await SmokeUi.InvokeAsync(TrayItem("Open DialShift"));
        var (shown, elapsed) = await SmokeUi.WaitUntilAsync(() => window.IsVisible, TimeSpan.FromSeconds(2));
        return new Outcome(shown, $"tray \"Open DialShift\": window visible={window.IsVisible}{(shown ? $" after {elapsed.TotalMilliseconds:0} ms" : "")}");
    }

    private async Task<Outcome> PersistenceAsync()
    {
        var saved = await settings.SaveAsync();
        var store = new SettingsStore(paths.DataDirectory);
        var loaded = store.Load();
        static string Slots(Settings s) => string.Join(" | ", s.Schedule.OrderBy(e => e.Id)
            .Select(e => $"{e.Id}:{e.StationId}:{e.Time}:{string.Join(',', e.Days)}:{e.Enabled}:{e.Label}"));
        var scheduleMatches = Slots(loaded) == Slots(Settings);
        var ok = saved && store.Warning == null && scheduleMatches && loaded.Volume == Settings.Volume
            && loaded.ScheduleEnabled == Settings.ScheduleEnabled && loaded.Stations.Select(s => s.Id).SequenceEqual(Settings.Stations.Select(s => s.Id));
        return new Outcome(ok, $"saved={saved}; reloaded from {store.FilePath}: {loaded.Schedule.Count} slot(s) (match={scheduleMatches}), " +
            $"volume {loaded.Volume} (expected {Settings.Volume}), {loaded.Stations.Count} station(s), warning={store.Warning ?? "none"}");
    }

    // ---- 12. Screenshots -------------------------------------------------------------------------------------------

    private async Task ScreenshotChecksAsync()
    {
        shell.ShowMainWindow();
        var shots = new (string Name, string File, Func<string, Task<string>> Take)[]
        {
            ("Screenshot: Stations page", "stations.png", file => CapturePageAsync(MainPage.Stations, file)),
            ("Screenshot: Schedule page", "schedule.png", file => CapturePageAsync(MainPage.Schedule, file)),
            ("Screenshot: Settings page", "settings.png", file => CapturePageAsync(MainPage.Settings, file)),
            ("Screenshot: station editor", "station-editor.png", CaptureStationEditorAsync),
            ("Screenshot: schedule editor", "schedule-editor.png", CaptureScheduleEditorAsync),
            ("Screenshot: compact 780×650", "compact.png", CaptureCompactAsync)
        };
        string? unsupported = null;
        foreach (var (name, file, take) in shots)
        {
            if (unsupported != null)
            {
                results.Skip(name, unsupported);
                continue;
            }
            try
            {
                results.Record(name, true, await take(Path.Combine(results.OutputDirectory, file)));
            }
            catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException)
            {
                unsupported = "Avalonia can't render the real window to a bitmap here: " + ex.Message;
                results.Skip(name, unsupported);
            }
            catch (Exception ex)
            {
                results.Record(name, false, "threw " + ex);
            }
        }
        viewModel.SelectedPage = MainPage.Stations;
    }

    private async Task<string> CapturePageAsync(MainPage page, string file)
    {
        viewModel.SelectedPage = page;
        await Task.Delay(300);
        return Describe(SmokeUi.Capture(window, file), file);
    }

    private async Task<string> CaptureStationEditorAsync(string file)
    {
        var command = viewModel.Stations.Rows.First().EditCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<StationEditorDialog>();
        try { return Describe(SmokeUi.Capture(dialog, file), file); }
        finally
        {
            SmokeUi.Click(dialog, ((StationEditorViewModel)dialog.DataContext!).CancelCommand);
            await SmokeUi.CompleteAsync(command, "Station editor (screenshot)");
        }
    }

    private async Task<string> CaptureScheduleEditorAsync(string file)
    {
        if (slot != null) viewModel.Schedule.SelectDay(slot.Days[0]);
        var row = viewModel.Schedule.Slots.FirstOrDefault();
        var command = row != null ? row.EditCommand.ExecuteAsync() : viewModel.Schedule.AddCommand.ExecuteAsync();
        var dialog = await SmokeUi.WaitForWindowAsync<ScheduleEditorDialog>();
        try { return Describe(SmokeUi.Capture(dialog, file), file); }
        finally
        {
            SmokeUi.Click(dialog, ((ScheduleEditorViewModel)dialog.DataContext!).CancelCommand);
            await SmokeUi.CompleteAsync(command, "Schedule editor (screenshot)");
        }
    }

    private async Task<string> CaptureCompactAsync(string file)
    {
        var (width, height) = (window.Width, window.Height);
        window.Width = 780;
        window.Height = 650;
        viewModel.SelectedPage = MainPage.Stations;
        try
        {
            await Task.Delay(400);
            return Describe(SmokeUi.Capture(window, file), file) + $" (window {window.Bounds.Width:0}×{window.Bounds.Height:0} DIP)";
        }
        finally
        {
            window.Width = width;
            window.Height = height;
        }
    }

    private static string Describe(PixelSize size, string file) => $"{size.Width}×{size.Height} px → {Path.GetFileName(file)}";

    // ---- 13. Recovery (--recovery-test) ----------------------------------------------------------------------------

    private async Task<Outcome> FailedStreamPlaysFallbackAsync()
    {
        var fallback = Settings.Stations[0];
        var unavailable = new Station { Name = "Unavailable test stream", Tag = "Smoke recovery", Url = UnavailableStreamUrl };
        Settings.ScheduleEnabled = false;
        Settings.FallbackStationId = fallback.Id;
        Settings.Stations.Add(unavailable);
        await settings.CommitAsync(SettingsChange.Stations | SettingsChange.Schedule);
        lock (statusGate) statusChanges.Clear();

        await coordinator.PlayAsync(unavailable.Id);
        var (met, elapsed) = await SmokeUi.WaitUntilAsync(() =>
            Snapshot is { IsPlaying: true, Status: PlaybackStatus.Playing, IsFallback: true } s && s.CurrentStationId == fallback.Id, RecoveryTimeout, 500);
        int failures, reconnects;
        lock (statusGate)
        {
            failures = statusChanges.Count(s => s == PlaybackStatus.Failed);
            reconnects = statusChanges.Count(s => s == PlaybackStatus.Reconnecting);
        }
        var s = Snapshot;
        var ok = met && failures >= RetryPolicy.FallbackAfterFailures && reconnects >= 1 && s.DesiredStationId == unavailable.Id;
        return new Outcome(ok, (met ? $"fallback {fallback.Name} Playing after {elapsed.TotalSeconds:0} s" : $"fallback not Playing within {RecoveryTimeout.TotalSeconds:0} s")
            + $"; {failures} failure(s) and {reconnects} reconnect(s) observed (need ≥ {RetryPolicy.FallbackAfterFailures} and ≥ 1); desired={s.DesiredStationName}; {Describe(s)}");
    }

    private async Task<Outcome> PauseCancelsRecoveryAsync()
    {
        await coordinator.StopAsync();
        await Task.Delay(1500);
        var first = Snapshot;
        // Longer than the first retry backoffs (3 s, 6 s): a retry that survived the pause would have restarted by now.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var later = Snapshot;
        var ok = !first.IsPlaying && !first.IsActive && !later.IsPlaying && !later.IsActive && later.RetryInSeconds == null;
        await RemoveUnavailableStationAsync();
        return new Outcome(ok, $"1.5 s after pause: {Describe(first)}; 7.5 s after: {Describe(later)}");
    }

    private async Task RemoveUnavailableStationAsync()
    {
        var unavailable = Settings.Stations.FirstOrDefault(s => s.Url == UnavailableStreamUrl);
        if (unavailable == null) return;
        await coordinator.ForgetStationAsync(unavailable.Id);
        Settings.Stations.Remove(unavailable);
        Settings.FallbackStationId = null;
        await settings.CommitAsync(SettingsChange.Stations);
    }

    // ---- 14. Second instance ---------------------------------------------------------------------------------------

    private Outcome NoStrayDialogs()
    {
        var stray = SmokeUi.OpenWindows().Where(w => !ReferenceEquals(w, window) && w.IsVisible).ToList();
        foreach (var dialog in stray) dialog.Close();
        return new Outcome(stray.Count == 0, stray.Count == 0
            ? "no dialog besides the main window is open"
            : "closed: " + string.Join(", ", stray.Select(w => $"{w.GetType().Name} \"{w.Title}\"")));
    }

    /// <summary>
    /// The real second launch (BHV-08, brief 1 §4.5): the same executable, arguments without the smoke switches, and the
    /// same data folder, so it finds this instance's lock and pipe. It must exit 0 AND bring the hidden window back.
    /// </summary>
    private async Task<Outcome> SecondInstanceActivatesAsync()
    {
        shell.HideMainWindow();
        var (hidden, _) = await SmokeUi.WaitUntilAsync(() => !window.IsVisible, TimeSpan.FromSeconds(2));
        if (!hidden) return new Outcome(false, "couldn't hide the main window before the second launch");

        var start = SecondInstanceStartInfo();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned no process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        TimeSpan? visibleAt = null;
        var exit = process.WaitForExitAsync();
        while (!exit.IsCompleted)
        {
            if (visibleAt == null && window.IsVisible) visibleAt = clock.Elapsed;
            if (clock.Elapsed > SecondInstanceExitTimeout)
            {
                process.Kill(entireProcessTree: true);
                return new Outcome(false, $"the second process did not exit within {SecondInstanceExitTimeout.TotalSeconds:0} s and was killed; window visible={window.IsVisible}");
            }
            await Task.Delay(50);
        }
        var exitedAt = clock.Elapsed;
        if (visibleAt == null)
        {
            var (shown, after) = await SmokeUi.WaitUntilAsync(() => window.IsVisible, ActivationTimeout, 50);
            if (shown) visibleAt = exitedAt + after;
        }
        var output = (await stdout + await stderr).Trim();
        var ok = process.ExitCode == ExitCodes.Success && visibleAt != null;
        return new Outcome(ok,
            $"`{string.Join(' ', start.ArgumentList.Prepend(Path.GetFileName(start.FileName)))}` exited {process.ExitCode} after {exitedAt.TotalSeconds:0.0} s; " +
            (visibleAt is { } at ? $"main window visible {at.TotalSeconds:0.0} s after the launch" : $"main window still hidden {ActivationTimeout.TotalSeconds:0} s after it exited") +
            (output.Length > 0 ? "; output: " + output : ""));
    }

    private ProcessStartInfo SecondInstanceStartInfo()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The app's executable path is unknown.");
        var commandLine = Environment.GetCommandLineArgs();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Launched as `dotnet DialShift.dll`: the host needs the assembly path first.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(commandLine[0]);
        foreach (var arg in LaunchOptions.WithoutSmokeSwitches(commandLine.Skip(1))) start.ArgumentList.Add(arg);
        start.Environment[AppPaths.DataDirectoryOverrideVariable] = paths.DataDirectory;
        return start;
    }

    // ---- Shared ----------------------------------------------------------------------------------------------------

    private void OnSnapshotChanged(object? sender, PlaybackSnapshot value)
    {
        lock (statusGate)
        {
            if (statusChanges.Count == 0 || statusChanges[^1] != value.Status) statusChanges.Add(value.Status);
        }
    }

    private bool IsLiveOn(Guid stationId) => Snapshot is { IsPlaying: true, Status: PlaybackStatus.Playing } s && s.CurrentStationId == stationId;

    private static string Describe(PlaybackSnapshot s) =>
        $"status={s.Status}, active={s.IsActive}, playing={s.IsPlaying}, current={s.CurrentStationName ?? "none"}, text=\"{s.StatusText}\", track=\"{s.TrackText}\"";

    private long LogLength()
    {
        try { return new FileInfo(paths.LogFile).Length; }
        catch (IOException) { return 0; }
    }

    /// <summary>The engine's own warnings and errors since <paramref name="offset"/> (at most the last three), for a failed playback check.</summary>
    private string PlaybackLogSince(long offset)
    {
        try
        {
            using var stream = new FileStream(paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The log may have rotated since the mark; then read it from the start.
            if (offset <= stream.Length) stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n')
                .Where(l => l.Contains("\"event\":\"playback.", StringComparison.Ordinal)
                    && (l.Contains("\"level\":\"warn\"", StringComparison.Ordinal) || l.Contains("\"level\":\"error\"", StringComparison.Ordinal)))
                .TakeLast(3).ToList();
            return lines.Count == 0 ? "; no playback warnings in the log" : "; log: " + string.Join(" ", lines.Select(l => l.Trim()));
        }
        catch (IOException) { return ""; }
    }
}
