using Avalonia;
using DialShift.App;
using DialShift.App.Services;
using DialShift.App.Tray;
using DialShift.App.ViewModels;
using DialShift.App.Views;
using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;

namespace DialShift.Tests.Ui;

/// <summary>
/// The UI graph of one scenario, wired like <c>AppComposition</c> but with doubles at the edges: a temp data directory with
/// the real <see cref="SettingsStore"/> and <see cref="SettingsService"/> (journaled), the real
/// <see cref="MainWindowViewModel"/>, and either a recording <see cref="FakeCoordinator"/> (routing checks) or the real
/// <see cref="PlaybackCoordinator"/> over a <see cref="FakePlaybackEngine"/> (end-to-end checks). Wall clock and zone are
/// injected: Monday 2026-09-14 10:00 UTC unless stated, so no check depends on the host (QA-B2).
/// </summary>
/// <remarks>
/// Two modes. <see cref="CreateViewModels"/>: no Avalonia at all; dialogs are a <see cref="RecordingDialogService"/> and the
/// dispatcher runs posts inline. <see cref="CreateHeadlessAsync"/>: on the headless UI thread, with the real
/// <see cref="MainWindow"/>, the real <see cref="AvaloniaDialogService"/> and dialogs, the real
/// <see cref="AvaloniaUiDispatcher"/>, and optionally the real <see cref="TrayMenuController"/>.
/// </remarks>
public sealed class UiRig : IAsyncDisposable
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero); // a Monday

    private UiRig(string? settingsJson, Action<Settings>? seed, bool realCoordinator, DateTimeOffset? now, TimeZoneInfo? zone)
    {
        Directory = new TempDirectory("ui");
        Paths = AppPaths.Resolve(name => name == AppPaths.DataDirectoryOverrideVariable ? Directory.Path : null, smokeTest: false);
        if (settingsJson != null) File.WriteAllText(Paths.SettingsFile, settingsJson);
        Store = new SettingsStore(Paths.DataDirectory);
        Settings = Store.Load();
        seed?.Invoke(Settings);
        Clock = new FakeClock(now ?? DefaultNow);
        Zone = zone ?? TimeZoneInfo.Utc;
        if (realCoordinator)
        {
            Engine = new FakePlaybackEngine();
            Real = new PlaybackCoordinator(Settings, Engine, Clock, Mono, Log, Zone);
            Coordinator = Real;
        }
        else
        {
            Fake = new FakeCoordinator(Journal, PlaybackSnapshot.Initial(Settings.Volume));
            Coordinator = Fake;
        }
    }

    public TempDirectory Directory { get; }
    public AppPaths Paths { get; }
    public SettingsStore Store { get; }
    public Settings Settings { get; }
    public Journal Journal { get; } = new();
    public RecordingAppLog Log { get; } = new();
    public FakeClock Clock { get; }
    public FakeMonotonicClock Mono { get; } = new();
    public TimeZoneInfo Zone { get; }

    public IPlaybackCoordinator Coordinator { get; }
    /// <summary>Set when the rig uses the recording coordinator.</summary>
    public FakeCoordinator? Fake { get; }
    /// <summary>Set when the rig uses the real coordinator.</summary>
    public PlaybackCoordinator? Real { get; }
    public FakePlaybackEngine? Engine { get; }

    public FakeStartupRegistration Startup { get; } = new();
    public FakeFileReveal Reveal { get; } = new();
    public FakeShell Shell => shell ??= new FakeShell(Journal);
    private FakeShell? shell;

    /// <summary>View-model mode only.</summary>
    public RecordingDialogService? Recorder { get; private set; }
    /// <summary>Headless mode only.</summary>
    public AvaloniaDialogService? AvaloniaDialogs { get; private set; }

    public JournalingSettingsService SettingsService { get; private set; } = null!;
    public ViewModelServices Services { get; private set; } = null!;
    public MainWindowViewModel ViewModel { get; private set; } = null!;
    public MainWindow? Window { get; private set; }
    public TrayMenuController? Tray { get; private set; }

    public static string Version { get; } = typeof(AppComposition).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>View-model mode (no Avalonia): recording dialogs, inline dispatcher.</summary>
    public static UiRig CreateViewModels(string? settingsJson = null, Action<Settings>? seed = null, bool realCoordinator = false,
        DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var rig = new UiRig(settingsJson, seed, realCoordinator, now, zone);
        var recorder = new RecordingDialogService();
        rig.Recorder = recorder;
        rig.Wire(recorder, recorder, new InlineUiDispatcher());
        return rig;
    }

    /// <summary>
    /// Headless mode: must run inside <see cref="Headless.RunAsync"/>. Shows the real main window at
    /// <paramref name="width"/>×<paramref name="height"/> unless <paramref name="show"/> is false.
    /// </summary>
    public static async Task<UiRig> CreateHeadlessAsync(string? settingsJson = null, Action<Settings>? seed = null, bool realCoordinator = false,
        bool tray = false, bool show = true, double width = 1050, double height = 860, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var rig = new UiRig(settingsJson, seed, realCoordinator, now, zone);
        var dialogs = new AvaloniaDialogService(() => rig.Window);
        rig.AvaloniaDialogs = dialogs;
        rig.Wire(dialogs, dialogs, new AvaloniaUiDispatcher());
        rig.Window = new MainWindow(rig.ViewModel) { Width = width, Height = height };
        if (tray) rig.Tray = new TrayMenuController(Application.Current!, TrayIconOptions.ForCurrentPlatform(), rig.Services, rig.Shell, new AvaloniaUiDispatcher());
        if (show)
        {
            rig.Window.Show();
            await Headless.PumpAsync();
            Headless.Layout(rig.Window);
        }
        return rig;
    }

    private void Wire(IDialogService dialogs, IEditorDialogService editors, IUiDispatcher dispatcher)
    {
        SettingsService = new JournalingSettingsService(new SettingsService(Settings, Store, Coordinator, dialogs, Log), Journal);
        Services = new ViewModelServices(Coordinator, SettingsService, dialogs, editors, Log);
        ViewModel = new MainWindowViewModel(Coordinator, SettingsService, dialogs, editors, Startup, Reveal, dispatcher, Shell, Log, Clock, Zone,
            new AppInfo(Version, Paths.DataDirectory));
    }

    /// <summary>The settings as they are on disk now (a fresh store load).</summary>
    public Settings OnDisk() => new SettingsStore(Paths.DataDirectory).Load();

    public bool SavedToDisk => File.Exists(Paths.SettingsFile);

    /// <summary>Journal entries since <paramref name="mark"/>, joined for readable assertions.</summary>
    public string Since(int mark) => string.Join(" > ", Journal.Since(mark));

    public async ValueTask DisposeAsync()
    {
        Tray?.Dispose();
        ViewModel?.Dispose();
        await Coordinator.DisposeAsync();
        Directory.Dispose();
    }
}
