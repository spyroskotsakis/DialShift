using System.Collections.ObjectModel;
using DialShift.Core;
using DialShift.Core.Playback;

namespace DialShift.App.ViewModels;

/// <summary>One saved station on the Stations page (BHV-51).</summary>
public sealed class StationRowViewModel : ObservableObject
{
    private bool isCurrent;

    public StationRowViewModel(Station station, bool isFallback, Func<Station, Task> listen, Func<Station, Task> edit, Action<Exception> onError)
    {
        Station = station;
        Name = station.Name;
        Initial = UiText.Initial(station.Name);
        Subtitle = station.Tag + (isFallback ? " · Fallback" : "");
        ListenCommand = new AsyncRelayCommand(() => listen(station), onError);
        EditCommand = new AsyncRelayCommand(() => edit(station), onError);
    }

    public Station Station { get; }
    public string Name { get; }
    public string Initial { get; }
    public string Subtitle { get; }
    public string ListenAutomationName => "Listen to " + Name;
    public string EditAutomationName => "Edit " + Name;

    /// <summary>The station on air (or being opened) right now; the row is highlighted.</summary>
    public bool IsCurrent { get => isCurrent; set => SetProperty(ref isCurrent, value); }

    public AsyncRelayCommand ListenCommand { get; }
    public AsyncRelayCommand EditCommand { get; }
}

/// <summary>The Stations tab: list, Listen, add/edit/delete (BHV-51 to BHV-53).</summary>
public sealed class StationsPageViewModel : PageViewModel
{
    private PlaybackSnapshot snapshot;
    private string countText = "";
    private bool isEmpty;

    public StationsPageViewModel(ViewModelServices services) : base(services)
    {
        snapshot = services.Coordinator.Snapshot;
        AddCommand = new AsyncRelayCommand(() => EditAsync(null), services.ReportError);
        Refresh();
    }

    public override string Title => "Your stations";
    public override string Description => "A few good frequencies. Always within reach.";

    public ObservableCollection<StationRowViewModel> Rows { get; } = [];

    public string CountText { get => countText; private set => SetProperty(ref countText, value); }

    public bool IsEmpty { get => isEmpty; private set => SetProperty(ref isEmpty, value); }

    public string EmptyText => "Start with a station you love. Add its direct MP3, AAC or HLS stream URL above.";

    public string CreditText => "Starter stations by SomaFM. Add your Greek favorites with their direct stream URLs.";

    public AsyncRelayCommand AddCommand { get; }

    public override void Refresh()
    {
        var settings = Services.Settings.Settings;
        Rows.Clear();
        foreach (var station in settings.Stations)
            Rows.Add(new StationRowViewModel(station, settings.FallbackStationId == station.Id, ListenAsync, EditAsync, Services.ReportError));
        CountText = $"{settings.Stations.Count:00}  SAVED FREQUENCIES";
        IsEmpty = settings.Stations.Count == 0;
        MarkCurrent();
    }

    public override void ApplySnapshot(PlaybackSnapshot value)
    {
        snapshot = value;
        MarkCurrent();
    }

    private void MarkCurrent()
    {
        var onAir = snapshot.IsActive ? snapshot.CurrentStationId ?? snapshot.DesiredStationId : null;
        foreach (var row in Rows) row.IsCurrent = row.Station.Id == onAir;
    }

    private async Task ListenAsync(Station station)
    {
        await Services.Coordinator.PlayAsync(station.Id);
        await Services.Settings.SaveAsync();
    }

    private async Task EditAsync(Station? station)
    {
        var editor = new StationEditorViewModel(Services.Settings.Settings, station, Services.Dialogs, Services.ReportError,
            Services.Catalog, Services.Logos, Services.Dispatcher, Services.CatalogSearchDelay);
        switch (await Services.Editors.ShowStationEditorAsync(editor))
        {
            case EditorResult.Saved:
                await Services.Settings.CommitAsync(SettingsChange.Stations);
                break;
            case EditorResult.Deleted when station != null:
                await DeleteAsync(station);
                break;
        }
    }

    /// <summary>BHV-53: stop playback of it first, then remove the station, its slots and every reference, then replay the schedule.</summary>
    private async Task DeleteAsync(Station station)
    {
        await Services.Coordinator.ForgetStationAsync(station.Id);
        var settings = Services.Settings.Settings;
        settings.Stations.Remove(station);
        settings.Schedule.RemoveAll(e => e.StationId == station.Id);
        if (settings.FallbackStationId == station.Id) settings.FallbackStationId = null;
        if (settings.LastStationId == station.Id) settings.LastStationId = null;
        await Services.Settings.CommitAsync(SettingsChange.Stations | SettingsChange.Schedule);
    }
}
