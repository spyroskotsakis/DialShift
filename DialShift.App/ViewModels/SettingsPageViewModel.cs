using System.Reflection;
using DialShift.App.Platform;
using DialShift.App.Services;

namespace DialShift.App.ViewModels;

/// <summary>An entry of the fallback picker; <see cref="Id"/> null is "No fallback · keep retrying".</summary>
public sealed record FallbackOption(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Version and data folder shown on the Settings page.</summary>
public sealed record AppInfo(string Version, string DataDirectory)
{
    /// <summary>The full SemVer of <paramref name="assembly"/> for display, such as "0.3.0-rc.1" (see the other overload).</summary>
    public static string DisplayVersion(Assembly assembly) =>
        DisplayVersion(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, assembly.GetName().Version);

    /// <summary>
    /// The informational version with its build metadata (<c>+&lt;commit&gt;</c>) removed, so a pre-release suffix such as
    /// <c>-rc.1</c> stays; else the assembly version as <c>major.minor.patch</c>; else "unknown".
    /// </summary>
    public static string DisplayVersion(string? informationalVersion, Version? assemblyVersion)
    {
        var semVer = informationalVersion?.Split('+', 2)[0].Trim();
        return !string.IsNullOrEmpty(semVer) ? semVer : assemblyVersion?.ToString(3) ?? "unknown";
    }
}

/// <summary>The Settings tab: launch at login, start in tray, fallback, about (BHV-59 to BHV-63).</summary>
public sealed class SettingsPageViewModel : PageViewModel
{
    public const string NoFallbackName = "No fallback · keep retrying";

    /// <summary>The launch-at-login checkbox in each platform's own words: Windows says "sign in", macOS "log in".</summary>
    public const string WindowsLaunchAtLoginLabel = "Launch DialShift in the tray when I sign in";
    public const string MacLaunchAtLoginLabel = "Launch DialShift in the tray when I log in";

    private readonly IStartupRegistration startup;
    private readonly IFileRevealService reveal;
    private readonly AppInfo info;
    private readonly ISettingsTransferService transfer;
    private readonly ITransferFilePicker transferPicker;
    private bool launchAtLogin;
    private bool isStartupBusy;
    private bool isTransferBusy;
    private string? startupDiagnostic;
    private IReadOnlyList<FallbackOption> fallbackOptions = [];
    private FallbackOption? selectedFallback;
    private bool refreshing;

    public SettingsPageViewModel(ViewModelServices services, IStartupRegistration startup, IFileRevealService reveal, AppInfo info,
        ISettingsTransferService transfer, ITransferFilePicker transferPicker) : base(services)
    {
        this.startup = startup;
        this.reveal = reveal;
        this.info = info;
        this.transfer = transfer;
        this.transferPicker = transferPicker;
        launchAtLogin = services.Settings.Settings.LaunchAtLogin;
        OpenSettingsFolderCommand = new AsyncRelayCommand(OpenSettingsFolderAsync, services.ReportError);
        ExportStationsCommand = new AsyncRelayCommand(ExportAsync, services.ReportError, () => !isTransferBusy);
        ImportStationsCommand = new AsyncRelayCommand(ImportAsync, services.ReportError, () => !isTransferBusy);
        Refresh();
    }

    public override string Title => "Set it. Forget it.";
    public override string Description => "Small preferences for your daily listening.";

    /// <summary>
    /// Reflects the verified OS registration (<see cref="StartupRegistrationStatus"/>), not the stored flag. Setting it
    /// calls <see cref="IStartupRegistration.SetEnabledAsync"/> once; a failure shows <see cref="StartupDiagnostic"/> inline
    /// and the checkbox reverts to the verified state without a second call (fixes the BHV-59 double-call bug).
    /// </summary>
    public bool LaunchAtLogin
    {
        get => launchAtLogin;
        set
        {
            if (value == launchAtLogin || isStartupBusy) return;
            SetProperty(ref launchAtLogin, value);
            Services.Run(() => ApplyLaunchAtLoginAsync(value));
        }
    }

    /// <summary>True while a registration call is in flight; the checkbox is disabled meanwhile.</summary>
    public bool IsStartupBusy
    {
        get => isStartupBusy;
        private set
        {
            if (SetProperty(ref isStartupBusy, value)) OnPropertyChanged(nameof(IsStartupEditable));
        }
    }

    public bool IsStartupEditable => !isStartupBusy;

    /// <summary>
    /// True from the moment an export or import starts until it ends (the picker included), so neither transfer can run
    /// twice and both buttons are disabled meanwhile (brief 5 §6, IE-08). Honest state: the flag is set before any await
    /// and cleared in a <c>finally</c>.
    /// </summary>
    public bool IsTransferBusy
    {
        get => isTransferBusy;
        private set
        {
            if (!SetProperty(ref isTransferBusy, value)) return;
            OnPropertyChanged(nameof(IsTransferEnabled));
            ExportStationsCommand.NotifyCanExecuteChanged();
            ImportStationsCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The two transfer buttons' <c>IsEnabled</c>: false while a transfer is in flight.</summary>
    public bool IsTransferEnabled => !isTransferBusy;

    public string? StartupDiagnostic
    {
        get => startupDiagnostic;
        private set
        {
            if (SetProperty(ref startupDiagnostic, value)) OnPropertyChanged(nameof(HasStartupDiagnostic));
        }
    }

    public bool HasStartupDiagnostic => !string.IsNullOrEmpty(startupDiagnostic);

    public bool StartInTray
    {
        get => Services.Settings.Settings.StartInTray;
        set
        {
            if (value == Services.Settings.Settings.StartInTray) return;
            Services.Settings.Settings.StartInTray = value;
            OnPropertyChanged();
            Services.Run(async () => await Services.Settings.SaveAsync());
        }
    }

    public IReadOnlyList<FallbackOption> FallbackOptions { get => fallbackOptions; private set => SetProperty(ref fallbackOptions, value); }

    /// <summary>Fallback picker (BHV-61): sets <c>FallbackStationId</c>, tells the coordinator, saves.</summary>
    public FallbackOption? SelectedFallback
    {
        get => selectedFallback;
        set
        {
            if (value == null || Equals(value, selectedFallback)) return;
            SetProperty(ref selectedFallback, value);
            if (refreshing || Services.Settings.Settings.FallbackStationId == value.Id) return;
            Services.Settings.Settings.FallbackStationId = value.Id;
            Services.Run(() => Services.Settings.CommitAsync(SettingsChange.Stations));
        }
    }

    public string LaunchAtLoginLabel => OperatingSystem.IsWindows() ? WindowsLaunchAtLoginLabel : MacLaunchAtLoginLabel;
    public string StartInTrayLabel => "Start in the tray when opened normally";
    public string StartupHelp => "Closing the window keeps your radio running. Choose Quit DialShift in the tray to exit.";
    public string FallbackHelp => "Retry a failed stream, then use this station as a fallback. Try the original again every 2 minutes.";
    public string VersionText => $"DialShift  /  {info.Version}";
    public string Tagline => "Your stations. Your schedule. Stored on this computer.";

    public AsyncRelayCommand OpenSettingsFolderCommand { get; }

    /// <summary>Export: the Save picker, then the transfer service's own file write and success dialog (brief 5 §6).</summary>
    public AsyncRelayCommand ExportStationsCommand { get; }

    /// <summary>Import: the Open picker, then the transfer service's own validation, confirmation and success dialog (brief 5 §6).</summary>
    public AsyncRelayCommand ImportStationsCommand { get; }

    /// <summary>Reads the OS registration once the page exists, so a stale entry (moved or upgraded app) shows as off with its reason. Logs <c>startup_registration.result</c>.</summary>
    public async Task LoadStartupStatusAsync()
    {
        IsStartupBusy = true;
        try
        {
            var status = await startup.GetStatusAsync();
            Services.Log.Info("startup_registration.result", $"check enabled={status.IsEnabled} stored={Services.Settings.Settings.LaunchAtLogin} diagnostic={status.DiagnosticMessage != null}");
            await ApplyStatusAsync(status);
        }
        finally { IsStartupBusy = false; }
    }

    public override void Refresh()
    {
        refreshing = true;
        try
        {
            var settings = Services.Settings.Settings;
            var options = new List<FallbackOption> { new(null, NoFallbackName) };
            options.AddRange(settings.Stations.Select(s => new FallbackOption(s.Id, s.Name)));
            if (!options.SequenceEqual(fallbackOptions)) FallbackOptions = options;
            SelectedFallback = FallbackOptions.FirstOrDefault(o => o.Id == settings.FallbackStationId) ?? FallbackOptions[0];
            OnPropertyChanged(nameof(StartInTray));
        }
        finally { refreshing = false; }
    }

    private async Task ApplyLaunchAtLoginAsync(bool enabled)
    {
        IsStartupBusy = true;
        try
        {
            StartupRegistrationStatus status;
            try { status = await startup.SetEnabledAsync(enabled); }
            catch (Exception ex)
            {
                // The contract says implementations never throw; stay safe if one does.
                Services.Log.Error("startup_registration.result", "Startup registration threw.", ex);
                status = new StartupRegistrationStatus(Services.Settings.Settings.LaunchAtLogin, ex.Message);
            }
            if (status.IsEnabled != enabled && string.IsNullOrEmpty(status.DiagnosticMessage))
                status = status with { DiagnosticMessage = UiText.StartupUpdateFailedTitle + "." };
            Services.Log.Info("startup_registration.result", $"requested={enabled} enabled={status.IsEnabled} diagnostic={status.DiagnosticMessage != null}");
            await ApplyStatusAsync(status);
        }
        finally { IsStartupBusy = false; }
    }

    private async Task ApplyStatusAsync(StartupRegistrationStatus status)
    {
        launchAtLogin = status.IsEnabled;
        OnPropertyChanged(nameof(LaunchAtLogin));
        StartupDiagnostic = status.DiagnosticMessage;
        if (Services.Settings.Settings.LaunchAtLogin == status.IsEnabled) return;
        Services.Settings.Settings.LaunchAtLogin = status.IsEnabled;
        await Services.Settings.SaveAsync();
    }

    private async Task OpenSettingsFolderAsync()
    {
        // Missing folder (IOException), no access, the file manager couldn't launch, failed or didn't answer in time.
        try { await reveal.RevealInFileManagerAsync(info.DataDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                       System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            Services.Log.Warn("ui.reveal_failed", "Couldn't open the settings folder.", ex);
            await Services.Dialogs.ShowMessageAsync(UiText.OpenFolderFailedTitle, ex.Message);
        }
    }

    /// <summary>
    /// Export (brief 5 §6, D108, D113): asks the picker for a path with the date-stamped default name, then hands it to
    /// the transfer service, which writes the file atomically, logs the counts and shows the counts dialog. A cancelled
    /// picker (null) changes nothing. Guarded by <see cref="IsTransferBusy"/> so neither transfer can run twice.
    /// </summary>
    private async Task ExportAsync()
    {
        IsTransferBusy = true;
        try
        {
            var path = await transferPicker.PickSavePathAsync(UiText.TransferFileName(DateTimeOffset.Now));
            if (path is null) return;
            await transfer.ExportAsync(path);
        }
        finally { IsTransferBusy = false; }
    }

    /// <summary>
    /// Import (brief 5 §6, D113): asks the picker for a file, then hands it to the transfer service, which validates
    /// fully before mutating, confirms, replaces the stations and schedule and shows the counts. A cancelled picker
    /// (null) or a cancelled confirmation changes nothing and shows no dialog. Guarded by <see cref="IsTransferBusy"/>.
    /// </summary>
    private async Task ImportAsync()
    {
        IsTransferBusy = true;
        try
        {
            var path = await transferPicker.PickOpenPathAsync();
            if (path is null) return;
            await transfer.ImportAsync(path);
        }
        finally { IsTransferBusy = false; }
    }
}
