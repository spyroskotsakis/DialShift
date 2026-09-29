using Avalonia.Media.Imaging;
using DialShift.Core.Catalog;

namespace DialShift.App.ViewModels;

/// <summary>
/// One catalog station as the Add dialog shows it: a result row and the detail pane (docs/catalog-contracts.md §5.2).
/// Every text is computed once from the entry; only <see cref="Logo"/> changes, when its remote image arrives, and the
/// quick-play state (<see cref="IsPreviewing"/>, <see cref="ShowPreview"/>), which <see cref="StationEditorViewModel"/> drives.
/// </summary>
public sealed class CatalogResultRow : ObservableObject
{
    private Bitmap? logo;
    private bool isPreviewing;
    private bool showPreview;

    /// <param name="entry">The catalog entry the row shows.</param>
    /// <param name="togglePreview">The Add dialog's quick-play toggle (Feature 2), which previews this row's stream or stops
    /// it; a row built without one (a test, a preview) gets a command that does nothing.</param>
    /// <param name="onError">Where a failed toggle is reported, like any command failure; only used with a toggle.</param>
    public CatalogResultRow(StationCatalogEntry entry, Func<CatalogResultRow, Task>? togglePreview = null, Action<Exception>? onError = null)
    {
        Entry = entry;
        PreviewCommand = new AsyncRelayCommand(
            () => togglePreview is { } toggle ? toggle(this) : Task.CompletedTask,
            ex => onError?.Invoke(ex));
        Name = entry.Name;
        Monogram = UiText.Initial(entry.Name);
        FrequencyText = UiText.FrequencyText(entry.FrequencyFm);
        var country = entry.CountryLabel.Length > 0 ? entry.CountryLabel : entry.Country;
        Subtitle = Join(" · ", entry.City, FrequencyText, country);
        Place = Join(" · ", entry.City, country);
        Kind = Join(" · ", entry.Type, string.Equals(entry.Genre, entry.Type, StringComparison.Ordinal) ? "" : entry.Genre);
        Location = string.Join(", ", new[] { entry.City, entry.Region, country }.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal));
        LanguageText = entry.Language;
        VotesText = entry.Votes switch
        {
            null => "",
            1 => "1 vote",
            { } votes => UiText.Count(votes) + " votes"
        };
        Notes = entry.Notes;
        AutomationName = $"{Name}, {Subtitle}" + (HasVpn ? $", {VpnText}" : "");
    }

    public StationCatalogEntry Entry { get; }

    public string Name { get; }

    /// <summary>Shown in the logo's place until (or unless) the logo loads.</summary>
    public string Monogram { get; }

    /// <summary>"City · 101.5 FM · Country", empty parts left out: the spoken row (<see cref="AutomationName"/>).</summary>
    public string Subtitle { get; }

    /// <summary>"City · Country", empty parts left out: the row's title after the name; the frequency has its own column.</summary>
    public string Place { get; }

    /// <summary>"Type · Genre", empty parts left out; the genre is left out when it repeats the type.</summary>
    public string Kind { get; }

    /// <summary>"City, Region, Country": the non-empty, distinct parts.</summary>
    public string Location { get; }

    public string FrequencyText { get; }

    public string LanguageText { get; }

    public string VotesText { get; }

    /// <summary>The full notes; the view wraps them.</summary>
    public string Notes { get; }

    /// <summary>The picked stream needs a VPN; the row and detail pane show the badge when it does. The pipeline guarantees
    /// <see cref="StationCatalogEntry.VpnRegion"/> is non-empty exactly then, so this reads the one that always has text.</summary>
    public bool HasVpn => Entry.VpnRegion.Length > 0;

    /// <summary>"VPN · United Kingdom" (<see cref="UiText.VpnText"/>), empty without a region.</summary>
    public string VpnText => UiText.VpnText(Entry.VpnRegion);

    public string AutomationName { get; }

    /// <summary>The remote logo once loaded; null shows <see cref="Monogram"/>.</summary>
    public Bitmap? Logo { get => logo; set => SetProperty(ref logo, value); }

    // ─── quick play (Feature 2 of the Add dialog) ───

    /// <summary>Previews this row's stream, or stops it when it is the one playing (see <see cref="IsPreviewing"/>).</summary>
    public AsyncRelayCommand PreviewCommand { get; }

    /// <summary>This row's stream is the one being previewed: the row and the detail pane show the stop icon.</summary>
    public bool IsPreviewing
    {
        get => isPreviewing;
        set
        {
            if (!SetProperty(ref isPreviewing, value)) return;
            OnPropertyChanged(nameof(ShowPlayIcon));
            OnPropertyChanged(nameof(PreviewAutomationName));
        }
    }

    /// <summary>The play icon while the stream is not the one playing, the stop icon while it is.</summary>
    public bool ShowPlayIcon => !isPreviewing;

    /// <summary>The quick-play button's spoken name, "Listen &lt;name&gt;" or "Stop &lt;name&gt;" (the row and the detail pane).</summary>
    public string PreviewAutomationName => (isPreviewing ? "Stop " : "Listen ") + Name;

    /// <summary>The row's quick-play button is shown: the pointer or the keyboard is on the row (the row is highlighted), or
    /// the row is the one previewing, so its stop stays reachable. The detail pane's button is shown whenever it has a row.</summary>
    public bool ShowPreview { get => showPreview; set => SetProperty(ref showPreview, value); }

    private static string Join(string separator, params string[] parts) => string.Join(separator, parts.Where(p => p.Length > 0));
}
