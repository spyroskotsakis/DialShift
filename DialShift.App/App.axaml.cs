using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DialShift.App.Platform;
using DialShift.App.Services;
using DialShift.App.SingleInstance;
using DialShift.App.Smoke;
using DialShift.App.Tray;
using DialShift.App.ViewModels;
using DialShift.App.Views;
using DialShift.Core;
using DialShift.Core.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace DialShift.App;

/// <summary>
/// The Avalonia application and the runtime half of the composition root (brief 1 §4.2, §6). <see cref="Program.Main"/>
/// has already built the services and become the primary instance, then hands them over through <see cref="Run"/>. From
/// there this class runs the startup sequence, owns the main window and the tray, and implements the one quit path (BHV-11).
/// </summary>
/// <remarks>
/// Startup failures of any kind log <c>app.startup_failed</c>, show the startup-failure dialog, tear down (releasing the
/// single-instance lock) and exit non-zero (BHV-04, HS-08). Quitting never hangs: every teardown step that can block is
/// bounded (CR-02).
/// </remarks>
public partial class App : Application, IAppShell
{
    /// <summary>The longest quit waits for the single-instance service, and separately for the provider, to dispose.</summary>
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly List<PosixSignalRegistration> signals = [];
    private ServiceProvider services = null!;
    private LaunchOptions launch = null!;
    private IAppLog log = null!;
    private ISingleInstanceService singleInstance = null!;
    private IClassicDesktopStyleApplicationLifetime? lifetime;
    private IActivatableLifetime? activatable;
    private Settings? settings;
    private IPlaybackCoordinator? coordinator;
    private MainWindow? mainWindow;
    private TrayMenuController? tray;
    private PlaybackHost? playbackHost;
    private ISystemPowerEvents? powerEvents;
    private bool showWhenReady;
    private int? startupFailureExitCode;
    private Task? teardown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Takes over the app from <see cref="Program.Main"/>. Called once, on the UI thread, from the desktop lifetime's
    /// <c>Startup</c> event, which fires before its message loop starts. Hosts that never call it (the XAML previewer,
    /// headless tests) get the theme and styles only.
    /// </summary>
    internal void Run(IClassicDesktopStyleApplicationLifetime desktop, ServiceProvider services, LaunchOptions launch, StartupFailure? pendingFailure)
    {
        this.services = services;
        this.launch = launch;
        log = services.GetRequiredService<IAppLog>();
        singleInstance = services.GetRequiredService<ISingleInstanceService>();
        lifetime = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += OnShutdownRequested;
        singleInstance.ActivationRequested += OnActivationRequested;
        RegisterQuitSignals();
        // Startup continues inside the message loop, so PlaybackHost captures the UI synchronization context (D18), power
        // events start with a live loop, and dialogs can show.
        Dispatcher.UIThread.Post(() => _ = pendingFailure == null
            ? StartAsync()
            : FailStartupAsync(pendingFailure.Reason, null, pendingFailure.ExitCode));
    }

    public void ShowMainWindow()
    {
        if (teardown != null) return;
        if (mainWindow == null)
        {
            // Activation arrived before the window exists: show it as soon as startup creates it.
            showWhenReady = true;
            return;
        }
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    public void HideMainWindow() => mainWindow?.Hide();

    public void Quit() => Quit(ExitCodes.Success);

    /// <summary>Quitting after a startup failure keeps its non-zero exit code and does not save (HS-08).</summary>
    private void Quit(int exitCode) => teardown ??= TeardownAsync(startupFailureExitCode ?? exitCode, saveSettings: startupFailureExitCode == null);

    private async Task StartAsync()
    {
        try
        {
            var store = services.GetRequiredService<SettingsStore>();
            settings = services.GetRequiredService<Settings>();
            if (store.Warning != null) log.Warn("settings.recovered", store.Warning);

            coordinator = services.GetRequiredService<IPlaybackCoordinator>();
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            mainWindow = new MainWindow(viewModel);
            lifetime!.MainWindow = mainWindow;
            tray = new TrayMenuController(this, TrayIconOptions.ForCurrentPlatform(),
                services.GetRequiredService<ViewModelServices>(), this, services.GetRequiredService<IUiDispatcher>());

            // macOS: with LSUIElement, reopening the running app from Finder or `open` arrives as Reopen (BHV-18).
            activatable = this.TryGetFeature<IActivatableLifetime>();
            if (activatable != null) activatable.Activated += OnActivated;

            if (showWhenReady || !(launch.StartInTray || settings.StartInTray)) ShowMainWindow();

            playbackHost = services.GetRequiredService<PlaybackHost>();
            playbackHost.Start();
            await coordinator.StartScheduleAsync();
            if (teardown != null) return;

            // A smoke run has nobody to dismiss the notice; settings.recovered is already logged above.
            if (!launch.SmokeTest) ObserveFailure(viewModel.ShowSettingsRecoveredAsync(store.Warning), "ui.dialog_failed");

            powerEvents = services.GetRequiredService<ISystemPowerEvents>();
            powerEvents.Resumed += OnResumed;
            powerEvents.Start();

            // Reads the verified launch-at-login state and logs startup_registration.result.
            await viewModel.InitializeAsync();

            if (launch.SmokeTest && teardown == null)
                Quit(await SmokeRunner.RunAsync(services, mainWindow, tray, launch));
        }
        catch (Exception ex)
        {
            await FailStartupAsync(ex.Message, ex, ExitCodes.StartupFailed);
        }
    }

    private async Task FailStartupAsync(string reason, Exception? exception, int exitCode)
    {
        if (teardown != null) return;
        startupFailureExitCode = exitCode;
        log.Error("app.startup_failed", reason, exception);
        // A smoke run records the failure in results.json instead: nobody would dismiss the dialog.
        if (launch.SmokeTest) SmokeRunner.RecordStartupFailure(services, launch, reason);
        else
        {
            try
            {
                await services.GetRequiredService<AvaloniaDialogService>().ShowStartupFailureAsync(reason, services.GetRequiredService<AppPaths>().LogFile);
            }
            catch (Exception ex)
            {
                log.Error("ui.dialog_failed", "Couldn't show the startup-failure dialog.", ex);
            }
        }
        // Exits with the failure code and does not save: the settings may be defaults standing in for a file that failed to load.
        Quit();
    }

    /// <summary>
    /// The single quit path, in the order of BHV-11: save settings, stop playback (bounded coordinator dispose, CR-02),
    /// dispose power events and the single-instance service (releases the lock), dispose the tray, dispose the provider
    /// (bounded), shut down.
    /// </summary>
    private async Task TeardownAsync(int exitCode, bool saveSettings)
    {
        var clean = true;
        if (saveSettings) SaveSettingsOnExit();
        if (playbackHost != null) clean &= await playbackHost.StopAsync();

        if (powerEvents != null)
        {
            powerEvents.Resumed -= OnResumed;
            powerEvents.Dispose();
        }
        singleInstance.ActivationRequested -= OnActivationRequested;
        clean &= await CompletesWithinAsync(singleInstance.DisposeAsync().AsTask(), DisposeTimeout, "single_instance");
        foreach (var signal in signals) signal.Dispose();
        if (activatable != null) activatable.Activated -= OnActivated;

        tray?.Dispose();
        clean &= await CompletesWithinAsync(services.DisposeAsync().AsTask(), DisposeTimeout, "services");

        log.Info("app.exit", $"code={exitCode} clean={clean}");
        if (lifetime != null) lifetime.ShutdownRequested -= OnShutdownRequested;
        lifetime?.Shutdown(exitCode);
    }

    /// <summary>Saves directly, without the "Save failed" dialog: nothing may hold up quitting. A failure is logged.</summary>
    private void SaveSettingsOnExit()
    {
        if (settings == null) return;
        try { services.GetRequiredService<SettingsStore>().Save(settings); }
        catch (Exception ex) { log.Error("settings.save_failed", "Couldn't save settings while quitting.", ex); }
    }

    private async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            log.Warn("app.quit.dispose_timeout", $"Disposing {what} took longer than {timeout.TotalSeconds:0.#} s; quitting anyway.");
            ObserveFailure(task, "app.quit.dispose_failed");
            return false;
        }
        try
        {
            await task;
            return true;
        }
        catch (Exception ex)
        {
            log.Error("app.quit.dispose_failed", $"Disposing {what} failed.", ex);
            return false;
        }
    }

    /// <summary>Logs a fire-and-forget task's failure instead of leaving it unobserved.</summary>
    private void ObserveFailure(Task task, string eventName) =>
        _ = task.ContinueWith(t => log.Error(eventName, "A background task failed.", t.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>
    /// A shutdown this app did not start: the app menu's Quit, or the OS ending the session. Avalonia 12 does not say which
    /// (<c>IsOSShutdown</c> is internal), and cancelling would veto a logout on macOS, so it is never cancelled. Settings are
    /// saved synchronously; the process then exits without the asynchronous playback teardown.
    /// </summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        SaveSettingsOnExit();
        log.Info("app.exit", "code=0 clean=false reason=shutdown_requested");
    }

    // Raised on a thread-pool thread by the single-instance listener (BHV-08).
    private void OnActivationRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ShowMainWindow);

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Reopen) ShowMainWindow();
    }

    // Raised on an arbitrary thread; NotifyWakeAsync is safe to call from any thread (D18).
    private void OnResumed(object? sender, EventArgs e)
    {
        if (coordinator != null) ObserveFailure(coordinator.NotifyWakeAsync(), "wake.notify_failed");
    }

    /// <summary>
    /// SIGTERM (<c>kill</c>, <c>launchctl bootout</c> of the launch-at-login agent) and SIGINT (Ctrl+C in a terminal) quit
    /// through the same clean path. Where the OS can't deliver them to this process, that is logged and startup goes on.
    /// </summary>
    private void RegisterQuitSignals()
    {
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
        {
            try { signals.Add(PosixSignalRegistration.Create(signal, OnQuitSignal)); }
            catch (Exception ex) when (ex is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                log.Warn("app.signal_unavailable", $"Can't handle {signal}; it will end the process without the quit path.", ex);
            }
        }
    }

    private void OnQuitSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Dispatcher.UIThread.Post(Quit);
    }
}
