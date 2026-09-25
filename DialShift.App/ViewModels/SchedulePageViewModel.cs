using System.Collections.ObjectModel;
using DialShift.Core;
using DialShift.Core.Playback;

namespace DialShift.App.ViewModels;

/// <summary>A Mon–Sun tab on the Schedule page.</summary>
public sealed class DayTabViewModel(DayOfWeek day, Action<DayOfWeek> select) : ObservableObject
{
    private bool isSelected;

    public DayOfWeek Day { get; } = day;
    public string Label => UiText.ShortDay(Day);
    public string AutomationName => "Show " + Day + " slots";
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
    public RelayCommand SelectCommand { get; } = new(() => select(day));
}

/// <summary>
/// One slot row on the Schedule page (BHV-55). A slot with a zone also shows the zone and its next start on this
/// computer (QA-N6): the row sits under the tab of its own zone's day, which can differ from the local day it fires on.
/// </summary>
public sealed class SlotRowViewModel : ObservableObject
{
    public const string UnknownZoneNote = "Runs on local time";
    public const string UnknownZoneToolTip = "This computer doesn't recognize this time zone, so the slot runs on local time. Edit the slot to choose a zone.";

    private readonly string? zoneName;
    private string nextStartText = "";

    public SlotRowViewModel(ScheduleEntry entry, string? stationName, Func<ScheduleEntry, Task> edit, Action<Exception> onError)
    {
        Entry = entry;
        StationName = stationName ?? "Missing station";
        zoneName = UiText.ZoneName(entry.TimeZone);
        ZoneResolution = Scheduler.TryResolveZone(entry.TimeZone, out _);
        EditCommand = new(() => edit(entry), onError);
    }

    public ScheduleEntry Entry { get; }
    public string Time => Entry.Time;
    public bool IsEnabled => Entry.Enabled;
    public string StationName { get; }
    public string Subtitle => (Entry.Enabled ? string.IsNullOrWhiteSpace(Entry.Label) ? "Scheduled switch" : Entry.Label : "Disabled") + " · " + UiText.Days(Entry.Days);
    public string EditAutomationName => zoneName is null ? $"Edit the {Time} slot" : $"Edit the {Time} {zoneName} slot";
    public AsyncRelayCommand EditCommand { get; }

    public ZoneResolution ZoneResolution { get; }

    /// <summary>The slot has a zone set (resolved or not); the row shows its zone line.</summary>
    public bool HasZone => zoneName != null;

    /// <summary>Shown in the warning color: the slot's zone does not resolve here and it falls back to local time.</summary>
    public bool IsZoneUnknown => ZoneResolution == ZoneResolution.Unknown;

    /// <summary>"Europe/Athens", or "Europe/Foo (unknown zone)"; empty for local time.</summary>
    public string ZoneText => zoneName is null ? "" : IsZoneUnknown ? TimeZoneChoices.UnknownZone(zoneName) : zoneName;

    public string ZoneToolTip => IsZoneUnknown ? UnknownZoneToolTip : $"The start time and days are in {zoneName} time.";

    /// <summary>"Next: Sun 08:30 your time" for a resolved zone, <see cref="UnknownZoneNote"/> for an unknown one, else empty.</summary>
    public string NextStartText { get => nextStartText; private set => SetProperty(ref nextStartText, value); }

    /// <summary>Recomputes <see cref="NextStartText"/> for <paramref name="localNow"/> (computer-local wall time in <paramref name="localZone"/>).</summary>
    public void UpdateNextStart(DateTime localNow, TimeZoneInfo localZone) =>
        NextStartText = ZoneResolution switch
        {
            ZoneResolution.Unknown => UnknownZoneNote,
            ZoneResolution.Resolved when Scheduler.NextFor(Entry, localNow, localZone) is { } next => UiText.NextStart(next.At, Entry.Enabled),
            _ => ""
        };
}

/// <summary>The Schedule tab: follow toggle, day tabs, slots, add/edit/delete (BHV-50, BHV-55 to BHV-58).</summary>
public sealed class SchedulePageViewModel : PageViewModel
{
    private readonly IClock clock;
    private readonly TimeZoneInfo localZone;
    private DayOfWeek selectedDay;
    private bool isEmpty;
    private string emptyText = "";

    /// <param name="clock">Wall clock for today's tab, the rows' next starts and the editor's zone offsets.</param>
    /// <param name="localZone">The computer's zone, the same one the coordinator evaluates the schedule in.</param>
    public SchedulePageViewModel(ViewModelServices services, IClock clock, TimeZoneInfo localZone) : base(services)
    {
        this.clock = clock;
        this.localZone = localZone;
        selectedDay = LocalNow().DayOfWeek;
        DayTabs = UiText.Week.Select(d => new DayTabViewModel(d, SelectDay)).ToList();
        AddCommand = new AsyncRelayCommand(() => EditAsync(null), services.ReportError, () => Services.Settings.Settings.Stations.Count > 0);
        Refresh();
    }

    public override string Title => "Make radio a routine";
    public override string Description => "Each slot switches station at its start time. It plays until the next switch.";

    public IReadOnlyList<DayTabViewModel> DayTabs { get; }

    public ObservableCollection<SlotRowViewModel> Slots { get; } = [];

    public DayOfWeek SelectedDay => selectedDay;

    /// <summary>"Follow my schedule" (BHV-50). Same commit path as the tray item, so both stay in sync.</summary>
    public bool IsScheduleEnabled
    {
        get => Services.Settings.Settings.ScheduleEnabled;
        set
        {
            if (value == Services.Settings.Settings.ScheduleEnabled) return;
            Services.Settings.Settings.ScheduleEnabled = value;
            OnPropertyChanged();
            Services.Run(() => Services.Settings.CommitAsync(SettingsChange.Schedule));
        }
    }

    public bool IsEmpty { get => isEmpty; private set => SetProperty(ref isEmpty, value); }

    public string EmptyTitle => "A little room for spontaneity.";

    public string EmptyText { get => emptyText; private set => SetProperty(ref emptyText, value); }

    public string HelperText => "Each slot runs in its own time zone (Local time by default). Changed your computer's time zone? Restart DialShift. " +
        "Pause or pick a station manually until the next slot. After sleep, DialShift catches up with the current slot.";

    public AsyncRelayCommand AddCommand { get; }

    public void SelectDay(DayOfWeek day)
    {
        selectedDay = day;
        OnPropertyChanged(nameof(SelectedDay));
        Refresh();
    }

    public override void Refresh()
    {
        var settings = Services.Settings.Settings;
        foreach (var tab in DayTabs) tab.IsSelected = tab.Day == selectedDay;
        Slots.Clear();
        // By start time, so a legacy "08.30" sorts with "08:30" (its text as stored breaks ties and orders unparsable ones last).
        foreach (var entry in settings.Schedule.Where(e => e.Days.Contains(selectedDay))
                     .OrderBy(e => ScheduleEditorViewModel.TryParseTime(e.Time, out var t) ? t.Ticks : long.MaxValue).ThenBy(e => e.Time, StringComparer.Ordinal))
            Slots.Add(new SlotRowViewModel(entry, settings.Stations.FirstOrDefault(s => s.Id == entry.StationId)?.Name, EditAsync, Services.ReportError));
        UpdateNextStarts();
        IsEmpty = Slots.Count == 0;
        EmptyText = "No switches on " + selectedDay + ". Add a time slot to tune in automatically.";
        OnPropertyChanged(nameof(IsScheduleEnabled));
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Recomputes each zoned row's next local start. The page calls it on a timer while visible, so a start that passed moves on.</summary>
    public void UpdateNextStarts()
    {
        var now = LocalNow();
        foreach (var row in Slots) row.UpdateNextStart(now, localZone);
    }

    /// <summary>Computer-local wall time (Kind Unspecified), as the coordinator computes it for the schedule.</summary>
    private DateTime LocalNow() => TimeZoneInfo.ConvertTime(clock.UtcNow, localZone).DateTime;

    private async Task EditAsync(ScheduleEntry? entry)
    {
        var editor = new ScheduleEditorViewModel(Services.Settings.Settings, entry, selectedDay, clock.UtcNow, Services.Dialogs, Services.ReportError);
        switch (await Services.Editors.ShowScheduleEditorAsync(editor))
        {
            case EditorResult.Saved:
                await Services.Settings.CommitAsync(SettingsChange.Schedule);
                break;
            case EditorResult.Deleted when entry != null:
                Services.Settings.Settings.Schedule.Remove(entry);
                await Services.Settings.CommitAsync(SettingsChange.Schedule);
                break;
        }
    }
}
