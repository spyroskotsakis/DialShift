using Avalonia;
using DialShift.App.Platform.MacOS;
using DialShift.App.Services;
using DialShift.App.SingleInstance;
using Microsoft.Extensions.DependencyInjection;

namespace DialShift.App;

/// <summary>
/// Process entry and the pre-UI half of the composition root (brief 1 §4.2, §6): resolves the data directory once, builds
/// the services, and settles single instance before Avalonia starts, so a second launch activates the running copy and
/// exits without ever creating a window (BHV-07, BHV-08).
/// </summary>
internal static class Program
{
    /// <summary>A second launch's longest wait for its services to dispose before it exits.</summary>
    private static readonly TimeSpan SecondInstanceDisposeTimeout = TimeSpan.FromSeconds(2);

    [STAThread]
    public static int Main(string[] args)
    {
        var launch = LaunchOptions.Parse(args);
        AppPaths paths;
        try
        {
            paths = AppPaths.Resolve(Environment.GetEnvironmentVariable, launch.SmokeTest);
        }
        catch (InvalidOperationException ex)
        {
            // Without a data directory there is no log file either; standard error is the only channel.
            Console.Error.WriteLine("DialShift can't start: " + ex.Message);
            return ExitCodes.StartupFailed;
        }

        var services = AppComposition.BuildServiceProvider(paths);
        var singleInstance = services.GetRequiredService<ISingleInstanceService>();
        var start = singleInstance.TryStartPrimary();
        if (start == SingleInstanceStartResult.AlreadyRunning)
        {
            // The service logs single_instance.activate_sent or single_instance.activate_failed.
            var result = singleInstance.ActivateExistingAsync().GetAwaiter().GetResult();
            services.DisposeAsync().AsTask().Wait(SecondInstanceDisposeTimeout);
            return result == SingleInstanceActivationResult.Activated ? ExitCodes.Success : ExitCodes.ActivationFailed;
        }
        var failure = StartupFailureFor(start, paths);

        var log = services.GetRequiredService<FileAppLog>();
        log.LogStartup(services.GetRequiredService<PlaybackEngineFactory>().EngineName, paths.Source);
        if (paths.Source == DataDirectorySource.EnvironmentOverride)
            log.Info("app.data_dir_override", $"Using the data directory from {AppPaths.DataDirectoryOverrideVariable}: {paths.DataDirectory}");

        var builder = BuildAvaloniaApp();
        // A login, restart or launch while every display sleeps must not stop the tray and the schedule from starting.
        if (OperatingSystem.IsMacOS()) builder = builder.UseRenderTimerFallback(log);
        try
        {
            return builder.StartWithClassicDesktopLifetime(args, lifetime =>
                lifetime.Startup += (_, _) => ((App)Application.Current!).Run(lifetime, services, launch, failure));
        }
        catch (Exception ex)
        {
            // The UI toolkit failed to start, or an exception escaped the UI thread: no dialog is possible any more.
            log.Error("app.crashed", "DialShift stopped unexpectedly.", ex);
            return ExitCodes.StartupFailed;
        }
    }

    /// <summary>
    /// The startup failure a primary-side <see cref="SingleInstanceStartResult"/> stands for (D29), or null for
    /// <see cref="SingleInstanceStartResult.Primary"/>. The service has already logged the cause and released the lock.
    /// </summary>
    internal static StartupFailure? StartupFailureFor(SingleInstanceStartResult start, AppPaths paths) => start switch
    {
        SingleInstanceStartResult.Primary => null,
        // single_instance.server_failed: the lock was acquired, the activation pipe wasn't (exit 3).
        SingleInstanceStartResult.Failed => new StartupFailure(
            "Its activation channel couldn't be opened. Another copy of DialShift may still be closing; try again in a moment.",
            ExitCodes.SingleInstanceFailed),
        // single_instance.lock_failed: the lock file itself couldn't be created or opened (exit 1).
        SingleInstanceStartResult.LockFailed => new StartupFailure(
            $"It couldn't create its lock file in {paths.DataDirectory}. Check that this folder can be written to and that the disk isn't full.",
            ExitCodes.StartupFailed),
        _ => throw new ArgumentOutOfRangeException(nameof(start), start, "A second launch is not a startup failure."),
    };

    /// <summary>The Avalonia builder (also the entry point the XAML previewer looks for).</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// <summary>A failure detected before Avalonia started, reported through the startup-failure dialog once it has.</summary>
internal sealed record StartupFailure(string Reason, int ExitCode);
