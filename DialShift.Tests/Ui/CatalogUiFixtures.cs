using DialShift.App.Services;
using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Catalog;
using DialShift.Tests.Fakes;

namespace DialShift.Tests.Ui;

/// <summary>
/// Station catalogs for the Add dialog's CAT checks (docs/catalog-contracts.md §8): a small hand-made catalog whose every
/// text the checks spell out, a numbered one for the result counts, and the real <c>app-catalog.json</c> next to the test
/// binary (CAT-02 puts it there), whose expectations the checks derive from the loaded file instead of spelling them out.
/// Station names and URLs here are fictional (example.org), never the catalog's.
/// </summary>
internal static class CatalogUiFixtures
{
    public static readonly DateTimeOffset Generated = new(2026, 9, 25, 19, 28, 48, TimeSpan.Zero);

    /// <summary>About 560 characters: long enough to need the detail pane's scrolling at 620 wide.</summary>
    public static readonly string KosmosNotes =
        "Public broadcaster's world-music station. " + string.Join(" ", Enumerable.Repeat(
            "Programmes range from Balkan brass and rebetiko to West African highlife, with long evening shows hosted by musicians.", 4))
        + " Streams around the clock.";

    public static readonly StationCatalogEntry Kosmos = new()
    {
        Name = "Kosmos 93.6", NameLocal = "Κόσμος", Country = "GR", CountryLabel = "Greece", City = "Athens", Region = "Attica",
        FrequencyFm = "93.6", Type = "Public", Genre = "World", Language = "Greek", StreamUrl = "https://streams.example.org/kosmos",
        Codec = "MP3", Bitrate = 128, Votes = 4210, Notes = KosmosNotes, Logo = "https://logos.example.org/kosmos.png", Tag = "Public · World"
    };

    public static readonly StationCatalogEntry Melodia = new()
    {
        Name = "Melodia 99.2", Country = "GR", CountryLabel = "Greece", City = "Athens", Region = "Attica", FrequencyFm = "99.2",
        Type = "Commercial", Genre = "Pop", Language = "Greek", StreamUrl = "https://streams.example.org/melodia", Votes = 1, Tag = "Commercial · Pop"
    };

    public static readonly StationCatalogEntry Thessaloniki = new()
    {
        Name = "Radio Thessaloniki", Country = "GR", CountryLabel = "Greece", City = "Thessaloniki", Region = "Central Macedonia",
        FrequencyFm = "94.5", Type = "Commercial", Genre = "Commercial", Language = "Greek", StreamUrl = "https://streams.example.org/thessaloniki",
        Votes = 50, Notes = "A regional station.", Logo = "https://logos.example.org/thessaloniki.png", Tag = "Commercial"
    };

    public static readonly StationCatalogEntry Bayern = new()
    {
        Name = "Bayern 3", Country = "DE", CountryLabel = "Germany", City = "München", Region = "Bavaria", FrequencyFm = "97.3",
        Type = "Public", Genre = "Pop", Language = "German", StreamUrl = "https://streams.example.org/bayern3", Votes = 900, Tag = "Public · Pop"
    };

    public static readonly StationCatalogEntry KolnAm = new()
    {
        Name = "Radio Köln AM", Country = "DE", CountryLabel = "Germany", City = "Köln", FrequencyFm = "1593", Type = "Commercial",
        Genre = "Talk", Language = "German", StreamUrl = "https://streams.example.org/koeln-am", Votes = 20, Notes = "Talk on medium wave.", Tag = "Talk"
    };

    public static readonly StationCatalogEntry Shortwave = new()
    {
        Name = "Radio Shortwave", Country = "DE", CountryLabel = "Germany", FrequencyFm = "Shortwave", Type = "Public", Genre = "News",
        StreamUrl = "https://streams.example.org/shortwave", Tag = "News"
    };

    public static readonly StationCatalogEntry Chill = new()
    {
        Name = "chill collection", Country = "XX", CountryLabel = "Internet (collections)", Genre = "Chillout", InternetOnly = true,
        StreamUrl = "https://streams.example.org/chill", Votes = 7, Tag = "Chillout"
    };

    /// <summary>Seven stations over two countries and a collection; empty search order (votes): Kosmos, Bayern, Thessaloniki, Köln, chill, Melodia, Shortwave.</summary>
    public static IReadOnlyList<StationCatalogEntry> Small { get; } = [Kosmos, Melodia, Thessaloniki, Bayern, KolnAm, Shortwave, Chill];

    /// <summary>"Station 01" … "Station nn", votes descending with the number, so the empty search lists them in order.</summary>
    public static IReadOnlyList<StationCatalogEntry> Numbered(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new StationCatalogEntry
        {
            Name = $"Station {i:00}", Country = "GR", CountryLabel = "Greece", StreamUrl = $"https://streams.example.org/station-{i:00}",
            Votes = 1000 - i, Tag = "Numbered"
        })];

    /// <summary>A loaded catalog of <paramref name="entries"/>, generated at <see cref="Generated"/>.</summary>
    public static CatalogLoadResult Loaded(IReadOnlyList<StationCatalogEntry> entries) => Loaded(entries, Generated);

    /// <summary>A loaded catalog of <paramref name="entries"/>; <paramref name="generated"/> null is a file without generated_utc.</summary>
    public static CatalogLoadResult Loaded(IReadOnlyList<StationCatalogEntry> entries, DateTimeOffset? generated) =>
        new(CatalogLoadState.Loaded, new StationCatalogIndex(entries), generated, null);

    private static readonly Lazy<Task<CatalogLoadResult>> real = new(() =>
        new CatalogProvider(CatalogProvider.ResolveLocation(_ => null, AppContext.BaseDirectory), new RecordingAppLog()).GetCatalogAsync());

    /// <summary>The real catalog next to the test binary, loaded once per process (DIALSHIFT_CATALOG_PATH ignored).</summary>
    public static Task<CatalogLoadResult> RealAsync() => real.Value;

    /// <summary>What the filter pickers must offer for <paramref name="field"/>: All, then the catalog's values (§5.2, D72).</summary>
    public static IReadOnlyList<CatalogFilterOption> ExpectedOptions(IReadOnlyList<StationCatalogEntry> entries, CatalogField field) =>
        [CatalogFilterOption.All, .. StationCatalogQuery.AvailableValues(entries, field).Select(v => new CatalogFilterOption(v.Value, v.Label))];

    /// <summary>A catalog pick as the dialog's code-behind makes it: highlight the row of <paramref name="entry"/>, then Select.</summary>
    public static bool Pick(StationEditorViewModel editor, StationCatalogEntry entry)
    {
        if (editor.Results.FirstOrDefault(r => Equals(r.Entry, entry)) is not { } row) return false;
        editor.HighlightedResult = row;
        editor.SelectEntryCommand.Execute(null);
        return true;
    }

    /// <summary>A station with the given fields, as a saved one.</summary>
    public static Station Saved(string name, string url, string? notes = null) => new() { Name = name, Url = url, Tag = "Saved", Notes = notes };
}
