using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DialShift.App.Platform;
using DialShift.App.Services;
using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace DialShift.App;

/// <summary>
/// The service graph of the composition root (brief 1 §4.2, §6). <see cref="Program.Main"/> builds it once, before
/// Avalonia starts, so the single-instance check can run first; <see cref="App"/> resolves from it during startup.
/// </summary>
/// <remarks>
/// <para>Every service is a singleton. Several are only <see cref="IAsyncDisposable"/> (the coordinator, the
/// single-instance service), so the provider must be disposed with <c>DisposeAsync</c>; <see cref="App"/> does that,
/// bounded, as the last step of its quit path.</para>
/// <para><b>The engine (D17).</b> There is no <see cref="IPlaybackEngine"/> registration. The coordinator registration
/// below is the only caller of <see cref="PlaybackEngineFactory.Create"/>, so the coordinator gets a fresh, never-started
/// engine, owns it, and is the only thing that disposes it. Do not register or resolve an engine anywhere else.</para>
/// <para><b>The running app.</b> <see cref="IAppShell"/> and the main window come from the running Avalonia
/// <see cref="App"/>, so the services that use them may be resolved only after Avalonia has created it.</para>
/// </remarks>
public static class AppComposition
{
    /// <param name="paths">The resolved data, log and settings locations.</param>
    /// <param name="configure">
    /// Test seam: runs after the app's own registrations and before the provider is built and validated, so a test can replace
    /// services (a later registration wins for single resolution) while keeping the real graph. <see cref="Program.Main"/>
    /// passes nothing.
    /// </param>
    public static ServiceProvider BuildServiceProvider(AppPaths paths, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddDialShiftPlatform();
        services.AddDialShiftPlayback();

        services.AddSingleton(_ => new SettingsStore(paths.DataDirectory));
        // Loading runs on first resolution, inside App startup: if the corrupt-file backup copy throws, the
        // startup-failure path reports it (BHV-03, BHV-04).
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());

        services.AddSingleton<IPlaybackCoordinator>(sp =>
        {
            var log = sp.GetRequiredService<IAppLog>();
            // Core's static scheduler has no logger of its own: this routes schedule.zone_unknown to dialshift.log. The
            // coordinator is resolved first at startup (the main window view model depends on it), before any zone lookup.
            Scheduler.Log = log;
            return new PlaybackCoordinator(
                sp.GetRequiredService<Settings>(),
                sp.GetRequiredService<PlaybackEngineFactory>().Create(),
                sp.GetRequiredService<IClock>(),
                // The per-OS sleep-inclusive clock from the platform lane (D14), never StopwatchMonotonicClock.
                sp.GetRequiredService<IMonotonicClock>(),
                log,
                TimeZoneInfo.Local);
        });
        services.AddSingleton<PlaybackHost>();

        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton(_ => new AvaloniaDialogService(RunningMainWindow));
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<IEditorDialogService>(sp => sp.GetRequiredService<AvaloniaDialogService>());
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAppShell>(_ => Application.Current as IAppShell
            ?? throw new InvalidOperationException("The DialShift Avalonia application is not running."));

        // Brief 3 station catalog (docs/catalog-contracts.md §4.4). The load is lazy: the first Add dialog starts it on the
        // thread pool, and startup never waits for it. The file sits next to the apphost, never in the current directory (D60).
        services.AddSingleton<ICatalogProvider>(sp => new CatalogProvider(
            CatalogProvider.ResolveLocation(Environment.GetEnvironmentVariable, AppContext.BaseDirectory),
            sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<ICatalogLogoLoader>(_ => new CatalogLogoLoader(new SocketsHttpHandler { ConnectTimeout = CatalogLogoLoader.Timeout }));

        services.AddSingleton<ViewModelServices>();
        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<IPlaybackCoordinator>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IEditorDialogService>(),
            sp.GetRequiredService<IStartupRegistration>(),
            sp.GetRequiredService<IFileRevealService>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<IAppShell>(),
            sp.GetRequiredService<IAppLog>(),
            sp.GetRequiredService<IClock>(),
            TimeZoneInfo.Local,
            new AppInfo(AppInfo.DisplayVersion(typeof(AppComposition).Assembly), paths.DataDirectory)));

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    /// <summary>The main window of the running app, which <see cref="App"/> assigns to the desktop lifetime at startup.</summary>
    private static Window? RunningMainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
