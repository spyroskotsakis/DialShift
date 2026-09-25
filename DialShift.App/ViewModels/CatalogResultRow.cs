using System.Globalization;
using Avalonia.Media.Imaging;
using DialShift.Core.Catalog;

namespace DialShift.App.ViewModels;

/// <summary>
/// One catalog station as the Add dialog shows it: a result row and the detail pane (docs/catalog-contracts.md §5.2).
/// Every text is computed once from the entry; only <see cref="Logo"/> changes, when its remote image arrives.
/// </summary>
public sealed class CatalogResultRow : ObservableObject
{
    private Bitmap? logo;

    public CatalogResultRow(StationCatalogEntry entry)
    {
        Entry = entry;
        Name = entry.Name;
        Monogram = UiText.Initial(entry.Name);
        FrequencyText = UiText.FrequencyText(entry.FrequencyFm);
        var country = entry.CountryLabel.Length > 0 ? entry.CountryLabel : entry.Country;
        Subtitle = Join(" · ", entry.City, FrequencyText, country);
        Kind = Join(" · ", entry.Type, string.Equals(entry.Genre, entry.Type, StringComparison.Ordinal) ? "" : entry.Genre);
        Location = string.Join(", ", new[] { entry.City, entry.Region, country }.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal));
        LanguageText = entry.Language;
        VotesText = entry.Votes switch
        {
            null => "",
            1 => "1 vote",
            { } votes => votes.ToString(CultureInfo.InvariantCulture) + " votes"
        };
        Notes = entry.Notes;
        AutomationName = $"{Name}, {Subtitle}";
    }

    public StationCatalogEntry Entry { get; }

    public string Name { get; }

    /// <summary>Shown in the logo's place until (or unless) the logo loads.</summary>
    public string Monogram { get; }

    /// <summary>"City · 101.5 FM · Country", empty parts left out.</summary>
    public string Subtitle { get; }

    /// <summary>"Type · Genre", empty parts left out; the genre is left out when it repeats the type.</summary>
    public string Kind { get; }

    /// <summary>"City, Region, Country": the non-empty, distinct parts.</summary>
    public string Location { get; }

    public string FrequencyText { get; }

    public string LanguageText { get; }

    public string VotesText { get; }

    /// <summary>The full notes; the view wraps them.</summary>
    public string Notes { get; }

    public string AutomationName { get; }

    /// <summary>The remote logo once loaded; null shows <see cref="Monogram"/>.</summary>
    public Bitmap? Logo { get => logo; set => SetProperty(ref logo, value); }

    private static string Join(string separator, params string[] parts) => string.Join(separator, parts.Where(p => p.Length > 0));
}
