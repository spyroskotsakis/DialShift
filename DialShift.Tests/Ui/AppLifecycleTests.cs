using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using DialShift.App;
using DialShift.App.ViewModels;
using DialShift.App.Views;
using DialShift.App.Views.Dialogs;
using DialShift.Core;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.Headless;

namespace DialShift.Tests.Ui;

/// <summary>
/// The real <c>App</c> startup and quit sequence, headless, through <see cref="AppHarness"/>: window show/hide/activation
/// (HS-05), start in tray (HS-06), the recovered-settings notice (HS-07), the startup-failure path (HS-08) and the one quit
/// path with its order and bounds (BHV-11, CR-02). The process exit code itself is <c>Program.Main</c>'s return value; here
/// it is the code the App hands to the desktop lifetime, which <c>StartWithClassicDesktopLifetime</c> returns.
/// </summary>
public static class AppLifecycleTests
{
    public static async Task RunAsync()
    {
        await Headless.RunAsync(StartShowHideActivateQuit);
        await Headless.RunAsync(QuitOrder);
        await Headless.RunAsync(StartInTrayFlag);
        await Headless.RunAsync(StartInTraySetting);
        await Headless.RunAsync(RecoveredNotice);
        await Headless.RunAsync(RecoveredNoticeInTray);
        await Headless.RunAsync(FailureBeforeWindow);
        await Headless.RunAsync(FailureAfterWindow);
        await Headless.RunAsync(FailureBeforeAvalonia);
        await Headless.RunAsync(BoundedQuit);
        await Headless.RunAsync(OsShutdownRequested);
    }

    private static string MessageText(MessageDialog dialog) =>
        string.Join("\n", Find<SelectableTextBlock>(dialog).Select(t => t.Text));

    private static int CountOf(Journal journal, string entry) => journal.Entries.Count(e => e == entry);

    private static int IndexOf(Journal journal, string entry) => journal.Entries.ToList().IndexOf(entry);

    private static async Task StartShowHideActivateQuit()
    {
        Settings? live = null;
        await using var app = await AppHarness.StartAsync(realCoordinator: true, seed: s =>
        {
            live = s;
            s.ScheduleEnabled = true;
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[1].Id, Time = "09:00", Days = [DayOfWeek.Monday] });
        });
        var window = app.MainWindow!;
        Check("HS-05 BHV-11 startup: explicit shutdown mode (only Quit exits), the main window is the lifetime's", app.Lifetime.ShutdownMode == ShutdownMode.OnExplicitShutdown && window != null);
        Check("HS-05 BHV-09 a normal launch shows the main window", OpenedWindows.OfType<MainWindow>().Count() == 1 && window!.IsVisible);
        Check("HS-14 BHV-10 startup catch-up plays the current slot's station (Mon 09:00 → Drone Zone at 10:00)",
            app.Engine!.Starts.Count == 1 && app.Engine.Starts[0].Source.Url.ToString() == live!.Stations[1].Url);
        Check("HS-05 power events started once and observed; activation and shutdown requests observed",
            app.Power.StartCount == 1 && app.Power.HasSubscribers && app.SingleInstance.HasSubscribers && app.Lifetime.HasShutdownRequestedSubscribers);
        Check("HS-13 DOD-09 the App's startup reads the verified launch-at-login status and logs startup_registration.result",
            app.Startup.GetCalls == 1 && app.Log.HasEvent("startup_registration.result"));
        Check("HS-07 BHV-03 a readable settings file shows no recovery notice", !app.Log.HasEvent("settings.recovered") && !OpenedWindows.OfType<MessageDialog>().Any());

        window!.Close();
        await PumpAsync();
        Check("HS-05 BHV-12 closing hides; the app keeps running and playing", !window.IsVisible && app.Lifetime.ExitCode == null && app.Engine.ActiveSessionId != null);
        app.App.ShowMainWindow();
        await PumpAsync();
        Check("HS-05 BHV-15 ShowMainWindow brings it back, Normal state", window.IsVisible && window.WindowState == WindowState.Normal);
        window.WindowState = WindowState.Minimized;
        await PumpAsync();
        Check("HS-05 BHV-13 minimizing hides it", !window.IsVisible);
        app.App.ShowMainWindow();
        await PumpAsync();
        Check("HS-05 BHV-15 ShowMainWindow restores a minimized window to Normal", window.IsVisible && window.WindowState == WindowState.Normal);
        app.App.HideMainWindow();
        await PumpAsync();
        Check("HS-05 BHV-14 HideMainWindow (the header button's shell call) hides it; audio continues", !window.IsVisible && app.Engine.ActiveSessionId != null);
        await app.SingleInstance.RaiseActivationAsync();
        Check("HS-05 BHV-08 BHV-15 a second launch's activation (raised off the UI thread) shows the window", await WaitAsync(() => window.IsVisible));

        app.Power.RaiseResumed();
        Check("HS-05 BHV-42 an OS resume reaches the coordinator's wake recovery", await WaitAsync(() => app.Log.HasEvent("wake.detected")));

        live!.Volume = 17;
        app.App.Quit();
        Check("HS-04 BHV-11 Quit shuts down with exit code 0", await app.WaitForExitAsync() == 0);
        Check("HS-04 BHV-11 Quit saved the settings first", new SettingsStore(app.Paths.DataDirectory).Load().Volume == 17);
        Check("HS-04 BHV-11 Quit stopped and disposed the engine, the power events and the single-instance lock",
            app.Engine.IsDisposed && app.Engine.ActiveSessionId == null && app.Power.DisposeCount == 1 && app.SingleInstance.DisposeCount == 1 && !app.Power.HasSubscribers);
        Check("HS-04 BHV-11 app.exit is logged, clean", app.Log.Entries.Any(e => e.EventName == "app.exit" && e.Message == "code=0 clean=True"));
        app.App.Quit();
        await PumpAsync();
        Check("HS-04 BHV-11 quitting twice shuts down once, and the shutdown-request handler is gone",
            CountOf(app.Journal, "lifetime.Shutdown:0") == 1 && !app.Lifetime.HasShutdownRequestedSubscribers);
        // The real lifetime closes the windows at shutdown; the recording one does not, so hide it to observe ShowMainWindow.
        window.Hide();
        app.App.ShowMainWindow();
        await PumpAsync();
        Check("HS-04 BHV-11 after quit nothing reopens the window", !window.IsVisible);
    }

    private static async Task QuitOrder()
    {
        await using var app = await AppHarness.StartAsync();
        app.App.Quit();
        await app.WaitForExitAsync();
        var order = new[] { "coordinator.DisposeAsync", "power.Dispose", "single_instance.Dispose", "lifetime.Shutdown:0" }.Select(e => IndexOf(app.Journal, e)).ToList();
        Check("HS-04 BHV-11 quit order: stop playback → power events → single-instance lock → shutdown",
            order.All(i => i >= 0) && order.SequenceEqual(order.Order()));
        Check("HS-04 BHV-11 the settings file was written by the quit save", File.Exists(app.Paths.SettingsFile));
    }

    private static async Task StartInTrayFlag()
    {
        await using var app = await AppHarness.StartAsync(startInTray: true);
        Check("HS-06 BHV-09 --tray: startup completes with the window created but never shown",
            app.MainWindow != null && !app.MainWindow.IsVisible && !OpenedWindows.OfType<MainWindow>().Any() && app.Journal.Entries.Contains("coordinator.StartScheduleAsync"));
        await app.SingleInstance.RaiseActivationAsync();
        Check("HS-06 BHV-09 an activation later shows it", await WaitAsync(() => app.MainWindow!.IsVisible) && OpenedWindows.OfType<MainWindow>().Count() == 1);
    }

    private static async Task StartInTraySetting()
    {
        await using var app = await AppHarness.StartAsync(seed: s => s.StartInTray = true);
        Check("HS-06 BHV-09 BHV-60 StartInTray=true: the window is never shown at launch",
            app.MainWindow != null && !app.MainWindow.IsVisible && !OpenedWindows.OfType<MainWindow>().Any() && app.Started);
    }

    private static async Task RecoveredNotice()
    {
        await using var app = await AppHarness.StartAsync(settingsJson: "{ \"Version\": 1, \"Stations\": [ { \"Name\": \"\" } ] }");
        var dialog = await WaitForWindowAsync<MessageDialog>(0);
        var backups = Directory.GetFiles(app.Paths.DataDirectory, "settings.json.unreadable-*");
        Check("HS-07 BHV-03 corrupt settings: backup file written and settings.recovered logged",
            backups.Length == 1 && app.Log.Entries.Any(e => e.EventName == "settings.recovered" && e.Level == AppLogLevel.Warn));
        Check("HS-07 BHV-03 BHV-64 \"DialShift · Settings recovered\" names the backup, owned by the visible main window",
            dialog.Title == UiText.SettingsRecoveredTitle && MessageText(dialog).Contains(backups[0], StringComparison.Ordinal) && dialog.Owner == app.MainWindow);
        await PressAsync(dialog, Key.Enter);
        await PumpAsync();
        Check("HS-07 BHV-03 the notice is shown once", !dialog.IsVisible && OpenedWindows.OfType<MessageDialog>().Count() == 1);
        Check("HS-07 BHV-03 the app runs on defaults (three starter stations shown)", app.MainWindow!.IsVisible && app.Lifetime.ExitCode == null);
    }

    private static async Task RecoveredNoticeInTray()
    {
        await using var app = await AppHarness.StartAsync(startInTray: true, settingsJson: "not json at all");
        var dialog = await WaitForWindowAsync<MessageDialog>(0);
        Check("HS-07 BHV-03 BHV-64 started in the tray, the recovery notice is ownerless, centered, on top — the hidden window never shows",
            dialog.Owner == null && dialog.WindowStartupLocation == WindowStartupLocation.CenterScreen && dialog.Topmost && !app.MainWindow!.IsVisible);
        await PressAsync(dialog, Key.Enter);
        Check("HS-07 BHV-03 dismissing it leaves the app running in the tray", !dialog.IsVisible && !app.MainWindow!.IsVisible && app.Lifetime.ExitCode == null);
    }

    private static async Task FailureBeforeWindow()
    {
        await using var app = await AppHarness.StartAsync(viewModel: () => throw new InvalidOperationException("Forced startup failure (test)."));
        var dialog = await WaitForWindowAsync<MessageDialog>(0);
        var failed = app.Log.Entries.SingleOrDefault(e => e.EventName == "app.startup_failed");
        Check("HS-08 BHV-04 app.startup_failed is logged as an error with the reason and the exception",
            failed is { Level: AppLogLevel.Error, Message: "Forced startup failure (test).", Exception: InvalidOperationException });
        Check("HS-08 BHV-04 the dialog says \"DialShift couldn't start. <reason>\" and points at the log file",
            dialog.Title == "DialShift" && MessageText(dialog).Contains($"DialShift couldn't start. Forced startup failure (test).\n\nDetails: {app.Paths.LogFile}", StringComparison.Ordinal));
        Check("HS-08 BHV-64 with no window yet the dialog is ownerless and on top", dialog.Owner == null && dialog.Topmost && app.MainWindow == null);
        Check("HS-08 the app waits for the user before exiting", app.Lifetime.ExitCode == null);
        await PressAsync(dialog, Key.Enter);
        Check("HS-08 BHV-04 OK exits with code 1 (StartupFailed)", await app.WaitForExitAsync() == ExitCodes.StartupFailed);
        Check("HS-08 BHV-11 the single-instance lock is released", app.SingleInstance.DisposeCount == 1);
        Check("HS-08 the failed start does not save settings over the file", !File.Exists(app.Paths.SettingsFile));
        Check("HS-08 app.exit logs code=1", app.Log.Entries.Any(e => e.EventName == "app.exit" && e.Message.StartsWith("code=1 ", StringComparison.Ordinal)));
    }

    private static async Task FailureAfterWindow()
    {
        await using var app = await AppHarness.StartAsync(configureFake: c => c.StartScheduleException = new IOException("Schedule store unavailable (test)."));
        var dialog = await WaitForWindowAsync<MessageDialog>(0);
        Check("HS-08 BHV-04 a failure after the window exists: logged, dialog owned by the main window",
            app.Log.Entries.Any(e => e.EventName == "app.startup_failed" && e.Message == "Schedule store unavailable (test).") && dialog.Owner == app.MainWindow);
        await ClickAsync(ButtonWithText(dialog, "OK"));
        Check("HS-08 BHV-04 exit code 1 after OK", await app.WaitForExitAsync() == ExitCodes.StartupFailed);
        Check("HS-08 BHV-11 teardown still ran: playback disposed, power events never started, lock released",
            app.Fake!.DisposeCount >= 1 && app.Power.StartCount == 0 && app.SingleInstance.DisposeCount == 1);
    }

    private static async Task FailureBeforeAvalonia()
    {
        const string reason = "Its activation channel couldn't be opened. Another copy of DialShift may still be closing; try again in a moment.";
        await using var app = await AppHarness.StartAsync(pendingFailure: (reason, ExitCodes.SingleInstanceFailed));
        var dialog = await WaitForWindowAsync<MessageDialog>(0);
        Check("HS-08 BHV-04 a failure found before Avalonia (single-instance server) is reported the same way",
            app.Log.HasEvent("app.startup_failed") && MessageText(dialog).Contains("DialShift couldn't start. " + reason, StringComparison.Ordinal) && app.MainWindow == null);
        await PressAsync(dialog, Key.Enter);
        Check("HS-08 BHV-04 it exits with its own code (3, SingleInstanceFailed) and releases the lock",
            await app.WaitForExitAsync() == ExitCodes.SingleInstanceFailed && app.SingleInstance.DisposeCount == 1 && !File.Exists(app.Paths.SettingsFile));
    }

    private static async Task BoundedQuit()
    {
        await using var app = await AppHarness.StartAsync(configureFake: c => c.HoldDispose = true);
        var watch = Stopwatch.StartNew();
        app.App.Quit();
        var code = await app.WaitForExitAsync();
        watch.Stop();
        Console.WriteLine($"  quit with a hanging coordinator took {watch.Elapsed.TotalSeconds:F1} s");
        Check("HS-14 CR-02 HZ-02 a coordinator whose dispose hangs cannot block quit: exit 0 within the documented bound (< 8 s)",
            code == 0 && watch.Elapsed < TimeSpan.FromSeconds(8));
        Check("HS-14 CR-02 the timeout is logged and app.exit reports clean=False",
            app.Log.HasEvent("app.quit.dispose_timeout") && app.Log.Entries.Any(e => e.EventName == "app.exit" && e.Message == "code=0 clean=False"));
        Check("HS-14 CR-02 the rest of the teardown still ran (lock released)", app.SingleInstance.DisposeCount == 1 && app.Power.DisposeCount == 1);
    }

    private static async Task OsShutdownRequested()
    {
        Settings? live = null;
        await using var app = await AppHarness.StartAsync(seed: s => live = s);
        live!.Volume = 23;
        var cancelled = app.Lifetime.RaiseShutdownRequested();
        Check("HS-04 BHV-11 an OS/app-menu shutdown is never vetoed", !cancelled);
        Check("HS-04 BHV-11 it saves the settings synchronously and logs app.exit (reason=shutdown_requested)",
            new SettingsStore(app.Paths.DataDirectory).Load().Volume == 23
            && app.Log.Entries.Any(e => e.EventName == "app.exit" && e.Message == "code=0 clean=false reason=shutdown_requested"));
    }
}
