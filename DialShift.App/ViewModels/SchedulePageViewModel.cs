using System.Collections.ObjectModel;
using DialShift.Core;

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

/// <summary>One slot row on the Schedule page (BHV-55).</summary>
public sealed class SlotRowViewModel(ScheduleEntry entry, string? stationName, Func<ScheduleEntry, Task> edit, Action<Exception> onError)
{
    public ScheduleEntry Entry { get; } = entry;
    public string Time => Entry.Time;
    public bool IsEnabled => Entry.Enabled;
    public string StationName { get; } = stationName ?? "Missing station";
    public string Subtitle => (Entry.Enabled ? string.IsNullOrWhiteSpace(Entry.Label) ? "Scheduled switch" : Entry.Label : "Disabled") + " · " + UiText.Days(Entry.Days);
    public string EditAutomationName => $"Edit the {Time} slot";
    public AsyncRelayCommand EditCommand { get; } = new(() => edit(entry), onError);
}

/// <summary>The Schedule tab: follow toggle, day tabs, slots, add/edit/delete (BHV-50, BHV-55 to BHV-58).</summary>
public sealed class SchedulePageViewModel : PageViewModel
{
    private DayOfWeek selectedDay;
    private bool isEmpty;
    private string emptyText = "";

    public SchedulePageViewModel(ViewModelServices services, DayOfWeek today) : base(services)
    {
        selectedDay = today;
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

    public string HelperText => "Times follow your local time zone. Pause or pick a station manually until the next slot. After sleep, DialShift catches up with the current slot.";

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
        foreach (var entry in settings.Schedule.Where(e => e.Days.Contains(selectedDay)).OrderBy(e => e.Time, StringComparer.Ordinal))
            Slots.Add(new SlotRowViewModel(entry, settings.Stations.FirstOrDefault(s => s.Id == entry.StationId)?.Name, EditAsync, Services.ReportError));
        IsEmpty = Slots.Count == 0;
        EmptyText = "No switches on " + selectedDay + ". Add a time slot to tune in automatically.";
        OnPropertyChanged(nameof(IsScheduleEnabled));
        AddCommand.NotifyCanExecuteChanged();
    }

    private async Task EditAsync(ScheduleEntry? entry)
    {
        var editor = new ScheduleEditorViewModel(Services.Settings.Settings, entry, selectedDay, Services.Dialogs, Services.ReportError);
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
