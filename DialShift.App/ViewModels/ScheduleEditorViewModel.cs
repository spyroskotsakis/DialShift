using DialShift.App.Services;
using DialShift.Core;

namespace DialShift.App.ViewModels;

/// <summary>One day checkbox in the slot editor.</summary>
public sealed class DayChoiceViewModel(DayOfWeek day, bool isChecked) : ObservableObject
{
    private bool isChecked = isChecked;

    public DayOfWeek Day { get; } = day;

    public string Label => UiText.ShortDay(Day);

    public bool IsChecked { get => isChecked; set => SetProperty(ref isChecked, value); }
}

/// <summary>Add/edit slot dialog (BHV-56) with conflict check (BHV-57) and delete confirmation (BHV-58, OQ-10).</summary>
public sealed class ScheduleEditorViewModel : EditorViewModel
{
    public const int LabelMaxLength = 150;
    public const int TimeMaxLength = 5;

    public const string LabelLabel = "Show / label (optional)";
    public const string StationLabel = "Station";
    public const string StationAutomationName = "Scheduled station";
    public const string TimeLabel = "Start time · 24-hour HH:mm";

    private readonly Settings settings;
    private readonly IDialogService dialogs;
    private string label;
    private Station? selectedStation;
    private string time;
    private bool enabled;

    public ScheduleEditorViewModel(Settings settings, ScheduleEntry? original, DayOfWeek selectedDay, IDialogService dialogs, Action<Exception> onError)
        : base(original == null ? "Plan your next switch" : "Edit time slot",
            "Choose when to tune in. The station continues until the next scheduled switch.",
            canDelete: original != null, onError)
    {
        this.settings = settings;
        this.dialogs = dialogs;
        Original = original;
        Stations = settings.Stations.ToList();
        label = original?.Label ?? "";
        selectedStation = Stations.FirstOrDefault(s => s.Id == original?.StationId) ?? Stations.FirstOrDefault();
        time = original?.Time ?? "08:00";
        enabled = original?.Enabled ?? true;
        Days = UiText.Week.Select(d => new DayChoiceViewModel(d, original == null ? d == selectedDay : original.Days.Contains(d))).ToList();
        foreach (var day in Days) day.PropertyChanged += (_, _) => Error = null;
        WeekdaysCommand = new RelayCommand(() => SetDays(d => d is not DayOfWeek.Saturday and not DayOfWeek.Sunday));
        WeekendCommand = new RelayCommand(() => SetDays(d => d is DayOfWeek.Saturday or DayOfWeek.Sunday));
        EveryDayCommand = new RelayCommand(() => SetDays(_ => true));
    }

    public ScheduleEntry? Original { get; }


    public override string DeleteLabel => "Delete slot";

    public IReadOnlyList<Station> Stations { get; }

    public IReadOnlyList<DayChoiceViewModel> Days { get; }

    public string Label { get => label; set => Edit(ref label, value ?? ""); }

    public Station? SelectedStation { get => selectedStation; set => Edit(ref selectedStation, value); }

    public string Time { get => time; set => Edit(ref time, value ?? ""); }

    public bool Enabled { get => enabled; set => Edit(ref enabled, value); }

    public RelayCommand WeekdaysCommand { get; }

    public RelayCommand WeekendCommand { get; }

    public RelayCommand EveryDayCommand { get; }

    private void SetDays(Func<DayOfWeek, bool> include)
    {
        foreach (var day in Days) day.IsChecked = include(day.Day);
    }

    protected override void Save()
    {
        if (SelectedStation is not { } station) { Fail("Choose a station first.", nameof(SelectedStation)); return; }
        if (!Scheduler.TryTime(Time.Trim(), out var parsed)) { Fail("Use a 24-hour time, such as 08:30 or 21:00.", nameof(Time)); return; }
        var days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToList();
        if (days.Count == 0) { Fail("Choose at least one day."); return; }

        var entry = new ScheduleEntry
        {
            Id = Original?.Id ?? Guid.NewGuid(),
            StationId = station.Id,
            Label = Label.Trim(),
            Time = parsed.ToString("HH:mm"),
            Days = days,
            Enabled = Enabled
        };
        if (Scheduler.Conflicts(settings.Schedule, entry)) { Fail("Another enabled slot already starts at this time on one of those days.", nameof(Time)); return; }

        if (Original != null) settings.Schedule.Remove(Original);
        settings.Schedule.Add(entry);
        Error = null;
        Close(EditorResult.Saved);
    }

    protected override async Task DeleteAsync()
    {
        if (Original is not { } entry) return;
        var stationName = settings.Stations.FirstOrDefault(s => s.Id == entry.StationId)?.Name ?? "a missing station";
        if (await dialogs.ConfirmAsync("Delete slot", UiText.DeleteSlotQuestion(entry, stationName), "Delete", "Cancel"))
            Close(EditorResult.Deleted);
    }
}
