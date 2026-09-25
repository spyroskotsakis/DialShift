using System;
using System.Linq;
using DialShift.Core;
using static DialShift.App.Views.MainWindow;

namespace DialShift.App.Views.Dialogs;

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
