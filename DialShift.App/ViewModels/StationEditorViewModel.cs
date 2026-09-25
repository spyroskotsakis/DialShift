using DialShift.App.Services;
using DialShift.Core;

namespace DialShift.App.ViewModels;

/// <summary>Add/edit station dialog (BHV-52) with delete confirmation (BHV-53).</summary>
public sealed class StationEditorViewModel : EditorViewModel
{
    public const int NameMaxLength = 100;
    public const int TagMaxLength = 160;
    public const int UrlMaxLength = 2048;

    public const string NameLabel = "Station name";
    public const string TagLabel = "Description / genre";
    public const string UrlLabel = "Stream URL · https://…";

    private readonly Settings settings;
    private readonly IDialogService dialogs;
    private string name;
    private string tag;
    private string url;

    public StationEditorViewModel(Settings settings, Station? original, IDialogService dialogs, Action<Exception> onError)
        : base(original == null ? "Add a frequency" : "Edit station",
            "Use the direct audio stream URL from the station's player or website.",
            canDelete: original != null, onError)
    {
        this.settings = settings;
        this.dialogs = dialogs;
        Original = original;
        name = original?.Name ?? "";
        tag = original?.Tag ?? "";
        url = original?.Url ?? "";
    }

    public Station? Original { get; }


    public override string DeleteLabel => "Delete station";

    public string Hint => "MP3, AAC and HLS streams are supported. A webpage URL usually won't play. Station icons use the first letter of their name.";

    public string Name { get => name; set => Edit(ref name, value ?? ""); }

    public string Tag { get => tag; set => Edit(ref tag, value ?? ""); }

    public string Url { get => url; set => Edit(ref url, value ?? ""); }

    protected override void Save()
    {
        var nameText = Name.Trim();
        var tagText = Tag.Trim();
        var urlText = Url.Trim();
        if (string.IsNullOrWhiteSpace(nameText)) { Fail("Give this station a name.", nameof(Name)); return; }
        if (!SettingsStore.ValidUrl(urlText)) { Fail("Enter a valid HTTP or HTTPS stream URL.", nameof(Url)); return; }

        var station = Original ?? new Station();
        station.Name = nameText;
        station.Tag = string.IsNullOrWhiteSpace(tagText) ? "Internet radio" : tagText;
        station.Url = urlText;
        if (Original == null) settings.Stations.Add(station);
        Error = null;
        Close(EditorResult.Saved);
    }

    protected override async Task DeleteAsync()
    {
        if (Original is not { } station) return;
        var slots = settings.Schedule.Count(e => e.StationId == station.Id);
        if (await dialogs.ConfirmAsync("Delete station", UiText.DeleteStationQuestion(station, slots), "Delete", "Cancel"))
            Close(EditorResult.Deleted);
    }
}
