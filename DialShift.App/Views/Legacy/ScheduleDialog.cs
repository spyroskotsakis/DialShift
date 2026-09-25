using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DialShift.Core;
using static DialShift.App.Views.Legacy.MainWindow;

namespace DialShift.App.Views.Legacy;

public sealed class ScheduleDialog : EditorDialog
{
    public ScheduleDialog(App app, ScheduleEntry? original, DayOfWeek selectedDay) : base(original == null ? "Plan your next switch" : "Edit time slot", "Choose when to tune in. The station continues until the next scheduled switch.")
    {
        var label = Field("Show / label (optional)", original?.Label ?? "", 150);
        Form.Children.Add(Text("Station", 13, true));
        var station = new ComboBox { ItemsSource = app.Settings.Stations, SelectedItem = app.Settings.Stations.FirstOrDefault(s => s.Id == original?.StationId) ?? app.Settings.Stations.FirstOrDefault() };
        Form.Children.Add(station);
        var time = Field("Start time · 24-hour HH:mm", original?.Time ?? "08:00", 5);
        Form.Children.Add(Text("Repeat on", 13, true));
        var days = new WrapPanel(); var checks = new Dictionary<DayOfWeek, CheckBox>();
        foreach (var day in Week) { var check = new CheckBox { Content = day.ToString()[..3], IsChecked = original == null ? day == selectedDay : original.Days.Contains(day) }; checks.Add(day, check); days.Children.Add(check); }
        Form.Children.Add(days);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 6), Spacing = 8 };
        presets.Children.Add(Button("Weekdays", () => { foreach (var pair in checks) pair.Value.IsChecked = pair.Key is not DayOfWeek.Saturday and not DayOfWeek.Sunday; }));
        presets.Children.Add(Button("Weekend", () => { foreach (var pair in checks) pair.Value.IsChecked = pair.Key is DayOfWeek.Saturday or DayOfWeek.Sunday; }));
        presets.Children.Add(Button("Every day", () => { foreach (var check in checks.Values) check.IsChecked = true; }));
        Form.Children.Add(presets);
        var enabled = new CheckBox { Content = "Enable this time slot", IsChecked = original?.Enabled ?? true }; Form.Children.Add(enabled);
        if (original != null) { var orig = original; Actions.Children.Add(Button("Delete slot", () => { app.Settings.Schedule.Remove(orig); Close(true); })); }
        FinishButtons(() =>
        {
            if (station.SelectedItem is not Station selected) { Error.Text = "Choose a station first."; return; }
            if (!Scheduler.TryTime((time.Text ?? "").Trim(), out var parsed)) { Error.Text = "Use a 24-hour time, such as 08:30 or 21:00."; time.Focus(); return; }
            var selectedDays = checks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
            if (selectedDays.Count == 0) { Error.Text = "Choose at least one day."; return; }
            var entry = new ScheduleEntry { Id = original?.Id ?? Guid.NewGuid(), StationId = selected.Id, Label = (label.Text ?? "").Trim(), Time = parsed.ToString("HH:mm"), Days = selectedDays, Enabled = enabled.IsChecked == true };
            if (Scheduler.Conflicts(app.Settings.Schedule, entry)) { Error.Text = "Another enabled slot already starts at this time on one of those days."; return; }
            if (original != null) app.Settings.Schedule.Remove(original!);
            app.Settings.Schedule.Add(entry);
            Close(true);
        });
    }
}
