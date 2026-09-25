using Avalonia;
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
        StartupFailure? failure = null;
        switch (singleInstance.TryStartPrimary())
        {
            case SingleInstanceStartResult.AlreadyRunning:
                // The service logs single_instance.activate_sent or single_instance.activate_failed.
                var result = singleInstance.ActivateExistingAsync().GetAwaiter().GetResult();
                services.DisposeAsync().AsTask().Wait(SecondInstanceDisposeTimeout);
                return result == SingleInstanceActivationResult.Activated ? ExitCodes.Success : ExitCodes.ActivationFailed;
            case SingleInstanceStartResult.Failed:
                // The service already released the lock and logged single_instance.server_failed.
                failure = new StartupFailure(
                    "Its activation channel couldn't be opened. Another copy of DialShift may still be closing; try again in a moment.",
                    ExitCodes.SingleInstanceFailed);
                break;
        }

        var log = services.GetRequiredService<FileAppLog>();
        log.LogStartup(services.GetRequiredService<PlaybackEngineFactory>().EngineName, paths.Source);
        if (paths.Source == DataDirectorySource.EnvironmentOverride)
            log.Info("app.data_dir_override", $"Using the data directory from {AppPaths.DataDirectoryOverrideVariable}: {paths.DataDirectory}");

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, lifetime =>
                lifetime.Startup += (_, _) => ((App)Application.Current!).Run(lifetime, services, launch, failure));
        }
        catch (Exception ex)
        {
            // The UI toolkit failed to start, or an exception escaped the UI thread: no dialog is possible any more.
            log.Error("app.crashed", "DialShift stopped unexpectedly.", ex);
            return ExitCodes.StartupFailed;
        }
    }

    /// <summary>The Avalonia builder (also the entry point the XAML previewer looks for).</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

/// <summary>A failure detected before Avalonia started, reported through the startup-failure dialog once it has.</summary>
internal sealed record StartupFailure(string Reason, int ExitCode);
