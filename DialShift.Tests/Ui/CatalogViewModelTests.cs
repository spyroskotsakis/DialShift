using DialShift.App.Services;
using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Catalog;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.CatalogUiFixtures;

namespace DialShift.Tests.Ui;

/// <summary>
/// The Add dialog's station catalog in the view model (brief 3, docs/catalog-contracts.md §5, §8): CAT-04's UI half (a load
/// that never completes), CAT-07's label half, CAT-08's options and filters, CAT-09's count texts, CAT-10's pick, detail
/// and D73 notes, CAT-11 (Edit mode and BHV-52 unchanged), CAT-12's highlight rules, CAT-13's states, CAT-14's debounce
/// (no search on the UI thread, stale results dropped) and CAT-17's status line. Part of the UiViewModels suite.
/// </summary>
/// <remarks>
/// Each editor gets a <see cref="QueuedUiDispatcher"/>: the load and the searches run on the thread pool as in the app, and
/// their results reach the view model only when the check drains the queue, so every check sees a settled state and no
/// view-model change ever races the check's own.
/// </remarks>
internal static class CatalogViewModelTests
{
    private static readonly TimeSpan Forever = TimeSpan.FromMinutes(10);

    public static async Task RunAsync()
    {
        StaticTexts();
        FrequencyLabels();
        await LoadingAndDegraded();
        await FirstResultsAndCounts();
        await FiltersAndClear();
        await DebounceAndStaleResults();
        await HighlightRules();
        await PickAndDetail();
        await NotesOnSave();
        await EditModeUnchanged();
        await RealCatalog();
    }

    /// <summary>One editor over a fake catalog, with a queued UI thread.</summary>
    private sealed class EditorRig
    {
        public EditorRig(CatalogLoadResult? result = null, Station? original = null, TimeSpan? delay = null, TaskCompletionSource? hold = null,
            Exception? fault = null, Settings? settings = null)
        {
            Catalog = new FakeCatalogProvider { Hold = hold, Fault = fault };
            if (result != null) Catalog.Result = result;
            Settings = settings ?? new Settings();
            Vm = new StationEditorViewModel(Settings, original, Dialogs, Errors.Add, Catalog, Logos, Ui, delay ?? TimeSpan.Zero);
            Vm.FocusRequested += (_, field) => Focus.Add(field);
            Vm.PropertyChanged += (_, e) => Changes.Add(e.PropertyName);
        }

        public Settings Settings { get; }
        public QueuedUiDispatcher Ui { get; } = new();
        public FakeCatalogProvider Catalog { get; }
        public FakeLogoLoader Logos { get; } = new();
        public RecordingDialogService Dialogs { get; } = new();
        public List<Exception> Errors { get; } = [];
        public List<string> Focus { get; } = [];
        public List<string?> Changes { get; } = [];
        public StationEditorViewModel Vm { get; }

        /// <summary>Drains the UI queue until the latest search (or the load) has been applied.</summary>
        public async Task Settled()
        {
            if (!await Ui.RunUntilAsync(() => Vm.PendingSearch.IsCompleted))
                throw new TimeoutException("The editor's pending search did not settle within 10 s.");
            Ui.Drain();
        }

        public int ChangesOf(string property) => Changes.Count(p => p == property);

        public IReadOnlyList<StationCatalogEntry> Shown => [.. Vm.Results.Select(r => r.Entry)];

        public CatalogSearchResult Expected(string text, CatalogFilters? filters = null) =>
            StationCatalogQuery.Search(Catalog.Result.Catalog, text, filters ?? CatalogFilters.None);
    }

    // ─── §5.3 / §5.4 texts ───

    private static void StaticTexts()
    {
        Check("CAT-13 §5.3 texts are exact: placeholder, loading, unavailable, no match, manual-entry separator",
            UiText.SearchPlaceholder == "Search by name, frequency or city…" && UiText.CatalogLoading == "Loading the station catalog…"
            && UiText.CatalogUnavailable == "Catalog unavailable — enter stream details manually"
            && UiText.CatalogNoMatch == "No stations match — adjust filters or enter the stream manually"
            && UiText.ManualEntrySeparator == "Or enter stream details manually");
        Check("CAT-09 §5.3 ResultCount (text or a filter): 0 → \"\", 1 of 1 → \"1 match\", 50 of 50 → \"50 matches\", 50 of 214 → \"Showing 50 of 214 matches\"",
            UiText.ResultCount(0, 0) == "" && UiText.ResultCount(1, 1) == "1 match"
            && UiText.ResultCount(50, 50) == "50 matches" && UiText.ResultCount(50, 214) == "Showing 50 of 214 matches"
            && UiText.ResultCount(1, 2) == "Showing 1 of 2 matches");
        Check("CAT-09 D85 BrowseCount (no text, every filter All): 50 of 8274 → \"Top 50 of 8,274 stations by votes\", 7 of 7 → \"All 7 stations by votes\", 1 of 1 → \"1 station\", 0 → \"\"",
            UiText.BrowseCount(50, 8274) == "Top 50 of 8,274 stations by votes" && UiText.BrowseCount(7, 7) == "All 7 stations by votes"
            && UiText.BrowseCount(1, 1) == "1 station" && UiText.BrowseCount(0, 0) == "");
        Check("CAT-17 §5.3 CatalogStatus: \"8,274 stations · catalog updated 2026-09-25\"; without generated_utc \"8,274 stations\"",
            UiText.CatalogStatus(8274, Generated) == "8,274 stations · catalog updated 2026-09-25" && UiText.CatalogStatus(8274, null) == "8,274 stations");
        Check("CAT-17 CatalogStatus is singular for one station: \"1 station\", \"1 station · catalog updated 2026-09-25\"",
            UiText.CatalogStatus(1, null) == "1 station" && UiText.CatalogStatus(1, Generated) == "1 station · catalog updated 2026-09-25");
        Check("CAT-17 the date is generated_utc's UTC date, whatever its offset or the host's zone (23:30 −05:00 → 2026-09-26; 01:00 +03:00 → 2026-09-25)",
            UiText.CatalogStatus(10, new DateTimeOffset(2026, 9, 25, 23, 30, 0, TimeSpan.FromHours(-5))) == "10 stations · catalog updated 2026-09-26"
            && UiText.CatalogStatus(10, new DateTimeOffset(2026, 9, 26, 1, 0, 0, TimeSpan.FromHours(3))) == "10 stations · catalog updated 2026-09-25");
        // D85: grouped the invariant way, whatever the computer's culture (German groups with a dot).
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Check("CAT-17 D85 counts are grouped with the invariant comma under a de-DE culture (12,345 stations; 824,571 votes)",
                UiText.CatalogStatus(12345, null) == "12,345 stations" && UiText.ResultCount(50, 12345) == "Showing 50 of 12,345 matches"
                && new CatalogResultRow(new StationCatalogEntry { Name = "Many votes", Country = "GR", StreamUrl = "https://streams.example.org/many", Votes = 824571 }).VotesText == "824,571 votes");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    private static void FrequencyLabels()
    {
        string[] inputs = ["101.5", "89.0", "108.0", "64", "1017", "150", "8500", "1593", "108.5", "149", "1593.0", "87,5", "1,017", " 101.5", "+101.5", "Shortwave", "63.9", "abc"];
        foreach (var value in inputs)
        {
            var band = StationCatalogQuery.BandOf(value);
            var text = UiText.FrequencyText(value);
            var expected = band switch { FrequencyBand.Fm => value + " FM", FrequencyBand.Kilohertz => value + " kHz", _ => value };
            Check($"CAT-07 §5.4 FrequencyText(\"{value}\") = \"{expected}\" (BandOf {band}): \" FM\" exactly for Fm, \" kHz\" exactly for Kilohertz, the raw value otherwise",
                text == expected && text.EndsWith(" FM", StringComparison.Ordinal) == (band == FrequencyBand.Fm)
                && text.EndsWith(" kHz", StringComparison.Ordinal) == (band == FrequencyBand.Kilohertz));
        }
        Check("CAT-07 §5.4 FrequencyText(\"\") is \"\" (no unit for an unknown frequency)", UiText.FrequencyText("") == "");
        Check("CAT-07 the exact labels: \"101.5 FM\", \"1593 kHz\", \"Shortwave\"",
            UiText.FrequencyText("101.5") == "101.5 FM" && UiText.FrequencyText("1593") == "1593 kHz" && UiText.FrequencyText("Shortwave") == "Shortwave");

        var rows = Small.Select(e => new CatalogResultRow(e)).ToList();
        Check("CAT-07 a result row's frequency label and subtitle follow BandOf: \"93.6 FM\", \"1593 kHz\", raw \"Shortwave\", none for an empty frequency",
            rows.Single(r => r.Entry == Kosmos).FrequencyText == "93.6 FM" && rows.Single(r => r.Entry == KolnAm).FrequencyText == "1593 kHz"
            && rows.Single(r => r.Entry == Shortwave).FrequencyText == "Shortwave" && rows.Single(r => r.Entry == Chill).FrequencyText == ""
            && rows.Single(r => r.Entry == KolnAm).Subtitle == "Köln · 1593 kHz · Germany" && rows.Single(r => r.Entry == Shortwave).Subtitle == "Shortwave · Germany"
            && rows.Single(r => r.Entry == Chill).Subtitle == "Internet (collections)");
    }

    // ─── CAT-13 / CAT-04: loading, unavailable, a faulting provider ───

    private static async Task LoadingAndDegraded()
    {
        // A load that never completes (CAT-04's UI half): the form keeps working.
        var never = new EditorRig(Loaded(Small), hold: new TaskCompletionSource());
        var vm = never.Vm;
        never.Ui.Drain();
        Check("CAT-13 loading: IsCatalogLoading, not available, status \"Loading the station catalog…\", every filter offers only All, no results",
            vm.IsAddMode && vm.IsCatalogLoading && !vm.IsCatalogAvailable && vm.CatalogStatusText == UiText.CatalogLoading
            && new[] { vm.CountryOptions, vm.CityOptions, vm.TypeOptions, vm.GenreOptions, vm.LanguageOptions }.All(o => o.SequenceEqual([CatalogFilterOption.All]))
            && vm.Results.Count == 0 && !vm.HasNoMatches && !vm.IsResultsOpen && vm.TotalCountText == "" && !vm.PendingSearch.IsCompleted);
        vm.SearchText = "kos";
        await Task.Delay(20);
        never.Ui.Drain();
        Check("CAT-13 typing while loading is kept and searches nothing yet", vm.SearchText == "kos" && vm.Results.Count == 0 && !vm.IsResultsOpen);
        vm.Name = "Hand-entered";
        vm.Url = "https://radio.example.org/hand";
        vm.SaveCommand.Execute(null);
        var added = never.Settings.Stations.SingleOrDefault();
        Check("CAT-04 CAT-13 a load that never completes leaves manual Save working: saved, no notes",
            vm.Result == EditorResult.Saved && added is { Name: "Hand-entered", Url: "https://radio.example.org/hand", Tag: "Internet radio", Notes: null });
        Check("CAT-04 closing the dialog abandons the catalog wait (the provider's token is cancelled), and nothing is applied afterwards",
            never.Catalog.LastToken.IsCancellationRequested && vm.PendingSearch.IsCompleted && never.Ui.Drain() == 0 && vm.IsCatalogLoading && never.Errors.Count == 0);

        // The load completes after the user typed: the typed text is searched and the results open.
        var hold = new TaskCompletionSource();
        var late = new EditorRig(Loaded(Small), hold: hold);
        late.Vm.SearchText = "kos";
        hold.SetResult();
        await late.Settled();
        Check("CAT-13 the text typed during the load is searched when it completes, and the results open",
            late.Vm.IsCatalogAvailable && !late.Vm.IsCatalogLoading && late.Shown.SequenceEqual([Kosmos]) && late.Vm.IsResultsOpen && late.Vm.TotalCountText == "1 match");

        // Unavailable (degraded mode).
        var gone = new EditorRig(CatalogLoadResult.Unavailable("the file is missing."));
        await gone.Settled();
        vm = gone.Vm;
        Check("CAT-13 unavailable: status \"Catalog unavailable — enter stream details manually\", not loading, not available, no \"No stations match\"",
            vm.CatalogStatusText == UiText.CatalogUnavailable && !vm.IsCatalogLoading && !vm.IsCatalogAvailable && !vm.HasNoMatches
            && vm.Results.Count == 0 && !vm.IsResultsOpen && vm.PendingSearch.IsCompleted);
        vm.SearchText = "kos";
        vm.SelectedCountry = new CatalogFilterOption("GR", "Greece");
        vm.ClearFiltersCommand.Execute(null);
        await Task.Delay(20);
        gone.Ui.Drain();
        Check("CAT-13 unavailable: search, filters and Clear search nothing (no results, no overlay, nothing pending)",
            vm.Results.Count == 0 && !vm.IsResultsOpen && vm.PendingSearch.IsCompleted && gone.Ui.Pending == 0);
        vm.SaveCommand.Execute(null);
        Check("CAT-11 CAT-13 unavailable: BHV-52's missing-name message and focus are unchanged",
            vm.Error == "Give this station a name." && gone.Focus[^1] == "Name");
        vm.Name = "Manual";
        vm.Url = "radio.example.org/manual";
        vm.SaveCommand.Execute(null);
        Check("CAT-11 CAT-13 unavailable: BHV-52's bad-URL message and focus are unchanged",
            vm.Error == "Enter a valid HTTP or HTTPS stream URL." && gone.Focus[^1] == "Url");
        vm.Url = "https://radio.example.org/manual";
        vm.SaveCommand.Execute(null);
        Check("CAT-13 unavailable: manual entry saves as before (no notes, no error)",
            vm.Result == EditorResult.Saved && gone.Settings.Stations.Single() is { Name: "Manual", Notes: null } && gone.Errors.Count == 0);

        // A provider that faults (the contract forbids it): degraded mode, no error dialog.
        var faulty = new EditorRig(Loaded(Small), fault: new InvalidOperationException("boom"));
        await faulty.Settled();
        Check("CAT-13 a provider that throws leaves the dialog in degraded mode (unavailable text), never an error",
            faulty.Vm.CatalogStatusText == UiText.CatalogUnavailable && !faulty.Vm.IsCatalogAvailable && faulty.Errors.Count == 0);

        // End to end through the Stations page (the real MainWindowViewModel wiring) with the catalog unavailable.
        await using var rig = UiRig.CreateViewModels();
        rig.Catalog.Result = CatalogLoadResult.Unavailable("the file is missing.");
        rig.Recorder!.StationScripts.Enqueue(editor =>
        {
            editor.Name = "Kosmos by hand";
            editor.Url = "https://radio.example.org/kosmos";
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });
        await rig.ViewModel.Stations.AddCommand.ExecuteAsync();
        Check("CAT-11 CAT-13 unavailable, end to end: \"+ Add station\" still adds by hand, commits and lists it; no Notes on disk",
            rig.Settings.Stations[^1] is { Name: "Kosmos by hand", Notes: null } && rig.OnDisk().Stations.Any(s => s is { Name: "Kosmos by hand", Notes: null })
            && rig.ViewModel.Stations.Rows[^1].Name == "Kosmos by hand");
    }

    // ─── CAT-13 / CAT-09: first results and counts ───

    private static async Task FirstResultsAndCounts()
    {
        var numbered = Numbered(60);
        var sixty = new EditorRig(Loaded(numbered));
        await sixty.Settled();
        var vm = sixty.Vm;
        Check("CAT-13 CAT-09 D85 empty search shows every station ranked, capped at 50: \"Top 50 of 60 stations by votes\", overlay closed until the user asks",
            vm.Results.Count == 50 && vm.TotalCount == 60 && vm.TotalCountText == "Top 50 of 60 stations by votes" && !vm.HasNoMatches && !vm.IsResultsOpen
            && sixty.Shown.SequenceEqual(numbered.Take(50)));
        Check("CAT-17 the status line names the station count and the catalog date: \"60 stations · catalog updated 2026-09-25\"",
            vm.CatalogStatusText == "60 stations · catalog updated 2026-09-25");
        vm.SearchText = "station 0";
        await sixty.Settled();
        Check("CAT-09 a search opens the overlay; 9 of 9 shown: \"9 matches\"", vm.IsResultsOpen && vm.TotalCount == 9 && vm.TotalCountText == "9 matches");
        vm.SearchText = "Station 42";
        await sixty.Settled();
        Check("CAT-09 one match: \"1 match\"", vm.TotalCountText == "1 match" && sixty.Shown.SequenceEqual([numbered[41]]));
        vm.SearchText = "no such station anywhere";
        await sixty.Settled();
        Check("CAT-13 no match: HasNoMatches, no rows, empty count text, the overlay stays open for the no-match text",
            vm.HasNoMatches && vm.Results.Count == 0 && vm.TotalCount == 0 && vm.TotalCountText == "" && vm.IsResultsOpen);
        vm.SearchText = "";
        await sixty.Settled();
        Check("CAT-13 D85 emptying the search shows all again (\"Top 50 of 60 stations by votes\") and clears HasNoMatches",
            !vm.HasNoMatches && vm.TotalCountText == "Top 50 of 60 stations by votes" && vm.Results.Count == 50);

        var one = new EditorRig(Loaded([Kosmos], generated: null));
        await one.Settled();
        Check("CAT-17 D85 a one-station catalog without generated_utc: status \"1 station\", count \"1 station\"",
            one.Vm.CatalogStatusText == "1 station" && one.Vm.TotalCountText == "1 station");

        var empty = new EditorRig();
        await empty.Settled();
        Check("CAT-13 a loaded but empty catalog: \"0 stations\", available, no rows; the first (empty) search applied reports no match, overlay closed",
            empty.Vm.IsCatalogAvailable && empty.Vm.CatalogStatusText == "0 stations" && empty.Vm.Results.Count == 0 && empty.Vm.HasNoMatches && !empty.Vm.IsResultsOpen);
    }

    // ─── CAT-08: options, filters, Clear ───

    private static async Task FiltersAndClear()
    {
        // A search delay no check waits for: only the filters and Clear (which never wait) can bring results.
        var rig = new EditorRig(Loaded(Small), delay: Forever);
        await rig.Settled();
        var vm = rig.Vm;
        var fields = new (CatalogField Field, IReadOnlyList<CatalogFilterOption> Options)[]
        {
            (CatalogField.Country, vm.CountryOptions), (CatalogField.City, vm.CityOptions), (CatalogField.Type, vm.TypeOptions),
            (CatalogField.Genre, vm.GenreOptions), (CatalogField.Language, vm.LanguageOptions)
        };
        foreach (var (field, options) in fields)
            Check($"CAT-08 {field} options = All + AvailableValues ({string.Join(", ", options.Select(o => o.Label))})",
                options.SequenceEqual(ExpectedOptions(Small, field)) && options[0] == CatalogFilterOption.All && options[0].Value == null && options[0].ToString() == "All");
        Check("CAT-08 country options show labels and filter by code: Germany/DE, Greece/GR, Internet (collections)/XX",
            vm.CountryOptions.Skip(1).Select(o => (o.Value, o.Label)).SequenceEqual([("DE", "Germany"), ("GR", "Greece"), ("XX", "Internet (collections)")]));
        Check("CAT-08 every filter starts at All", new[] { vm.SelectedCountry, vm.SelectedCity, vm.SelectedType, vm.SelectedGenre, vm.SelectedLanguage }.All(o => o == CatalogFilterOption.All));

        var greece = vm.CountryOptions.Single(o => o.Value == "GR");
        vm.SelectedCountry = greece;
        await rig.Settled();
        Check("CAT-08 a filter change searches at once (no debounce) and opens the results: Country Greece → the three GR stations",
            vm.IsResultsOpen && rig.Shown.SequenceEqual(rig.Expected("", new CatalogFilters(Country: "GR")).Items) && rig.Shown.All(e => e.Country == "GR") && rig.Shown.Count == 3);

        vm.SearchText = "radio";
        vm.SelectedCountry = vm.CountryOptions.Single(o => o.Value == "DE");
        await rig.Settled();
        Check("CAT-08 the filter ANDs with the pending text: \"radio\" + Germany → Radio Köln AM, Radio Shortwave (not Radio Thessaloniki)",
            rig.Shown.SequenceEqual([KolnAm, Shortwave]) && rig.Shown.SequenceEqual(rig.Expected("radio", new CatalogFilters(Country: "DE")).Items));

        vm.SelectedCity = vm.CityOptions.Single(o => o.Value == "Köln");
        vm.SelectedType = vm.TypeOptions.Single(o => o.Value == "Commercial");
        vm.SelectedGenre = vm.GenreOptions.Single(o => o.Value == "Talk");
        vm.SelectedLanguage = vm.LanguageOptions.Single(o => o.Value == "German");
        await rig.Settled();
        Check("CAT-08 all five filters AND with each other and the text: → Radio Köln AM only",
            rig.Shown.SequenceEqual([KolnAm]) && rig.Shown.SequenceEqual(rig.Expected("radio", new CatalogFilters("DE", "Köln", "Commercial", "Talk", "German")).Items));
        vm.SelectedGenre = vm.GenreOptions.Single(o => o.Value == "News");
        await rig.Settled();
        Check("CAT-08 contradicting filters: no match (HasNoMatches)", vm.HasNoMatches && vm.Results.Count == 0);

        var changes = rig.Changes.Count;
        vm.SelectedGenre = null!;
        Check("CAT-08 assigning null (a picker losing its items) selects All and says so",
            vm.SelectedGenre == CatalogFilterOption.All && rig.Changes.Skip(changes).Contains(nameof(StationEditorViewModel.SelectedGenre)));
        vm.SelectedGenre = null!;
        Check("CAT-08 a second null assignment keeps All and still tells the picker (PropertyChanged)",
            vm.SelectedGenre == CatalogFilterOption.All && rig.Changes.Skip(changes).Count(p => p == nameof(StationEditorViewModel.SelectedGenre)) == 2);
        await rig.Settled();

        var resultsChanges = rig.ChangesOf(nameof(StationEditorViewModel.Results));
        vm.IsResultsOpen = false;
        var clearMark = rig.Changes.Count;
        vm.ClearFiltersCommand.Execute(null);
        await rig.Settled();
        Check("CAT-08 D85 Clear: search \"\" and every filter All, in one search (one Results change), overlay open, all stations listed (\"All 7 stations by votes\")",
            vm.SearchText == "" && new[] { vm.SelectedCountry, vm.SelectedCity, vm.SelectedType, vm.SelectedGenre, vm.SelectedLanguage }.All(o => o == CatalogFilterOption.All)
            && rig.ChangesOf(nameof(StationEditorViewModel.Results)) == resultsChanges + 1 && vm.IsResultsOpen
            && rig.Shown.SequenceEqual(rig.Expected("").Items) && vm.TotalCountText == "All 7 stations by votes");
        Check("CAT-08 Clear announces the cleared search and filters (PropertyChanged for each)",
            new[] { "SearchText", "SelectedCountry", "SelectedCity", "SelectedType", "SelectedLanguage" }.All(rig.Changes.Skip(clearMark).Contains));
        vm.CancelCommand.Execute(null);
    }

    // ─── CAT-14: the debounce never runs on the UI thread; stale results are dropped ───

    private static async Task DebounceAndStaleResults()
    {
        var rig = new EditorRig(Loaded(Small));
        await rig.Settled();
        var vm = rig.Vm;
        var before = vm.Results;
        vm.SearchText = "kos";
        Check("CAT-14 SearchText returns before any result is applied: Results, TotalCount and the overlay are unchanged synchronously",
            ReferenceEquals(vm.Results, before) && vm.TotalCount == 7 && !vm.IsResultsOpen && !vm.PendingSearch.IsCompleted);
        await rig.Settled();
        Check("CAT-14 the result arrives through the UI dispatcher's queue", rig.Shown.SequenceEqual([Kosmos]) && vm.IsResultsOpen);

        // Fast typing: every keystroke supersedes the previous search; only the final text's results are applied, once.
        var mark = rig.ChangesOf(nameof(StationEditorViewModel.Results));
        foreach (var text in new[] { "r", "ra", "rad", "radi", "radio", "radio ", "radio s", "radio sh" })
            vm.SearchText = text;
        await rig.Settled();
        Check("CAT-14 D72 fast typing (8 keystrokes, no delay): Results change exactly once, to the final text's results (Radio Shortwave)",
            rig.ChangesOf(nameof(StationEditorViewModel.Results)) == mark + 1 && rig.Shown.SequenceEqual([Shortwave]) && vm.TotalCountText == "1 match");

        var debounced = new EditorRig(Loaded(Small), delay: TimeSpan.FromMilliseconds(40));
        await debounced.Settled();
        mark = debounced.ChangesOf(nameof(StationEditorViewModel.Results));
        foreach (var text in new[] { "m", "me", "mel", "melo", "melod", "melodia" })
        {
            debounced.Vm.SearchText = text;
            await Task.Delay(2);
        }
        await debounced.Settled();
        Check("CAT-14 D72 typing faster than the 40 ms debounce: one Results change, matching the final text \"melodia\"",
            debounced.ChangesOf(nameof(StationEditorViewModel.Results)) == mark + 1 && debounced.Shown.SequenceEqual([Melodia]));

        // A debounce that never ends: if the delay ran on the calling thread, the setter would not return.
        var slow = new EditorRig(Loaded(Small), delay: Forever);
        await slow.Settled();
        var start = slow.Vm.Results;
        slow.Vm.SearchText = "bayern";
        await Task.Delay(50);
        slow.Ui.Drain();
        Check("CAT-14 D72 the debounce delay runs off the UI thread: with a 10-minute delay the setter returns, nothing is applied, the search is pending",
            ReferenceEquals(slow.Vm.Results, start) && !slow.Vm.PendingSearch.IsCompleted && slow.Vm.SearchText == "bayern");
        slow.Vm.SelectedType = slow.Vm.TypeOptions.Single(o => o.Value == "Public");
        await slow.Settled();
        Check("CAT-14 a filter change supersedes the pending debounce and searches the current text at once (bayern + Public → Bayern 3)",
            slow.Shown.SequenceEqual([Bayern]));

        // Closing cancels everything pending; nothing lands afterwards.
        var closing = new EditorRig(Loaded(Small), delay: TimeSpan.FromMilliseconds(30));
        await closing.Settled();
        var kept = closing.Vm.Results;
        closing.Vm.SearchText = "melodia";
        closing.Vm.CancelCommand.Execute(null);
        await Task.Delay(100);
        closing.Ui.Drain();
        Check("CAT-14 D72 Cancel while a search is pending: nothing is applied afterwards, PendingSearch completes",
            ReferenceEquals(closing.Vm.Results, kept) && closing.Vm.PendingSearch.IsCompleted && closing.Vm.Result == EditorResult.Cancelled && closing.Errors.Count == 0);
    }

    // ─── CAT-12 (view-model half): MoveHighlight ───

    private static async Task HighlightRules()
    {
        var rig = new EditorRig(Loaded(Small));
        await rig.Settled();
        var vm = rig.Vm;
        vm.SearchText = "radio";
        await rig.Settled();
        var rows = vm.Results;
        Check("CAT-12 D85 \"radio\" lists Radio Thessaloniki, Radio Köln AM, Radio Shortwave (votes order), the overlay open on the first row, the detail pane following it",
            rig.Shown.SequenceEqual([Thessaloniki, KolnAm, Shortwave]) && vm.IsResultsOpen && vm.HighlightedResult == rows[0] && vm.DetailRow == rows[0]);
        vm.IsResultsOpen = false;
        Check("CAT-12 D85 closing the overlay drops the highlight and the detail of an unpicked row", vm.HighlightedResult == null && vm.DetailRow == null);
        vm.MoveHighlight(-1);
        Check("CAT-12 §5.5 Up with no highlight stays none", vm.HighlightedResult == null);
        vm.MoveHighlight(1);
        Check("CAT-12 §5.5 Down with no highlight → the first row; the detail pane follows it", vm.HighlightedResult == rows[0] && vm.DetailRow == rows[0]);
        vm.MoveHighlight(1);
        Check("CAT-12 §5.5 Down → the second row", vm.HighlightedResult == rows[1] && vm.DetailRow == rows[1]);
        vm.MoveHighlight(5);
        Check("CAT-12 §5.5 past the last row clamps to the last (no wrap)", vm.HighlightedResult == rows[2]);
        vm.MoveHighlight(1);
        Check("CAT-12 §5.5 Down on the last row stays there", vm.HighlightedResult == rows[2]);
        vm.MoveHighlight(-1);
        Check("CAT-12 §5.5 Up → the previous row", vm.HighlightedResult == rows[1]);
        vm.MoveHighlight(-10);
        Check("CAT-12 §5.5 Up past the first row clamps to the first", vm.HighlightedResult == rows[0]);
        vm.SearchText = "radio s";
        await rig.Settled();
        Check("CAT-12 D85 a new result list moves the highlight to its first row (Radio Shortwave)",
            vm.HighlightedResult?.Entry == Shortwave && vm.DetailRow == vm.HighlightedResult);
        vm.IsResultsOpen = false;
        vm.SelectEntryCommand.Execute(null);
        Check("CAT-12 SelectEntryCommand with no highlight does nothing", vm.Name == "" && vm.Url == "" && vm.SelectedEntry == null);
        vm.SearchText = "nothing matches this";
        await rig.Settled();
        vm.MoveHighlight(1);
        Check("CAT-12 MoveHighlight over no results does nothing", vm.HighlightedResult == null);
    }

    // ─── CAT-10: pick, detail texts, logos ───

    private static async Task PickAndDetail()
    {
        var rig = new EditorRig(Loaded(Small));
        await rig.Settled();
        var vm = rig.Vm;
        Check("CAT-10 the applied rows' logos are requested through the loader; rows without a logo are not",
            rig.Logos.Requests.Order(StringComparer.Ordinal).SequenceEqual([Kosmos.Logo, Thessaloniki.Logo]));

        vm.SaveCommand.Execute(null);
        Check("CAT-10 fixture: a stale validation message before the pick", vm.Error == "Give this station a name.");
        var kosmosRow = vm.Results.Single(r => r.Entry == Kosmos);
        vm.HighlightedResult = kosmosRow;
        Check("CAT-10 highlighting shows the row in the detail pane (no pick yet) and loads its logo through the loader",
            vm.DetailRow == kosmosRow && vm.SelectedEntry == null && vm.Name == "" && rig.Logos.Requests.Count(u => u == Kosmos.Logo) == 2);
        Check("CAT-10 the loader gave no logo: DetailLogo null → the monogram \"K\"", vm.DetailLogo == null && vm.DetailRow!.Monogram == "K");
        vm.IsResultsOpen = true;
        vm.SelectEntryCommand.Execute(null);
        Check("CAT-10 §5.2 Select fills Name, Description and Stream URL from the entry",
            vm.Name == "Kosmos 93.6" && vm.Tag == "Public · World" && vm.Url == "https://streams.example.org/kosmos");
        Check("CAT-10 Select sets SelectedEntry, closes the overlay, clears the highlight, keeps the picked row in the detail pane",
            vm.SelectedEntry == Kosmos && !vm.IsResultsOpen && vm.HighlightedResult == null && vm.DetailRow == kosmosRow);
        Check("CAT-10 the pick goes through the normal setters: the stale validation message clears", vm.Error == null && !vm.HasError);

        var d = vm.DetailRow!;
        Check("CAT-10 detail texts: full notes, \"4,210 votes\", \"93.6 FM\", \"Greek\", \"Athens, Attica, Greece\", \"Public · World\"",
            d.Notes == KosmosNotes && d.VotesText == "4,210 votes" && d.FrequencyText == "93.6 FM" && d.LanguageText == "Greek"
            && d.Location == "Athens, Attica, Greece" && d.Kind == "Public · World" && d.Name == "Kosmos 93.6");
        Check("CAT-10 §5.2 the row's subtitle and automation name: \"Athens · 93.6 FM · Greece\", \"Kosmos 93.6, Athens · 93.6 FM · Greece\"",
            d.Subtitle == "Athens · 93.6 FM · Greece" && d.AutomationName == "Kosmos 93.6, Athens · 93.6 FM · Greece");

        var rows = Small.ToDictionary(e => e, e => new CatalogResultRow(e));
        Check("CAT-10 §5.2 votes: \"1 vote\" singular, \"\" when unknown", rows[Melodia].VotesText == "1 vote" && rows[Shortwave].VotesText == "");
        Check("CAT-10 §5.2 kind: the genre is left out when it repeats the type (\"Commercial\"), and stands alone without a type (\"Chillout\")",
            rows[Thessaloniki].Kind == "Commercial" && rows[Chill].Kind == "Chillout" && rows[KolnAm].Kind == "Commercial · Talk");
        Check("CAT-10 §5.2 location: empty parts left out (\"Köln, Germany\"; \"Internet (collections)\")",
            rows[KolnAm].Location == "Köln, Germany" && rows[Chill].Location == "Internet (collections)" && rows[Shortwave].Location == "Germany");
        Check("CAT-10 §5.2 monogram: first character upper-cased invariantly (\"chill collection\" → \"C\"), \"?\" for an empty name, a surrogate pair kept whole",
            rows[Chill].Monogram == "C" && UiText.Initial("") == "?" && UiText.Initial("\U0001F4FBradio") == "\U0001F4FB" && UiText.Initial("ärt") == "Ä");

        vm.HighlightedResult = vm.Results.Single(r => r.Entry == Thessaloniki);
        Check("CAT-10 §5.2 DetailRow = the highlighted row while there is one", vm.DetailRow!.Entry == Thessaloniki);
        vm.HighlightedResult = null;
        Check("CAT-10 §5.2 … and the picked row again when the highlight goes", vm.DetailRow == kosmosRow);

        // Truncation to the field limits, never splitting a surrogate pair.
        var longName = new string('a', 99) + " " + new string('b', 60);
        var pairName = new string('c', 99) + "\U0001F4FB" + "tail";
        var longTag = new string('t', 200);
        var pairTag = new string('u', 159) + "\U0001F4FB" + "tail";
        StationCatalogEntry Long(string name, string tag, string url) => new() { Name = name, Country = "GR", StreamUrl = url, Tag = tag };
        var longOne = Long(longName, longTag, "https://streams.example.org/long");
        var pairOne = Long(pairName, pairTag, "https://streams.example.org/pair");
        var trunc = new EditorRig(Loaded([longOne, pairOne]));
        await trunc.Settled();
        Pick(trunc.Vm, longOne);
        Check("CAT-10 §5.2 a 160-character name is cut to 100 and trimmed at the end (99 characters: the 100th was a space); a 200-character tag to 160",
            trunc.Vm.Name == new string('a', 99) && trunc.Vm.Tag == new string('t', 160) && trunc.Vm.Url == "https://streams.example.org/long");
        Pick(trunc.Vm, pairOne);
        Check("CAT-10 truncation never splits a surrogate pair at the limit (name 99, tag 159 characters)",
            trunc.Vm.Name == new string('c', 99) && trunc.Vm.Tag == new string('u', 159) && trunc.Vm.SelectedEntry == pairOne);
        Check("CAT-10 no errors were reported while picking", rig.Errors.Count == 0 && trunc.Errors.Count == 0);
    }

    // ─── CAT-10 / D73: notes on save ───

    private static async Task<(EditorRig Rig, Station? Saved)> SaveAfter(Action<StationEditorViewModel> act)
    {
        var rig = new EditorRig(Loaded(Small));
        await rig.Settled();
        act(rig.Vm);
        rig.Vm.SaveCommand.Execute(null);
        return (rig, rig.Settings.Stations.SingleOrDefault());
    }

    private static async Task NotesOnSave()
    {
        var (rig, saved) = await SaveAfter(vm => Pick(vm, Kosmos));
        Check("CAT-10 D73 a picked station saves with the entry's notes (URL unchanged)",
            rig.Vm.Result == EditorResult.Saved && saved is { Name: "Kosmos 93.6", Tag: "Public · World", Url: "https://streams.example.org/kosmos" } && saved.Notes == KosmosNotes);
        (_, saved) = await SaveAfter(vm => { Pick(vm, Kosmos); vm.Url = "https://streams.example.org/kosmos-hq"; });
        Check("CAT-10 D73 the URL changed after the pick: no notes", saved is { Url: "https://streams.example.org/kosmos-hq", Notes: null });
        (_, saved) = await SaveAfter(vm => { Pick(vm, Kosmos); vm.Url = "https://other.example.org/"; vm.Url = "  https://streams.example.org/kosmos  "; });
        Check("CAT-10 D73 the saved (trimmed) URL equals the picked one again: the notes are kept", saved?.Notes == KosmosNotes);
        (_, saved) = await SaveAfter(vm => { Pick(vm, Kosmos); vm.Url = "https://streams.example.org/KOSMOS"; });
        Check("CAT-10 D73 the comparison is ordinal: a URL differing only in case gets no notes", saved is { Notes: null });
        (_, saved) = await SaveAfter(vm => { Pick(vm, Kosmos); vm.Name = "My Kosmos"; vm.Tag = "Mine"; });
        Check("CAT-10 D73 editing the name or description keeps the notes (only the URL decides)", saved is { Name: "My Kosmos", Tag: "Mine" } && saved.Notes == KosmosNotes);
        (_, saved) = await SaveAfter(vm => { Pick(vm, Kosmos); Pick(vm, Thessaloniki); });
        Check("CAT-10 D73 the last pick wins: its fields and notes", saved is { Name: "Radio Thessaloniki", Notes: "A regional station." });
        (_, saved) = await SaveAfter(vm => Pick(vm, Melodia));
        Check("CAT-10 D73 an entry without notes saves Notes = null (not \"\")", saved is { Name: "Melodia 99.2", Notes: null });
        (_, saved) = await SaveAfter(vm => { vm.Name = "By hand"; vm.Url = "https://streams.example.org/kosmos"; });
        Check("CAT-10 D73 a station entered by hand gets no notes, even with a catalog URL", saved is { Name: "By hand", Notes: null });

        // Through the Stations page and the settings file (the real commit path).
        await using var ui = UiRig.CreateViewModels();
        ui.Catalog.Result = Loaded(Small);
        ui.Recorder!.StationScripts.Enqueue(async editor =>
        {
            if (!await SettleAsync(editor)) throw new TimeoutException("The catalog search did not settle.");
            Pick(editor, Thessaloniki);
            editor.SaveCommand.Execute(null);
        });
        await ui.ViewModel.Stations.AddCommand.ExecuteAsync();
        Check("CAT-10 D73 end to end: the picked station is committed with its notes and they are on disk",
            ui.Settings.Stations[^1] is { Name: "Radio Thessaloniki", Notes: "A regional station." }
            && ui.OnDisk().Stations.Any(s => s is { Name: "Radio Thessaloniki", Url: "https://streams.example.org/thessaloniki", Notes: "A regional station." }));
    }

    /// <summary>Waits for an editor built by the app's own wiring (inline dispatcher, the default debounce); false after 10 s.</summary>
    private static async Task<bool> SettleAsync(StationEditorViewModel editor)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10); // real time on purpose: bounds a test's wait only
        while (!editor.PendingSearch.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(1);
        }
        return true;
    }

    // ─── CAT-11: Edit mode is the plain form ───

    private static async Task EditModeUnchanged()
    {
        var station = Saved("Kosmos", "https://radio.example.org/kosmos", notes: "Picked from the catalog.");
        var settings = new Settings { Stations = [station] };
        var rig = new EditorRig(Loaded(Small), original: station, settings: settings);
        await Task.Delay(20);
        rig.Ui.Drain();
        var vm = rig.Vm;
        Check("CAT-11 D62 Edit mode never loads the catalog (no GetCatalogAsync call)", rig.Catalog.Calls == 0);
        Check("CAT-11 Edit mode: not Add mode, not loading, not available, no status text, nothing pending",
            !vm.IsAddMode && !vm.IsCatalogLoading && !vm.IsCatalogAvailable && vm.CatalogStatusText == "" && vm.PendingSearch.IsCompleted
            && vm.Title == "Edit station" && vm.CanDelete && vm.Name == "Kosmos" && vm.Url == "https://radio.example.org/kosmos");
        vm.SearchText = "kos";
        vm.SelectedCountry = new CatalogFilterOption("GR", "Greece");
        vm.ClearFiltersCommand.Execute(null);
        await Task.Delay(20);
        rig.Ui.Drain();
        Check("CAT-11 Edit mode: search, filters and Clear do nothing", vm.Results.Count == 0 && !vm.IsResultsOpen && rig.Ui.Pending == 0 && vm.DetailRow == null);
        vm.Name = " ";
        vm.SaveCommand.Execute(null);
        Check("CAT-11 BHV-52 Edit mode: \"Give this station a name.\", focus Name (unchanged)", vm.Error == "Give this station a name." && rig.Focus[^1] == "Name");
        vm.Name = "Kosmos 93.6";
        vm.Url = "ftp://radio.example.org/kosmos";
        vm.SaveCommand.Execute(null);
        Check("CAT-11 BHV-52 Edit mode: \"Enter a valid HTTP or HTTPS stream URL.\", focus Url (unchanged)",
            vm.Error == "Enter a valid HTTP or HTTPS stream URL." && rig.Focus[^1] == "Url");
        vm.Url = "https://radio.example.org/kosmos-936";
        vm.SaveCommand.Execute(null);
        Check("CAT-11 D62 BHV-53 Edit mode Save never touches Notes (kept even though the URL changed); the station is updated in place",
            vm.Result == EditorResult.Saved && settings.Stations.Single() == station
            && station is { Name: "Kosmos 93.6", Url: "https://radio.example.org/kosmos-936", Notes: "Picked from the catalog." });
        var plain = Saved("Plain", "https://radio.example.org/plain");
        var editPlain = new EditorRig(Loaded(Small), original: plain, settings: new Settings { Stations = [plain] });
        editPlain.Vm.SaveCommand.Execute(null);
        Check("CAT-11 Edit mode Save leaves a station without notes without notes", plain.Notes == null && editPlain.Catalog.Calls == 0);
    }

    // ─── the real 8,274-entry catalog ───

    private static async Task RealCatalog()
    {
        var real = await RealAsync();
        Check("CAT-10 CAT-17 fixture: the real app-catalog.json next to the test binary loads", real.State == CatalogLoadState.Loaded && real.Catalog.Entries.Count > 50);
        var entries = real.Catalog.Entries;
        var rig = new EditorRig(real);
        await rig.Settled();
        var vm = rig.Vm;
        var count = entries.Count;
        var grouped = count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        Check($"CAT-17 D85 real catalog: the status line is \"{vm.CatalogStatusText}\": the station count (grouped) and generated_utc's UTC date",
            real.GeneratedUtc is { } generated
            && vm.CatalogStatusText == $"{grouped} stations · catalog updated {generated.UtcDateTime:yyyy-MM-dd}" && vm.CatalogStatusText == UiText.CatalogStatus(count, generated));
        Check($"CAT-13 D85 real catalog: the empty search lists the 50 most-voted of {count}: \"Top 50 of {grouped} stations by votes\"",
            vm.Results.Count == 50 && vm.TotalCount == count && vm.TotalCountText == $"Top 50 of {grouped} stations by votes"
            && rig.Shown.SequenceEqual(StationCatalogQuery.Search(real.Catalog, "", CatalogFilters.None).Items));
        foreach (var field in Enum.GetValues<CatalogField>())
        {
            var options = field switch
            {
                CatalogField.Country => vm.CountryOptions, CatalogField.City => vm.CityOptions, CatalogField.Type => vm.TypeOptions,
                CatalogField.Genre => vm.GenreOptions, _ => vm.LanguageOptions
            };
            Check($"CAT-08 real catalog: {field} options = All + the catalog's {options.Count - 1} distinct values",
                options.SequenceEqual(ExpectedOptions(entries, field)) && options.Count > 1);
        }

        var labels = entries.Select(e => (Entry: e, Row: new CatalogResultRow(e))).ToList();
        var wrong = labels.Where(x =>
        {
            var band = StationCatalogQuery.BandOf(x.Entry.FrequencyFm);
            var text = x.Row.FrequencyText;
            return text.EndsWith(" FM", StringComparison.Ordinal) != (band == FrequencyBand.Fm)
                || text.EndsWith(" kHz", StringComparison.Ordinal) != (band == FrequencyBand.Kilohertz)
                || (band == FrequencyBand.None && text != x.Entry.FrequencyFm);
        }).Select(x => $"{x.Entry.FrequencyFm} → {x.Row.FrequencyText}").ToList();
        if (wrong.Count > 0) Console.WriteLine("  wrong labels: " + string.Join("; ", wrong.Take(10)));
        Check($"CAT-07 real catalog: all {count} rows label \" FM\" exactly when BandOf is Fm and \" kHz\" exactly when Kilohertz, raw otherwise " +
            $"({labels.Count(x => x.Row.FrequencyText.EndsWith(" FM", StringComparison.Ordinal))} FM, {labels.Count(x => x.Row.FrequencyText.EndsWith(" kHz", StringComparison.Ordinal))} kHz)",
            wrong.Count == 0);

        var country = vm.CountryOptions[1];
        vm.SelectedCountry = country;
        vm.SearchText = "radio";
        await rig.Settled();
        Check($"CAT-08 real catalog: \"radio\" + Country {country.Label} = the query's own result ({vm.TotalCount} matches)",
            rig.Shown.SequenceEqual(StationCatalogQuery.Search(real.Catalog, "radio", new CatalogFilters(Country: country.Value)).Items)
            && vm.Results.All(r => r.Entry.Country == country.Value));
        vm.ClearFiltersCommand.Execute(null);
        await rig.Settled();

        var longestNotes = entries.MaxBy(e => e.Notes.Length)!;
        var longestName = entries.MaxBy(e => e.Name.Length)!;
        var picker = new EditorRig(Loaded([longestNotes, longestName]));
        await picker.Settled();
        Pick(picker.Vm, longestName);
        Check($"CAT-10 real catalog: the longest name ({longestName.Name.Length} characters) fills Name within 100",
            picker.Vm.Name.Length <= StationEditorViewModel.NameMaxLength && longestName.Name.StartsWith(picker.Vm.Name, StringComparison.Ordinal));
        Pick(picker.Vm, longestNotes);
        picker.Vm.SaveCommand.Execute(null);
        Check($"CAT-10 real catalog: the longest notes ({longestNotes.Notes.Length} characters) are saved whole with the station",
            picker.Settings.Stations.Single().Notes == longestNotes.Notes && picker.Vm.DetailRow?.Notes == longestNotes.Notes);
    }
}
