using System.Globalization;
using DialShift.App.Platform;
using DialShift.App.Services;
using DialShift.Core.Playback;

namespace DialShift.App.ViewModels;

public enum MainPage
{
    Stations,
    Schedule,
    Settings
}

/// <summary>How the player card colors the status line.</summary>
public enum PlayerStatusKind
{
    /// <summary>Stopped, waiting for the schedule, or shutting down.</summary>
    Idle,
    /// <summary>Connecting, reconnecting, or recovering after sleep.</summary>
    Busy,
    /// <summary>Audible playback.</summary>
    Live,
    /// <summary>The stream failed and a retry is counting down.</summary>
    Problem
}

/// <summary>
/// The main window: player card, tabs, footer. It renders the coordinator's immutable <see cref="PlaybackSnapshot"/>
/// (brief 1 §5.3) and sends commands back through <see cref="IPlaybackCoordinator"/>. Snapshots arrive on any thread and
/// are coalesced onto the UI thread through <see cref="IUiDispatcher"/>, the only marshalling point (brief 1 §4.1).
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ViewModelServices services;
    private readonly IPlaybackCoordinator coordinator;
    private readonly ISettingsService settings;
    private readonly LatestValueDispatcher<PlaybackSnapshot> snapshots;
    private PlaybackSnapshot snapshot;
    private MainPage selectedPage;
    private double volume;
    private int volumeRequestsInFlight;
    private bool recoveredNoticeShown;

    public MainWindowViewModel(
        IPlaybackCoordinator coordinator,
        ISettingsService settings,
        IDialogService dialogs,
        IEditorDialogService editors,
        IStartupRegistration startup,
        IFileRevealService reveal,
        IUiDispatcher dispatcher,
        IAppShell shell,
        IAppLog log,
        IClock clock,
        TimeZoneInfo localZone,
        AppInfo info)
    {
        this.coordinator = coordinator;
        this.settings = settings;
        services = new ViewModelServices(coordinator, settings, dialogs, editors, log);
        snapshot = coordinator.Snapshot;
        volume = snapshot.Volume;
        LocalTimeText = UiText.LocalTime(localZone);

        var today = TimeZoneInfo.ConvertTime(clock.UtcNow, localZone).DayOfWeek;
        Stations = new StationsPageViewModel(services);
        Schedule = new SchedulePageViewModel(services, today);
        Settings = new SettingsPageViewModel(services, startup, reveal, info);

        TogglePlayCommand = new AsyncRelayCommand(async () => { await coordinator.ToggleAsync(); await settings.SaveAsync(); }, services.ReportError);
        NextStationCommand = new AsyncRelayCommand(async () => { await coordinator.NextStationAsync(); await settings.SaveAsync(); }, services.ReportError);
        PersistVolumeCommand = new AsyncRelayCommand(async () => await settings.SaveAsync(), services.ReportError);
        HideToTrayCommand = new RelayCommand(shell.HideMainWindow);
        ShowStationsCommand = new RelayCommand(() => SelectedPage = MainPage.Stations);
        ShowScheduleCommand = new RelayCommand(() => SelectedPage = MainPage.Schedule);
        ShowSettingsCommand = new RelayCommand(() => SelectedPage = MainPage.Settings);

        snapshots = new LatestValueDispatcher<PlaybackSnapshot>(dispatcher, ApplySnapshot);
        coordinator.SnapshotChanged += OnSnapshotChanged;
        settings.SettingsChanged += OnSettingsChanged;
        ApplySnapshot(snapshot);
    }

    public StationsPageViewModel Stations { get; }
    public SchedulePageViewModel Schedule { get; }
    public SettingsPageViewModel Settings { get; }

    public MainPage SelectedPage
    {
        get => selectedPage;
        set
        {
            if (!SetProperty(ref selectedPage, value)) return;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(IsStationsSelected));
            OnPropertyChanged(nameof(IsScheduleSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
        }
    }

    public PageViewModel CurrentPage => selectedPage switch
    {
        MainPage.Schedule => Schedule,
        MainPage.Settings => Settings,
        _ => Stations
    };

    public bool IsStationsSelected => selectedPage == MainPage.Stations;
    public bool IsScheduleSelected => selectedPage == MainPage.Schedule;
    public bool IsSettingsSelected => selectedPage == MainPage.Settings;

    public PlaybackSnapshot Snapshot => snapshot;

    public string StatusText => UiText.Status(snapshot);

    public PlayerStatusKind StatusKind => snapshot.Status switch
    {
        PlaybackStatus.Playing => PlayerStatusKind.Live,
        PlaybackStatus.Connecting or PlaybackStatus.Reconnecting or PlaybackStatus.SuspendedBySystem => PlayerStatusKind.Busy,
        PlaybackStatus.Failed => PlayerStatusKind.Problem,
        _ => PlayerStatusKind.Idle
    };

    public bool IsLive => StatusKind == PlayerStatusKind.Live;
    public bool IsBusy => StatusKind == PlayerStatusKind.Busy;
    public bool IsProblem => StatusKind == PlayerStatusKind.Problem;

    public string StationTitle => UiText.Title(snapshot);

    public string TrackText => snapshot.TrackText;

    public string PlayPauseLabel => UiText.PlayPause(snapshot.IsActive);

    public string PlayPauseAutomationName => snapshot.IsActive ? "Pause" : "Play";

    /// <summary>Slider value 0–100. Changing it forwards to the coordinator (0 mutes); the view persists on release.</summary>
    public double Volume
    {
        get => volume;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (!SetProperty(ref volume, clamped)) return;
            OnPropertyChanged(nameof(VolumeLabel));
            services.Run(() => SendVolumeAsync((int)clamped));
        }
    }

    public string VolumeLabel => $"{(int)volume}%";

    public string UpNextText => UiText.UpNext(settings.Settings.ScheduleEnabled, snapshot, CultureInfo.CurrentCulture);

    public string LocalTimeText { get; }

    public string TrayButtonLabel => "↘  Hide to tray";

    public AsyncRelayCommand TogglePlayCommand { get; }
    public AsyncRelayCommand NextStationCommand { get; }
    public AsyncRelayCommand PersistVolumeCommand { get; }
    public RelayCommand HideToTrayCommand { get; }
    public RelayCommand ShowStationsCommand { get; }
    public RelayCommand ShowScheduleCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }

    /// <summary>Reads state that needs a platform call (the verified launch-at-login status). Call once after construction.</summary>
    public Task InitializeAsync() => Settings.LoadStartupStatusAsync();

    /// <summary>Shows the corrupt-settings notice (BHV-03) at most once per process.</summary>
    public async Task ShowSettingsRecoveredAsync(string? warning)
    {
        if (warning == null || recoveredNoticeShown) return;
        recoveredNoticeShown = true;
        await services.Dialogs.ShowMessageAsync(UiText.SettingsRecoveredTitle, warning);
    }

    /// <summary>UI thread. Public so tests can apply a snapshot synchronously.</summary>
    public void ApplySnapshot(PlaybackSnapshot value)
    {
        snapshot = value;
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(StationTitle));
        OnPropertyChanged(nameof(TrackText));
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(PlayPauseAutomationName));
        OnPropertyChanged(nameof(UpNextText));
        if (volumeRequestsInFlight == 0 && SetProperty(ref volume, value.Volume, nameof(Volume))) OnPropertyChanged(nameof(VolumeLabel));
        Stations.ApplySnapshot(value);
    }

    public void Dispose()
    {
        coordinator.SnapshotChanged -= OnSnapshotChanged;
        settings.SettingsChanged -= OnSettingsChanged;
    }

    private async Task SendVolumeAsync(int value)
    {
        volumeRequestsInFlight++;
        try { await coordinator.SetVolumeAsync(value); }
        finally { volumeRequestsInFlight--; }
    }

    private void OnSnapshotChanged(object? sender, PlaybackSnapshot value) => snapshots.Push(value);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Stations.Refresh();
        Schedule.Refresh();
        Settings.Refresh();
        OnPropertyChanged(nameof(UpNextText));
    }
}
