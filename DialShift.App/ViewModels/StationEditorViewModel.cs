using Avalonia.Media.Imaging;
using DialShift.App.Services;
using DialShift.Core;
using DialShift.Core.Catalog;

namespace DialShift.App.ViewModels;

/// <summary>
/// Add/edit station dialog (BHV-52) with delete confirmation (BHV-53). In Add mode it also searches the station catalog
/// (brief 3, docs/catalog-contracts.md §5): a debounced search with five filters, a results list, a detail pane, and a pick
/// that fills the three fields. Edit mode is the plain form and never loads the catalog (D62).
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Every property changes on the UI thread. The catalog load, each search and each logo run on the
/// thread pool; their results come back through <see cref="IUiDispatcher"/>. A search result is applied only while its
/// generation is still the latest (D72), so a slow search never overwrites a newer one, and nothing is applied after
/// <see cref="EditorViewModel.CloseRequested"/>.</para>
/// </remarks>
public sealed class StationEditorViewModel : EditorViewModel
{
    public static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(200);

    public const int NameMaxLength = 100;
    public const int TagMaxLength = 160;
    public const int UrlMaxLength = 2048;

    public const string NameLabel = "Station name";
    public const string TagLabel = "Description / genre";
    public const string UrlLabel = "Stream URL · https://…";

    private static readonly IReadOnlyList<CatalogFilterOption> AllOnly = [CatalogFilterOption.All];

    private readonly Settings settings;
    private readonly IDialogService dialogs;
    private readonly Action<Exception> onError;
    private readonly ICatalogLogoLoader logos;
    private readonly IUiDispatcher dispatcher;
    private readonly TimeSpan searchDelay;
    private readonly LatestValueDispatcher<SearchOutcome> searchResults;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock pushGate = new();

    private string name;
    private string tag;
    private string url;

    private StationCatalogIndex index = StationCatalogIndex.Empty;
    private bool isCatalogLoading;
    private bool isCatalogAvailable;
    private string catalogStatusText = "";
    private string searchText = "";
    private IReadOnlyList<CatalogFilterOption> countryOptions = AllOnly;
    private IReadOnlyList<CatalogFilterOption> cityOptions = AllOnly;
    private IReadOnlyList<CatalogFilterOption> typeOptions = AllOnly;
    private IReadOnlyList<CatalogFilterOption> genreOptions = AllOnly;
    private IReadOnlyList<CatalogFilterOption> languageOptions = AllOnly;
    private CatalogFilterOption selectedCountry = CatalogFilterOption.All;
    private CatalogFilterOption selectedCity = CatalogFilterOption.All;
    private CatalogFilterOption selectedType = CatalogFilterOption.All;
    private CatalogFilterOption selectedGenre = CatalogFilterOption.All;
    private CatalogFilterOption selectedLanguage = CatalogFilterOption.All;
    private IReadOnlyList<CatalogResultRow> results = [];
    private int totalCount;
    private string totalCountText = "";
    private bool hasNoMatches;
    private bool isResultsOpen;
    private CatalogResultRow? highlighted;
    private StationCatalogEntry? selectedEntry;
    private CatalogResultRow? selectedRow;
    private CatalogResultRow? detailRow;
    private Bitmap? detailLogo;

    private bool closed;
    private int searchGeneration;
    private int lastPushedGeneration;
    private CancellationTokenSource? searchCts;
    private CancellationTokenSource? rowLogosCts;
    private CancellationTokenSource? detailLogoCts;
    private TaskCompletionSource? pendingSearch;

    public StationEditorViewModel(Settings settings, Station? original, IDialogService dialogs, Action<Exception> onError,
        ICatalogProvider catalog, ICatalogLogoLoader logos, IUiDispatcher dispatcher, TimeSpan searchDelay)
        : base(original == null ? "Add a frequency" : "Edit station",
            "Use the direct audio stream URL from the station's player or website.",
            canDelete: original != null, onError)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(searchDelay, TimeSpan.Zero);
        this.settings = settings;
        this.dialogs = dialogs;
        this.onError = onError;
        this.logos = logos;
        this.dispatcher = dispatcher;
        this.searchDelay = searchDelay;
        Original = original;
        name = original?.Name ?? "";
        tag = original?.Tag ?? "";
        url = original?.Url ?? "";
        ClearFiltersCommand = new RelayCommand(ClearSearchAndFilters);
        SelectEntryCommand = new RelayCommand(SelectHighlighted);
        searchResults = new LatestValueDispatcher<SearchOutcome>(dispatcher, ApplySearch);

        if (original != null) return; // D62: editing never loads the catalog.
        CloseRequested += (_, _) => Shutdown();
        isCatalogLoading = true;
        catalogStatusText = UiText.CatalogLoading;
        pendingSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = LoadCatalogAsync(catalog);
    }

    public Station? Original { get; }

    public override string DeleteLabel => "Delete station";

    public string Hint => "MP3, AAC and HLS streams are supported. A webpage URL usually won't play. Station icons use the first letter of their name.";

    public string Name { get => name; set => Edit(ref name, value ?? ""); }

    public string Tag { get => tag; set => Edit(ref tag, value ?? ""); }

    public string Url { get => url; set => Edit(ref url, value ?? ""); }

    // ─── Station catalog (Add mode) ───

    /// <summary>Adding a station: the catalog panel exists only then.</summary>
    public bool IsAddMode => Original == null;

    /// <summary>Add mode, until the catalog load completes.</summary>
    public bool IsCatalogLoading { get => isCatalogLoading; private set => SetProperty(ref isCatalogLoading, value); }

    /// <summary>The catalog loaded; search, filters and Clear work. False while loading and in degraded mode.</summary>
    public bool IsCatalogAvailable { get => isCatalogAvailable; private set => SetProperty(ref isCatalogAvailable, value); }

    /// <summary>Loading, unavailable, or "8,274 stations · catalog updated 2026-09-25" (§5.3).</summary>
    public string CatalogStatusText
    {
        get => catalogStatusText;
        private set
        {
            if (SetProperty(ref catalogStatusText, value)) OnPropertyChanged(nameof(StatusLineText));
        }
    }

    /// <summary>
    /// The status line under the filters (D87): the no-match message while a search matches nothing in a non-empty catalog,
    /// else <see cref="CatalogStatusText"/> (so an empty catalog keeps "0 stations"). The results overlay stays closed on no
    /// match, so the manual form the message points to stays in view.
    /// </summary>
    public string StatusLineText => HasNoMatches && index.Entries.Count > 0 ? UiText.CatalogNoMatch : CatalogStatusText;

    /// <summary>The search text; a change searches after the debounce delay (D72). Typed during the load, it is searched when the load completes.</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value ?? "")) ScheduleSearch(searchDelay, open: true);
        }
    }

    public IReadOnlyList<CatalogFilterOption> CountryOptions { get => countryOptions; private set => SetProperty(ref countryOptions, value); }
    public IReadOnlyList<CatalogFilterOption> CityOptions { get => cityOptions; private set => SetProperty(ref cityOptions, value); }
    public IReadOnlyList<CatalogFilterOption> TypeOptions { get => typeOptions; private set => SetProperty(ref typeOptions, value); }
    public IReadOnlyList<CatalogFilterOption> GenreOptions { get => genreOptions; private set => SetProperty(ref genreOptions, value); }
    public IReadOnlyList<CatalogFilterOption> LanguageOptions { get => languageOptions; private set => SetProperty(ref languageOptions, value); }

    /// <summary>Never null: assigning null (a picker losing its items) selects <see cref="CatalogFilterOption.All"/>.</summary>
    public CatalogFilterOption SelectedCountry { get => selectedCountry; set => SetFilter(ref selectedCountry, value); }
    public CatalogFilterOption SelectedCity { get => selectedCity; set => SetFilter(ref selectedCity, value); }
    public CatalogFilterOption SelectedType { get => selectedType; set => SetFilter(ref selectedType, value); }
    public CatalogFilterOption SelectedGenre { get => selectedGenre; set => SetFilter(ref selectedGenre, value); }
    public CatalogFilterOption SelectedLanguage { get => selectedLanguage; set => SetFilter(ref selectedLanguage, value); }

    /// <summary>Empties the search, sets every filter to All, and searches once.</summary>
    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>At most <see cref="StationCatalogQuery.DefaultCap"/> rows, in rank order.</summary>
    public IReadOnlyList<CatalogResultRow> Results { get => results; private set => SetProperty(ref results, value); }

    public int TotalCount { get => totalCount; private set => SetProperty(ref totalCount, value); }

    /// <summary>The results footer (§5.3, D85): "Top 50 of 8,274 stations by votes" unfiltered, else "Showing 50 of 214 matches".</summary>
    public string TotalCountText { get => totalCountText; private set => SetProperty(ref totalCountText, value); }

    /// <summary>The catalog is available, a search was applied, and nothing matched (the overlay is then closed, D87).</summary>
    public bool HasNoMatches
    {
        get => hasNoMatches;
        private set
        {
            if (SetProperty(ref hasNoMatches, value)) OnPropertyChanged(nameof(StatusLineText));
        }
    }

    /// <summary>
    /// The results overlay is showing. A search or filter change that matches something opens it; one that matches nothing,
    /// or a pick, closes it (D87); the view may open or close it.
    /// Opening it highlights the first row when nothing is highlighted, so typing then Enter picks the top match; closing
    /// it drops the highlight, so the detail pane goes back to the picked station and Enter saves (D85).
    /// </summary>
    public bool IsResultsOpen
    {
        get => isResultsOpen;
        set
        {
            if (!SetProperty(ref isResultsOpen, value)) return;
            if (!value) HighlightedResult = null;
            else if (highlighted == null && results.Count > 0) HighlightedResult = results[0];
        }
    }

    /// <summary>The row under the keyboard highlight or the pointer; the list selection.</summary>
    public CatalogResultRow? HighlightedResult
    {
        get => highlighted;
        set
        {
            if (SetProperty(ref highlighted, value)) UpdateDetail();
        }
    }

    /// <summary>Picks <see cref="HighlightedResult"/>: fills Name, Description and Stream URL. Does nothing without a highlight.</summary>
    public RelayCommand SelectEntryCommand { get; }

    /// <summary>The last picked entry; its notes are saved with the station while its URL is kept (D73).</summary>
    public StationCatalogEntry? SelectedEntry { get => selectedEntry; private set => SetProperty(ref selectedEntry, value); }

    /// <summary>What the detail pane shows: the highlighted row, else the picked one.</summary>
    public CatalogResultRow? DetailRow => detailRow;

    /// <summary>The detail row's logo; null shows its monogram.</summary>
    public Bitmap? DetailLogo { get => detailLogo; private set => SetProperty(ref detailLogo, value); }

    /// <summary>Test seam: completes when the latest scheduled search has been applied (or the catalog turned out
    /// unavailable, or the dialog closed). In Add mode it is pending from construction until the first search lands.</summary>
    internal Task PendingSearch => pendingSearch?.Task ?? Task.CompletedTask;

    /// <summary>Walks the results (§5.5): from no highlight, down goes to the first row and up stays; otherwise it moves and
    /// stops at the first and last row. Results the user asked for already open on the first row (D85).</summary>
    public void MoveHighlight(int delta)
    {
        if (results.Count == 0 || delta == 0) return;
        var current = highlighted == null ? -1 : IndexOf(results, highlighted);
        if (current < 0)
        {
            if (delta > 0) HighlightedResult = results[0];
            return;
        }
        HighlightedResult = results[Math.Clamp(current + delta, 0, results.Count - 1)];
    }

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
        if (Original == null)
        {
            // D73: the notes describe the picked stream, so they stay only while the saved URL is still that stream.
            station.Notes = SelectedEntry is { Notes.Length: > 0 } entry && string.Equals(urlText, entry.StreamUrl, StringComparison.Ordinal)
                ? entry.Notes
                : null;
            settings.Stations.Add(station);
        }
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

    // ─── catalog load ───

    private async Task LoadCatalogAsync(ICatalogProvider catalog)
    {
        LoadedCatalog loaded;
        // Read once, before the first await: the source is disposed when the dialog closes.
        var token = lifetime.Token;
        try
        {
            var result = await catalog.GetCatalogAsync(token).ConfigureAwait(false);
            // The filter lists sort thousands of values: built here, off the UI thread.
            loaded = result.State == CatalogLoadState.Loaded
                ? await Task.Run(() => LoadedCatalog.From(result), token).ConfigureAwait(false)
                : new LoadedCatalog(result, null);
        }
        catch (OperationCanceledException)
        {
            return; // the dialog closed first
        }
        catch (Exception ex)
        {
            // The provider never faults by contract; if it did, the dialog still works without the catalog.
            loaded = new LoadedCatalog(CatalogLoadResult.Unavailable(ex.Message), null);
        }
        dispatcher.Post(() => ApplyCatalog(loaded));
    }

    private void ApplyCatalog(LoadedCatalog loaded)
    {
        if (closed) return;
        if (loaded.Options is not { } options)
        {
            IsCatalogLoading = false;
            CatalogStatusText = UiText.CatalogUnavailable;
            pendingSearch?.TrySetResult();
            return;
        }
        // The index's entry count feeds StatusLineText; CatalogStatusText changes below with it and raises that change.
        index = loaded.Result.Catalog;
        CountryOptions = options.Country;
        CityOptions = options.City;
        TypeOptions = options.Type;
        GenreOptions = options.Genre;
        LanguageOptions = options.Language;
        // Available before loading ends, so the search box (enabled while either holds) never blinks off and loses focus.
        IsCatalogAvailable = true;
        IsCatalogLoading = false;
        CatalogStatusText = UiText.CatalogStatus(index.Entries.Count, loaded.Result.GeneratedUtc);
        // The first results are the most-voted stations with the overlay closed, unless the user typed during the load.
        ScheduleSearch(TimeSpan.Zero, open: searchText.Length > 0);
    }

    // ─── search ───

    private void SetFilter(ref CatalogFilterOption field, CatalogFilterOption? value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value ?? CatalogFilterOption.All, propertyName)) ScheduleSearch(TimeSpan.Zero, open: true);
        // A null from a picker that lost its items: tell it the selection is still All.
        else if (value == null) OnPropertyChanged(propertyName);
    }

    private void ClearSearchAndFilters()
    {
        SetProperty(ref searchText, "", nameof(SearchText));
        SetProperty(ref selectedCountry, CatalogFilterOption.All, nameof(SelectedCountry));
        SetProperty(ref selectedCity, CatalogFilterOption.All, nameof(SelectedCity));
        SetProperty(ref selectedType, CatalogFilterOption.All, nameof(SelectedType));
        SetProperty(ref selectedGenre, CatalogFilterOption.All, nameof(SelectedGenre));
        SetProperty(ref selectedLanguage, CatalogFilterOption.All, nameof(SelectedLanguage));
        ScheduleSearch(TimeSpan.Zero, open: true);
    }

    private void ScheduleSearch(TimeSpan delay, bool open)
    {
        if (!isCatalogAvailable || closed) return;
        var generation = ++searchGeneration;
        CancelAndDispose(ref searchCts);
        var cts = searchCts = new CancellationTokenSource();
        if (pendingSearch == null || pendingSearch.Task.IsCompleted)
            pendingSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filters = new CatalogFilters(selectedCountry.Value, selectedCity.Value, selectedType.Value, selectedGenre.Value, selectedLanguage.Value);
        _ = RunSearchAsync(new SearchRequest(generation, index, searchText, filters, delay, open), cts.Token);
    }

    private async Task RunSearchAsync(SearchRequest request, CancellationToken token)
    {
        try
        {
            var result = await Task.Run(async () =>
            {
                if (request.Delay > TimeSpan.Zero) await Task.Delay(request.Delay, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return StationCatalogQuery.Search(request.Catalog, request.Text, request.Filters);
            }, token).ConfigureAwait(false);
            lock (pushGate)
            {
                // An older search that finished late must not replace a newer result still waiting for the UI thread.
                if (request.Generation <= lastPushedGeneration) return;
                lastPushedGeneration = request.Generation;
                searchResults.Push(new SearchOutcome(request, result));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed: the newer search, or the close, settles PendingSearch.
        }
        catch (Exception ex)
        {
            // Never faults the UI: the failure is reported like any failed command, and manual entry keeps working.
            dispatcher.Post(() => FailSearch(request.Generation, ex));
        }
    }

    private void FailSearch(int generation, Exception error)
    {
        if (closed || generation != searchGeneration) return;
        pendingSearch?.TrySetResult();
        onError(error);
    }

    private void ApplySearch(SearchOutcome outcome)
    {
        if (closed || outcome.Request.Generation != searchGeneration) return;
        var rows = outcome.Result.Items.Select(e => new CatalogResultRow(e)).ToList();
        Results = rows;
        TotalCount = outcome.Result.TotalCount;
        var browsing = string.IsNullOrWhiteSpace(outcome.Request.Text) && outcome.Request.Filters == CatalogFilters.None;
        TotalCountText = browsing
            ? UiText.BrowseCount(rows.Count, outcome.Result.TotalCount)
            : UiText.ResultCount(rows.Count, outcome.Result.TotalCount);
        HasNoMatches = outcome.Result.TotalCount == 0;
        // D87: no match closes the overlay, so the form the status line's message points to stays in view.
        if (outcome.Result.TotalCount == 0) IsResultsOpen = false;
        else if (outcome.Request.Open) IsResultsOpen = true;
        // D85: new rows in an open overlay highlight the top match again, so typing then Enter picks it; closed, none.
        HighlightedResult = isResultsOpen && rows.Count > 0 ? rows[0] : null;
        LoadRowLogos(rows);
        pendingSearch?.TrySetResult();
    }

    // ─── pick, detail, logos ───

    private void SelectHighlighted()
    {
        if (highlighted is not { } row) return;
        var entry = row.Entry;
        Name = Truncate(entry.Name, NameMaxLength).TrimEnd();
        Tag = Truncate(entry.Tag, TagMaxLength);
        Url = entry.StreamUrl;
        selectedRow = row;
        SelectedEntry = entry;
        IsResultsOpen = false;
        HighlightedResult = null;
    }

    private void UpdateDetail()
    {
        var row = highlighted ?? selectedRow;
        if (ReferenceEquals(row, detailRow)) return;
        detailRow = row;
        OnPropertyChanged(nameof(DetailRow));
        _ = LoadDetailLogoAsync(row);
    }

    private async Task LoadDetailLogoAsync(CatalogResultRow? row)
    {
        CancelAndDispose(ref detailLogoCts);
        DetailLogo = row?.Logo;
        if (closed || row == null || row.Logo != null || row.Entry.Logo.Length == 0) return;
        var cts = detailLogoCts = new CancellationTokenSource();
        if (await logos.LoadAsync(row.Entry.Logo, cts.Token).ConfigureAwait(false) is { } logo)
            dispatcher.Post(() => { if (!cts.IsCancellationRequested) DetailLogo = logo; });
    }

    private void LoadRowLogos(IReadOnlyList<CatalogResultRow> rows)
    {
        CancelAndDispose(ref rowLogosCts);
        var cts = rowLogosCts = new CancellationTokenSource();
        foreach (var row in rows)
            if (row.Entry.Logo.Length > 0) _ = LoadRowLogoAsync(row, cts.Token);
    }

    private async Task LoadRowLogoAsync(CatalogResultRow row, CancellationToken token)
    {
        if (await logos.LoadAsync(row.Entry.Logo, token).ConfigureAwait(false) is { } logo)
            dispatcher.Post(() => { if (!token.IsCancellationRequested) row.Logo = logo; });
    }

    /// <summary>The dialog closed: cancel the load wait, the search and the logos; nothing is applied afterwards.</summary>
    private void Shutdown()
    {
        if (closed) return;
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        CancelAndDispose(ref searchCts);
        CancelAndDispose(ref rowLogosCts);
        CancelAndDispose(ref detailLogoCts);
        pendingSearch?.TrySetResult();
    }

    /// <summary>
    /// Cancels and disposes <paramref name="cts"/> (UI thread) and clears the field, so it is never cancelled twice. The
    /// work still holding its token only reads it: a cancelled token's state stays readable after the dispose, and a late
    /// registration on it runs at once, so cancelling first makes the dispose safe.
    /// </summary>
    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts is not { } source) return;
        cts = null;
        source.Cancel();
        source.Dispose();
    }

    /// <summary>The first <paramref name="max"/> characters, without splitting a surrogate pair.</summary>
    private static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        return value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
    }

    private static int IndexOf(IReadOnlyList<CatalogResultRow> rows, CatalogResultRow row)
    {
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], row)) return i;
        return -1;
    }

    private sealed record SearchRequest(int Generation, StationCatalogIndex Catalog, string Text, CatalogFilters Filters, TimeSpan Delay, bool Open);

    private sealed record SearchOutcome(SearchRequest Request, CatalogSearchResult Result);

    /// <summary>A load result, with the filter lists when it loaded.</summary>
    private sealed record LoadedCatalog(CatalogLoadResult Result, FilterLists? Options)
    {
        public static LoadedCatalog From(CatalogLoadResult result)
        {
            var entries = result.Catalog.Entries;
            return new(result, new FilterLists(Options(CatalogField.Country), Options(CatalogField.City), Options(CatalogField.Type),
                Options(CatalogField.Genre), Options(CatalogField.Language)));

            // Flat and data-driven (D72): "All", then every distinct value of the whole catalog.
            IReadOnlyList<CatalogFilterOption> Options(CatalogField field) =>
                [CatalogFilterOption.All, .. StationCatalogQuery.AvailableValues(entries, field).Select(v => new CatalogFilterOption(v.Value, v.Label))];
        }
    }

    private sealed record FilterLists(
        IReadOnlyList<CatalogFilterOption> Country,
        IReadOnlyList<CatalogFilterOption> City,
        IReadOnlyList<CatalogFilterOption> Type,
        IReadOnlyList<CatalogFilterOption> Genre,
        IReadOnlyList<CatalogFilterOption> Language);
}
