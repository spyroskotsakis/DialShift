using Avalonia;
using DialShift.App;
using DialShift.App.Platform;
using DialShift.App.Services;
using DialShift.App.SingleInstance;
using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DialShift.Tests.Ui;

/// <summary>
/// Drives the real <see cref="App"/> startup and quit sequence headlessly (HS-05, HS-06, HS-08, BHV-11): it builds the real
/// service graph with <see cref="AppComposition.BuildServiceProvider"/>, overriding only the OS edges — single instance,
/// power events, startup registration, reveal, log, clocks, playback coordinator — and hands it to <c>App.Run</c> exactly as
/// <c>Program.Main</c> does, with a recording desktop lifetime.
/// </summary>
/// <remarks>
/// <para><b>Seams.</b> <c>App.Run</c>, <c>StartupFailure</c> and <c>App.Tray</c> are internal; DialShift.App grants
/// <c>InternalsVisibleTo("DialShift.Tests")</c>, so they are called directly.</para>
/// <para><b>Overrides</b> (registered after the app's own through the <c>configure</c> seam, so they win): the doubles above;
/// the dialog service, whose production owner lookup reads <c>Application.ApplicationLifetime</c>, which the headless platform
/// does not set; the production <see cref="SettingsService"/> wrapped in a <see cref="JournalingSettingsService"/>; the
/// settings when a scenario seeds them; and the main window view model, only to inject UTC as the computer's zone (production
/// passes <see cref="TimeZoneInfo.Local"/>; QA-B2) or a failing factory (HS-08). Everything else (the settings store,
/// <see cref="PlaybackHost"/>, the UI dispatcher, the shell, <see cref="ViewModelServices"/>) is the production registration,
/// and the whole graph is validated on build.</para>
/// </remarks>
public sealed class AppHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private AppHarness(string? settingsJson, Action<Settings>? seed, bool realCoordinator, Func<MainWindowViewModel>? viewModel,
        Action<FakeCoordinator>? configureFake)
    {
        Directory = new TempDirectory("app");
        Paths = AppPaths.Resolve(name => name == AppPaths.DataDirectoryOverrideVariable ? Directory.Path : null, smokeTest: false);
        if (settingsJson != null) File.WriteAllText(Paths.SettingsFile, settingsJson);
        SingleInstance = new FakeSingleInstance(Journal);
        Power = new FakePowerEvents(Journal);
        Lifetime = FakeDesktopLifetime.Create(Journal);

        provider = AppComposition.BuildServiceProvider(Paths, services =>
        {
            services.AddSingleton<IAppLog>(Log);
            services.AddSingleton<ISingleInstanceService>(SingleInstance);
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IMonotonicClock>(Mono);
            services.AddSingleton<IStartupRegistration>(Startup);
            services.AddSingleton<ISystemPowerEvents>(Power);
            services.AddSingleton<IFileRevealService>(Reveal);
            if (seed != null)
                services.AddSingleton(sp =>
                {
                    var settings = sp.GetRequiredService<SettingsStore>().Load();
                    seed(settings);
                    return settings;
                });
            services.AddSingleton<IPlaybackCoordinator>(sp =>
            {
                var settings = sp.GetRequiredService<Settings>();
                if (!realCoordinator)
                {
                    Fake = new FakeCoordinator(Journal, PlaybackSnapshot.Initial(settings.Volume));
                    configureFake?.Invoke(Fake);
                    return Fake;
                }
                Engine = new FakePlaybackEngine();
                return Real = new PlaybackCoordinator(settings, Engine, Clock, Mono, Log, TimeZoneInfo.Utc);
            });
            services.AddSingleton(_ => new AvaloniaDialogService(() => Lifetime.MainWindow));
            services.AddSingleton<ISettingsService>(sp => new JournalingSettingsService(new SettingsService(sp.GetRequiredService<Settings>(),
                sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<IPlaybackCoordinator>(), sp.GetRequiredService<IDialogService>(), Log), Journal));
            services.AddSingleton(sp => viewModel?.Invoke() ?? new MainWindowViewModel(
                sp.GetRequiredService<IPlaybackCoordinator>(), sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IEditorDialogService>(), sp.GetRequiredService<IStartupRegistration>(), sp.GetRequiredService<IFileRevealService>(),
                sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<IAppShell>(), sp.GetRequiredService<IAppLog>(), sp.GetRequiredService<IClock>(),
                TimeZoneInfo.Utc, new AppInfo(UiRig.Version, Paths.DataDirectory)));
        });
        App = (DialShift.App.App)Application.Current!;
    }

    public TempDirectory Directory { get; }
    public AppPaths Paths { get; }
    public Journal Journal { get; } = new();
    public RecordingAppLog Log { get; } = new();
    public FakeClock Clock { get; } = new(UiRig.DefaultNow);
    public FakeMonotonicClock Mono { get; } = new();
    public FakeStartupRegistration Startup { get; } = new();
    public FakeFileReveal Reveal { get; } = new();
    public FakeSingleInstance SingleInstance { get; }
    public FakePowerEvents Power { get; }
    public FakeDesktopLifetime Lifetime { get; }
    public DialShift.App.App App { get; }
    public FakeCoordinator? Fake { get; private set; }
    public PlaybackCoordinator? Real { get; private set; }
    public FakePlaybackEngine? Engine { get; private set; }

    public DialShift.App.Views.MainWindow? MainWindow => Lifetime.MainWindow as DialShift.App.Views.MainWindow;

    /// <summary>True once startup has finished (the last startup step reads the launch-at-login status) or failed.</summary>
    public bool Started => Startup.GetCalls > 0 || Log.HasEvent("app.startup_failed");

    /// <summary>
    /// Builds the services and calls <c>App.Run</c> on the headless UI thread (inside <see cref="Headless.RunAsync"/>), then
    /// waits for the posted startup to finish or fail. <paramref name="pendingFailure"/> is a failure detected before
    /// Avalonia (reason, exit code), as <c>Program.Main</c> passes for a failed single-instance server.
    /// <paramref name="configureFake"/> adjusts the recording coordinator when the provider creates it.
    /// </summary>
    public static async Task<AppHarness> StartAsync(bool startInTray = false, string? settingsJson = null, Action<Settings>? seed = null,
        bool realCoordinator = false, Func<MainWindowViewModel>? viewModel = null, (string Reason, int ExitCode)? pendingFailure = null,
        Action<FakeCoordinator>? configureFake = null)
    {
        var harness = new AppHarness(settingsJson, seed, realCoordinator, viewModel, configureFake);
        var failure = pendingFailure is { } f ? new StartupFailure(f.Reason, f.ExitCode) : null;
        harness.App.Run(harness.Lifetime.Lifetime, harness.provider, new LaunchOptions(startInTray, SmokeTest: false), failure);
        if (!await Headless.WaitAsync(() => harness.Started || Headless.OpenedWindows.OfType<DialShift.App.Views.Dialogs.MessageDialog>().Any()))
            throw new TimeoutException("App startup did not finish.");
        return harness;
    }

    /// <summary>Waits until the app has shut the lifetime down; returns the exit code (null on timeout).</summary>
    public async Task<int?> WaitForExitAsync()
    {
        await Headless.WaitAsync(() => Lifetime.ExitCode != null);
        return Lifetime.ExitCode;
    }

    /// <summary>Quits through the app's one quit path if the scenario did not, so signal registrations and services are released.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Lifetime.ExitCode == null)
        {
            App.Quit();
            await WaitForExitAsync();
        }
        Directory.Dispose();
    }
}
