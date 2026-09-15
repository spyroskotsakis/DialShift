using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DialShift.Core;
using static DialShift.MainWindow;

namespace DialShift;

public class EditorDialog : Window
{
    protected readonly StackPanel Form = new();
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Spacing = 8 };
    protected readonly TextBlock Error = Text("", 13, false, "#FFB5A7", new Thickness(0, 10, 0, 0));

    protected EditorDialog(string title, string description)
    {
        Title = title + " · DialShift"; Width = 580; Height = 630; MinWidth = 510; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new DockPanel { Margin = new Thickness(26) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(Text(title, 26, true)); header.Children.Add(Text(description, 13, false, "#A3B4B6", new Thickness(0, 8, 0, 0)));
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        DockPanel.SetDock(Actions, Dock.Bottom); panel.Children.Add(Actions);
        DockPanel.SetDock(Error, Dock.Bottom); panel.Children.Add(Error);
        panel.Children.Add(new ScrollViewer { Content = Form }); Content = panel;
    }

    protected TextBox Field(string label, string value, int maxLength = 500)
    {
        Form.Children.Add(Text(label, 13, true));
        var field = new TextBox { Text = value, MaxLength = maxLength };
        Form.Children.Add(field); return field;
    }

    protected void FinishButtons(Action save)
    {
        var cancel = Button("Cancel", () => Close(false)); cancel.IsCancel = true; Actions.Children.Add(cancel);
        var submit = Button("Save", save, true); submit.IsDefault = true; Actions.Children.Add(submit);
    }
}

public sealed class StationDialog : EditorDialog
{
    public StationDialog(App app, Station? original) : base(original == null ? "Add a frequency" : "Edit station", "Use the direct audio stream URL from the station's player or website.")
    {
        var name = Field("Station name", original?.Name ?? "", 100);
        var tag = Field("Description / genre", original?.Tag ?? "", 160);
        var url = Field("Stream URL · https://…", original?.Url ?? "", 2048);
        Form.Children.Add(Text("MP3, AAC and HLS streams are supported. A webpage URL usually won't play. Station icons use the first letter of their name.", 12, false, "#A3B4B6"));
        if (original != null)
        {
            var orig = original;
            var delete = Button("Delete station", () => DeleteStation(app, orig));
            delete.Foreground = Brush("#FFB5A7"); Actions.Children.Add(delete);
        }
        FinishButtons(() =>
        {
            var nameText = (name.Text ?? "").Trim();
            var tagText = (tag.Text ?? "").Trim();
            var urlText = (url.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(nameText)) { Error.Text = "Give this station a name."; name.Focus(); return; }
            if (!SettingsStore.ValidUrl(urlText)) { Error.Text = "Enter a valid HTTP or HTTPS stream URL."; url.Focus(); return; }
            var station = original ?? new Station();
            var changedUrl = station.Url != urlText;
            station.Name = nameText; station.Tag = string.IsNullOrWhiteSpace(tagText) ? "Internet radio" : tagText; station.Url = urlText;
            if (original == null) app.Settings.Stations.Add(station);
            else if (changedUrl && app.Radio.IsActive && app.Radio.Desired?.Id == station.Id) app.Radio.Play(station);
            Close(true);
        });
        Opened += (_, _) => name.Focus();
    }

    private async void DeleteStation(App app, Station original)
    {
        var count = app.Settings.Schedule.Count(e => e.StationId == original.Id);
        if (!await Message.Confirm(this, "Delete station", $"Delete {original.Name}" + (count > 0 ? $" and its {count} schedule slot(s)?" : "?"))) return;
        if (app.Radio.Desired?.Id == original.Id || app.Radio.Current?.Id == original.Id) app.Radio.Pause();
        app.Settings.Stations.Remove(original); app.Settings.Schedule.RemoveAll(e => e.StationId == original.Id);
        if (app.Settings.FallbackStationId == original.Id) app.Settings.FallbackStationId = null;
        if (app.Settings.LastStationId == original.Id) app.Settings.LastStationId = null;
        app.Radio.ForgetStation(original.Id);
        app.Radio.RefreshSchedule();
        Close(true);
    }
}

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

public static class Message
{
    public static Task Show(Window? owner, string title, string message) => ShowInternal(owner, title, message, confirm: false);
    public static Task<bool> Confirm(Window? owner, string title, string message) => ShowInternal(owner, title, message, confirm: true);

    private static async Task<bool> ShowInternal(Window? owner, string title, string message, bool confirm)
    {
        var result = false;
        var window = new Window
        {
            Title = title,
            Width = 460, Height = 250,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brush("#EFF6F0"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, FontSize = 14, Foreground = Brush("#A3B4B6"), TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Spacing = 8 };
        if (confirm)
        {
            var no = Button("No", () => { result = false; window.Close(); }); no.IsCancel = true;
            var yes = Button("Yes", () => { result = true; window.Close(); }, true); yes.IsDefault = true;
            actions.Children.Add(no); actions.Children.Add(yes);
        }
        else
        {
            var ok = Button("OK", () => window.Close(), true); ok.IsDefault = true;
            actions.Children.Add(ok);
        }
        panel.Children.Add(actions);
        window.Content = panel;
        if (owner != null) await window.ShowDialog(owner);
        else await window.ShowDialog(window);
        return result;
    }
}
