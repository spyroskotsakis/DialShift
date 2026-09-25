using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using DialShift.App.Services;
using DialShift.App.ViewModels;
using DialShift.Core.Playback;

namespace DialShift.App.Tray;

/// <summary>
/// The tray / menu-bar icon and its menu (BHV-19 to BHV-23).
/// <para><b>Identity rule (brief 1 §7.6, macOS crash pitfall).</b> The <see cref="TrayIcon"/> and its root
/// <see cref="NativeMenu"/> are created exactly once, here, and registered with <see cref="TrayIcon.SetIcons"/> exactly
/// once. Avalonia's macOS exporter binds its native proxy to that menu instance, so every refresh mutates
/// <see cref="RootMenu"/>.<c>Items</c> in place (<c>Clear()</c> + re-add). Nothing ever assigns <c>TrayIcon.Menu</c> again or
/// calls <c>SetIcons</c> again. <see cref="RootMenu"/> is exposed so tests can assert
/// <c>ReferenceEquals(TrayIcon.Menu, RootMenu)</c> after every editor operation (HS-03).</para>
/// <para>Refreshes are coalesced: snapshots are applied at most once per UI-thread turn, and the items are rebuilt only
/// when something the menu shows changed (stations, the follow-schedule check, play state, the station on air).</para>
/// </summary>
public sealed class TrayMenuController : IDisposable
{
    private readonly ViewModelServices services;
    private readonly IAppShell shell;
    private readonly LatestValueDispatcher<PlaybackSnapshot> snapshots;
    private PlaybackSnapshot snapshot;
    private string? renderedSignature;
    private bool disposed;

    public TrayMenuController(Application application, TrayIconOptions options, ViewModelServices services, IAppShell shell, IUiDispatcher dispatcher)
    {
        this.services = services;
        this.shell = shell;
        snapshot = services.Coordinator.Snapshot;
        RootMenu = new NativeMenu();
        Refresh();

        TrayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(options.IconUri)),
            ToolTipText = UiText.TrayTooltip(snapshot),
            Menu = RootMenu,
            IsVisible = true
        };
        if (options.IsTemplateIcon) MacOSProperties.SetIsTemplateIcon(TrayIcon, true);
        if (options.OpenWindowOnClick) TrayIcon.Clicked += OnClicked;
        TrayIcon.SetIcons(application, [TrayIcon]);

        snapshots = new LatestValueDispatcher<PlaybackSnapshot>(dispatcher, ApplySnapshot);
        services.Coordinator.SnapshotChanged += OnSnapshotChanged;
        services.Settings.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>The single tray icon; registered once.</summary>
    public TrayIcon TrayIcon { get; }

    /// <summary>The single root menu for the process lifetime. Never replaced.</summary>
    public NativeMenu RootMenu { get; }

    /// <summary>How many times the items were rebuilt (tests check coalescing).</summary>
    public int RebuildCount { get; private set; }

    /// <summary>UI thread. Rebuilds the items in place when what they show changed; <paramref name="force"/> always rebuilds.</summary>
    public void Refresh(bool force = false)
    {
        if (disposed) return;
        var settings = services.Settings.Settings;
        var onAir = snapshot.IsActive ? snapshot.CurrentStationId ?? snapshot.DesiredStationId : null;
        var signature = string.Join('|',
            snapshot.IsActive, onAir, settings.ScheduleEnabled,
            string.Join(',', settings.Stations.Select(s => s.Id + "=" + s.Name)));
        if (!force && signature == renderedSignature) return;
        renderedSignature = signature;
        RebuildCount++;

        var items = RootMenu.Items;
        items.Clear();
        items.Add(Item("Open DialShift", shell.ShowMainWindow));
        items.Add(Item(snapshot.IsActive ? "Pause" : "Play", async () =>
        {
            await services.Coordinator.ToggleAsync();
            await services.Settings.SaveAsync();
        }));
        items.Add(Item("Next station", async () =>
        {
            await services.Coordinator.NextStationAsync();
            await services.Settings.SaveAsync();
        }));

        var stations = new NativeMenu();
        foreach (var station in settings.Stations)
        {
            var id = station.Id;
            var item = Item(station.Name, async () =>
            {
                await services.Coordinator.PlayAsync(id);
                await services.Settings.SaveAsync();
            });
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = id == onAir;
            stations.Items.Add(item);
        }
        if (settings.Stations.Count == 0) stations.Items.Add(new NativeMenuItem("No saved stations") { IsEnabled = false });
        items.Add(new NativeMenuItem("Stations") { Menu = stations });

        var follow = Item("Follow schedule", async () =>
        {
            services.Settings.Settings.ScheduleEnabled = !services.Settings.Settings.ScheduleEnabled;
            await services.Settings.CommitAsync(SettingsChange.Schedule);
        });
        follow.ToggleType = MenuItemToggleType.CheckBox;
        follow.IsChecked = settings.ScheduleEnabled;
        items.Add(follow);

        items.Add(new NativeMenuItemSeparator());
        items.Add(Item("Volume +10", () => ChangeVolumeAsync(+10)));
        items.Add(Item("Volume −10", () => ChangeVolumeAsync(-10)));
        items.Add(new NativeMenuItemSeparator());
        items.Add(Item("Quit DialShift", shell.Quit));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        services.Coordinator.SnapshotChanged -= OnSnapshotChanged;
        services.Settings.SettingsChanged -= OnSettingsChanged;
        TrayIcon.Clicked -= OnClicked;
        TrayIcon.IsVisible = false;
        TrayIcon.Dispose();
    }

    private async Task ChangeVolumeAsync(int delta)
    {
        await services.Coordinator.SetVolumeAsync(services.Settings.Settings.Volume + delta);
        await services.Settings.SaveAsync();
    }

    private NativeMenuItem Item(string header, Action action) => new(header) { Command = new RelayCommand(action) };

    private NativeMenuItem Item(string header, Func<Task> action) => new(header) { Command = new AsyncRelayCommand(action, services.ReportError) };

    private void ApplySnapshot(PlaybackSnapshot value)
    {
        if (disposed) return;
        snapshot = value;
        var tooltip = UiText.TrayTooltip(value);
        if (TrayIcon.ToolTipText != tooltip) TrayIcon.ToolTipText = tooltip;
        Refresh();
    }

    private void OnSnapshotChanged(object? sender, PlaybackSnapshot value) => snapshots.Push(value);

    private void OnSettingsChanged(object? sender, EventArgs e) => Refresh();

    private void OnClicked(object? sender, EventArgs e) => shell.ShowMainWindow();
}
