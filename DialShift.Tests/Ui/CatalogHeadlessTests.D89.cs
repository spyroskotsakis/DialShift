using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DialShift.App.Services;
using DialShift.App.ViewModels;
using DialShift.App.Views.Dialogs;
using DialShift.Core;
using DialShift.Core.Catalog;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.CatalogUiFixtures;
using static DialShift.Tests.Ui.Headless;
using static DialShift.Tests.Ui.HeadlessUiTests;

namespace DialShift.Tests.Ui;

/// <summary>
/// The Add dialog's D89 polish on the headless platform (docs/catalog-contracts.md §5.5, §8 CAT-12, CAT-13, CAT-14):
/// Enter while the post-load browse search is still pending falls through to Save; a run-now search that fails picks
/// nothing and closes the results; the results' scroll bar, expanded under the pointer, covers no row text; the detail
/// pane's placeholder offers browsing only when the catalog has stations; and the filter labels step down to the hint ink
/// while their pickers are disabled, readable either way.
/// </summary>
/// <remarks>
/// The two Enter checks open the real <see cref="StationEditorDialog"/> over an editor whose UI dispatcher is a
/// <see cref="QueuedUiDispatcher"/>, stepped by the check: the catalog load and every search still run on the thread pool
/// as in the app, but their results reach the editor only when the check runs the queue, so "Enter while that search is
/// pending" is a state the check holds, not a race it hopes to win.
/// </remarks>
internal static partial class CatalogHeadlessTests
{
    /// <summary>The Add editor over <paramref name="result"/>, with a stepped UI thread, in the real dialog (shown, not yet loaded).</summary>
    private sealed class SteppedEditor
    {
        public SteppedEditor(CatalogLoadResult result, TimeSpan searchDelay)
        {
            Catalog = new FakeCatalogProvider { Result = result };
            Editor = new StationEditorViewModel(Settings, null, new RecordingDialogService(), Errors.Add, Catalog, new FakeLogoLoader(), Ui, searchDelay);
            Dialog = new StationEditorDialog(Editor);
        }

        public Settings Settings { get; } = new();
        public QueuedUiDispatcher Ui { get; } = new();
        public FakeCatalogProvider Catalog { get; }
        public List<Exception> Errors { get; } = [];
        public StationEditorViewModel Editor { get; }
        public StationEditorDialog Dialog { get; }

        public async Task ShowAsync()
        {
            Dialog.Show();
            await PumpAsync();
            Layout(Dialog);
        }

        /// <summary>Runs posts until the catalog load has applied, and not one post more: the first search is then scheduled, its result not applied.</summary>
        public async Task ApplyLoadOnlyAsync()
        {
            // The load's single post, ApplyCatalog, is the first thing the editor queues (real time bounds the wait only).
            if (!await WaitAsync(() => Ui.Pending > 0)) throw new TimeoutException("The catalog load never posted its result.");
            Ui.RunOne();
            Layout(Dialog);
        }

        /// <summary>Runs posts until the latest search has been applied.</summary>
        public async Task SettleAsync()
        {
            if (!await Ui.RunUntilAsync(() => Editor.PendingSearch.IsCompleted)) throw new TimeoutException("The editor's search did not settle within 10 s.");
            Ui.Drain();
            Layout(Dialog);
        }
    }

    // ─── CAT-12 D89 (1): Enter with the first browse search still pending is Save ───

    private static async Task EnterWithTheFirstBrowsePending()
    {
        var rig = new SteppedEditor(Loaded(Small), StationEditorViewModel.DefaultSearchDelay);
        var (dialog, editor) = (rig.Dialog, rig.Editor);
        await rig.ShowAsync();
        await rig.ApplyLoadOnlyAsync();
        var search = ByName<TextBox>(dialog, "Search stations");
        var name = Field(dialog, StationEditorViewModel.NameLabel);
        Check("CAT-12 D89 fixture: the catalog applied and its first (browse) search is pending, not applied: available, the search box empty " +
              "and focused, no rows, the results closed",
            editor.IsCatalogAvailable && !editor.PendingSearch.IsCompleted && editor.SearchText.Length == 0 && editor.Results.Count == 0
            && !editor.IsResultsOpen && !Overlay(dialog).IsVisible && Focused(dialog) == search);

        // Whether the search box handled Enter: read as the key bubbles out of it, after the dialog's tunnel handler set it
        // and before the window's default button (Save) sees it.
        var handled = new List<bool>();
        search.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) handled.Add(e.Handled); },
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        await PressAsync(dialog, Key.Enter);
        Layout(dialog);
        Console.WriteLine($"  Enter with the browse pending: handled by the search box {string.Join(",", handled)}, rows {editor.Results.Count}, open {editor.IsResultsOpen}, " +
                          $"picked {editor.SelectedEntry?.Name ?? "none"}, name \"{name.Text}\", error \"{VisibleError(dialog)?.Text}\", focus {FocusedName(dialog)}");
        Check("CAT-12 D89 Enter in the empty search box while the post-load browse search is pending is not handled by the search box and picks nothing",
            handled.SequenceEqual([false]) && editor.SelectedEntry == null && editor.DetailRow == null && name.Text == "");
        Check($"CAT-12 D89 … the pending browse search is applied at once: all {Small.Count} stations by votes (Kosmos 93.6 first), \"{UiText.BrowseCount(Small.Count, Small.Count)}\", nothing pending",
            editor.PendingSearch.IsCompleted && editor.Results.Select(r => r.Entry).SequenceEqual(StationCatalogQuery.Search(rig.Catalog.Result.Catalog, "", CatalogFilters.None).Items)
            && editor.Results[0].Entry == Kosmos && editor.TotalCountText == UiText.BrowseCount(Small.Count, Small.Count));
        Check("CAT-12 D89 … the results stay closed, nothing highlighted",
            !editor.IsResultsOpen && !Overlay(dialog).IsVisible && editor.HighlightedResult == null);
        Check("CAT-12 D89 … and Enter goes on to Save: the missing-name message in the name field, nothing added, the dialog open",
            VisibleError(dialog)?.Text == "Give this station a name." && Focused(dialog) == name && dialog.IsVisible
            && rig.Settings.Stations.Count == 0 && rig.Errors.Count == 0);

        // The thread pool's copy of that search is a superseded generation: whenever it lands, it changes nothing.
        var shown = editor.Results;
        await Task.Delay(50);
        rig.Ui.Drain();
        Layout(dialog);
        Check("CAT-12 D89 … the superseded background copy of the browse search changes nothing when it lands (same rows, still closed, nothing picked)",
            ReferenceEquals(editor.Results, shown) && !Overlay(dialog).IsVisible && editor.SelectedEntry == null && rig.Errors.Count == 0);
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-12 D89 (3): a run-now search that fails picks nothing and closes the results ───

    /// <summary>
    /// No App seam makes a search fail, so the check breaks the catalog index it hands the editor: after the first results
    /// landed, every entry's folded-name key is set to null through reflection on the index's private key array, so any
    /// search with text throws from the query engine, as a defect there would. When a Core refactor renames or removes that
    /// field, the check FAILS naming it (never SKIPs), so the coverage cannot drop silently.
    /// </summary>
    private static async Task EnterWhenTheSearchFails()
    {
        const string Name = "CAT-12 D89 Enter while a search is pending that fails when run now: handled, nothing picked, the results closed, the failure reported";
        const string KeysField = "keys";
        var loaded = Loaded(Small);
        var index = loaded.Catalog;
        var keys = typeof(StationCatalogIndex).GetField(KeysField, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(index) as StationCatalogIndex.SearchKeys[];
        Check($"CAT-12 D89 fixture: StationCatalogIndex has the private {nameof(StationCatalogIndex.SearchKeys)}[] field '{KeysField}' that this check breaks " +
              "to make a search fail (the App has no failing-search seam; if a Core refactor renamed or removed it, update this check)",
            keys != null);
        var rig = new SteppedEditor(loaded, TimeSpan.FromMinutes(10));
        var (dialog, editor) = (rig.Dialog, rig.Editor);
        await rig.ShowAsync();
        await rig.SettleAsync();
        var search = ByName<TextBox>(dialog, "Search stations");
        var name = Field(dialog, StationEditorViewModel.NameLabel);
        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check("CAT-12 D89 fixture: Down opens the browse results on Kosmos 93.6", Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == Kosmos);

        for (var i = 0; i < keys!.Length; i++) keys[i] = keys[i] with { Name = null! };
        Check("CAT-12 D89 fixture: the broken index still browses (no text) but a search with text throws",
            StationCatalogQuery.Search(index, "", CatalogFilters.None).TotalCount == Small.Count
            && Throws<NullReferenceException>(() => StationCatalogQuery.Search(index, "melodia", CatalogFilters.None)));

        await TypeAsync(search, "melodia");
        Layout(dialog);
        var stale = editor.Results;
        Check("CAT-12 D89 fixture: the search for \"melodia\" is pending (its delay is 10 minutes) with the stale browse rows open, Kosmos 93.6 highlighted",
            !editor.PendingSearch.IsCompleted && Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == Kosmos && rig.Errors.Count == 0);

        var handled = new List<bool>();
        search.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) handled.Add(e.Handled); },
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        await PressAsync(dialog, Key.Enter);
        Layout(dialog);
        Console.WriteLine($"  Enter with a failing search: handled {string.Join(",", handled)}, errors [{string.Join(", ", rig.Errors.Select(e => e.GetType().Name))}], " +
                          $"open {editor.IsResultsOpen}, highlight {editor.HighlightedResult?.Entry.Name ?? "none"}, picked {editor.SelectedEntry?.Name ?? "none"}, name \"{name.Text}\"");
        Check(Name,
            handled.SequenceEqual([true]) && editor.SelectedEntry == null && name.Text == "" && editor.DetailRow == null
            && !editor.IsResultsOpen && !Overlay(dialog).IsVisible && editor.HighlightedResult == null && editor.PendingSearch.IsCompleted
            && rig.Errors.Count == 1 && rig.Errors[0] is NullReferenceException);
        Check("CAT-12 D89 … Enter being handled, nothing is saved: no name error, the dialog open, the focus in the search box, the stale rows left as they were",
            VisibleError(dialog) == null && dialog.IsVisible && rig.Settings.Stations.Count == 0 && Focused(dialog) == search && ReferenceEquals(editor.Results, stale));

        await PressAsync(dialog, Key.Enter);
        Layout(dialog);
        Check("CAT-12 D89 a second Enter cannot pick the stale rows (FailSearch closed the results): nothing picked, it is Save, the missing-name message; no second error",
            editor.SelectedEntry == null && name.Text == "" && !Overlay(dialog).IsVisible
            && VisibleError(dialog)?.Text == "Give this station a name." && rig.Settings.Stations.Count == 0 && rig.Errors.Count == 1);

        // The scheduled copy was cancelled by the run-now search: nothing lands later.
        await Task.Delay(50);
        rig.Ui.Drain();
        Layout(dialog);
        Check("CAT-12 D89 … the cancelled background search reports nothing and reopens nothing later",
            rig.Errors.Count == 1 && !Overlay(dialog).IsVisible && editor.SelectedEntry == null);
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-14 D89 (4): the expanded results scroll bar covers no row text ───

    private static async Task ResultsScrollBarCoversNoText()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        rig.Catalog.Result = await RealAsync();
        foreach (var width in new[] { 620, 680 })
        {
            var (dialog, editor) = await OpenAddAsync(rig);
            dialog.Width = width;
            await PressAsync(dialog, Key.Down);
            Layout(dialog);
            var list = ByName<ListBox>(dialog, "Station catalog results");
            var scroll = Find<ScrollViewer>(list).Single();
            var bar = Find<ScrollBar>(scroll).Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
            var thumb = Find<Thumb>(bar).Single();
            Rect Drawn() => new Rect(thumb.Bounds.Size).TransformToAABB(thumb.TransformToVisual(dialog)!.Value);
            Check($"CAT-14 D89 fixture: the real catalog's browse results are open at {width} wide, more than the card holds (the list has a scroll bar)",
                Overlay(dialog).IsVisible && editor.Results.Count > 0 && scroll.Extent.Height > scroll.Viewport.Height + 1 && bar.IsEffectivelyVisible);

            var barBox = InWindow(bar, dialog);
            dialog.MouseMove(barBox.Center, RawInputModifiers.None);
            // The thumb widens over a few frames: render until it holds still.
            var (last, still) = (-1.0, 0);
            var expanded = await WaitAsync(() =>
            {
                Layout(dialog);
                var w = Drawn().Width;
                (still, last) = (Math.Abs(w - last) < 0.01 ? still + 1 : 0, w);
                return bar.IsExpanded && still >= 3;
            });
            var wide = Drawn();
            var barLeft = Math.Min(barBox.X, wide.X);
            var view = InWindow(scroll, dialog);

            // Every text of every row in view: its laid-out box, and (for the log) where its glyphs end.
            var texts = Items(dialog).Where(i => InWindow(i, dialog).Intersects(view))
                .SelectMany(i => Find<TextBlock>(i).Where(t => t.IsEffectivelyVisible && DisplayText(t).Length > 0).Select(t => (Row: i, Text: t)))
                .ToList();
            double Right(TextBlock t) => InWindow(t, dialog).Right;
            double GlyphRight(TextBlock t) => InWindow(t, dialog).X + t.TextLayout.TextLines.Max(l => l.Start + l.WidthIncludingTrailingWhitespace);
            var rows = texts.Select(t => t.Row).Distinct().Count();
            var frequencies = texts.Count(t => t.Text.Classes.Contains("resultFrequency"));
            var worst = texts.OrderByDescending(t => Right(t.Text)).First();
            var under = texts.Where(t => Right(t.Text) >= barLeft || GlyphRight(t.Text) >= barLeft)
                .Select(t => $"\"{DisplayText(t.Text)}\" ends at x={Right(t.Text):F1}").ToList();
            Console.WriteLine($"  results scroll bar at {width} wide: expanded {bar.IsExpanded}, bar x={barBox.X:F1}–{barBox.Right:F1}, thumb {Show(wide)}; " +
                              $"{rows} rows in view, {texts.Count} texts ({frequencies} frequencies), rightmost \"{DisplayText(worst.Text)}\" box ends at x={Right(worst.Text):F1}, " +
                              $"glyphs at x={texts.Max(t => GlyphRight(t.Text)):F1}");
            foreach (var u in under.Take(5)) Console.WriteLine("  under the bar: " + u);
            Check($"CAT-14 D89 real catalog, dialog {width} wide, the results' scroll bar under the pointer and expanded ({barBox.Width:F1} px): no text of the {rows} rows in view, " +
                  $"frequencies included ({frequencies}), reaches it (rightmost text box ends at x={Right(worst.Text):F1}, the bar starts at x={barLeft:F1})",
                expanded && bar.IsPointerOver && rows >= 3 && frequencies > 0 && under.Count == 0
                && texts.All(t => Right(t.Text) < barLeft && GlyphRight(t.Text) < barLeft));
            Png(dialog, $"catalog-d89-results-scrollbar-{width}");
            dialog.Close();
            await PumpAsync();
        }
    }

    // ─── CAT-13 D89 (5): the detail pane's placeholder ───

    private static TextBlock Placeholder(Window dialog) =>
        Find<TextBlock>(Detail(dialog)).Single(t => t.Classes.Contains("hint") && t.GetVisualParent() is Panel p && p.GetVisualParent() == Detail(dialog));

    private static async Task DetailPlaceholders()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        var hold = new TaskCompletionSource();
        rig.Catalog.Hold = hold;
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig, settle: false);
        Check("CAT-13 D89 loading: the detail pane shows \"Details and notes show here.\" (no browse hint: there is nothing to browse yet)",
            editor.IsCatalogLoading && Detail(dialog).IsEffectivelyVisible && Placeholder(dialog) is { IsEffectivelyVisible: true, Text: UiText.CatalogDetailPlaceholderEmpty });
        Png(dialog, "catalog-d89-placeholder-loading");
        hold.SetResult();
        if (!await WaitAsync(() => editor.PendingSearch.IsCompleted)) throw new TimeoutException("The held load did not land.");
        Layout(dialog);
        Check("CAT-13 D89 the load lands with stations: the placeholder turns into the browse hint (the view follows the change)",
            editor.IsCatalogAvailable && Placeholder(dialog) is { IsEffectivelyVisible: true, Text: UiText.CatalogDetailPlaceholder });
        dialog.Close();
        await PumpAsync();

        rig.Catalog.Hold = null;
        rig.Catalog.Result = Loaded([]);
        (dialog, editor) = await OpenAddAsync(rig);
        Check("CAT-13 D89 a loaded empty catalog: the detail pane shows \"Details and notes show here.\", not the browse hint",
            editor.IsCatalogAvailable && StatusLine(dialog).Text == "0 stations · catalog updated 2026-09-25"
            && Placeholder(dialog) is { IsEffectivelyVisible: true, Text: UiText.CatalogDetailPlaceholderEmpty });
        Png(dialog, "catalog-d89-placeholder-empty");
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-13 CAT-14 D89 (6): the filter labels' ink ───

    private static async Task FilterLabelInk()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        var hold = new TaskCompletionSource();
        rig.Catalog.Hold = hold;
        rig.Catalog.Result = Loaded(Small);

        void CheckLabels(StationEditorDialog dialog, string state, bool enabled)
        {
            Layout(dialog);
            var expected = ThemeBrush(dialog, enabled ? "DsSecondaryBrush" : "DsSubtleBrush");
            var other = ThemeBrush(dialog, enabled ? "DsSubtleBrush" : "DsSecondaryBrush");
            var labels = Find<TextBlock>(dialog).Where(t => t.Classes.Contains("filterLabel")).ToList();
            var pickers = Find<ComboBox>(dialog).Where(c => c.Classes.Contains("filter")).ToList();
            using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
            var inks = labels.Select(l =>
            {
                var under = BackgroundUnder(l, dialog);
                var area = new Rect(InWindow(l, dialog).Position, new Size(l.TextLayout.WidthIncludingTrailingWhitespace, l.TextLayout.Height));
                return (Label: l, Color: (l.Foreground as ISolidColorBrush)?.Color, Contrast: Contrast(Over(InkOf(l.Foreground, l, dialog), under), under),
                    Rendered: RenderedContrast(frame, area, under));
            }).ToList();
            Console.WriteLine($"  filter labels ({state}): " + string.Join("; ", inks.Select(i => $"{i.Label.Text} {i.Color} {i.Contrast:F2}:1 / rendered {i.Rendered:F2}:1")));
            Check($"CAT-13 D89 {state}: the five pickers are {(enabled ? "enabled" : "disabled")} and their labels (Country, City, Type, Genre, Language) " +
                  $"are {(enabled ? "enabled" : "disabled")} with them",
                labels.Select(l => l.Text).SequenceEqual(["Country", "City", "Type", "Genre", "Language"]) && pickers.Count == 5
                && pickers.All(p => p.IsEffectivelyEnabled == enabled) && labels.All(l => l.IsEffectivelyEnabled == enabled));
            Check($"CAT-13 D89 {state}: every filter label is drawn in {(enabled ? "the regular label ink, DsSecondaryBrush" : "the hint ink, DsSubtleBrush")} ({expected.Color}), " +
                  $"not {other.Color}",
                expected.Color != other.Color && inks.All(i => i.Color == expected.Color && i.Label.Opacity == 1));
            Check($"CAT-14 D89 {state}: every filter label reaches 4.5:1 on the dialog background as the brushes resolve and composite " +
                  $"(lowest {inks.Min(i => i.Contrast):F2}:1; rendered {inks.Min(i => i.Rendered):F2}:1)",
                inks.All(i => i.Contrast >= MinContrast));
        }

        var (dialog, editor) = await OpenAddAsync(rig, settle: false);
        Check("CAT-13 D89 fixture: the catalog is loading", editor.IsCatalogLoading && !editor.IsCatalogAvailable);
        CheckLabels(dialog, "loading", enabled: false);
        hold.SetResult();
        if (!await WaitAsync(() => editor.PendingSearch.IsCompleted)) throw new TimeoutException("The held load did not land.");
        CheckLabels(dialog, "loaded", enabled: true);
        Png(dialog, "catalog-d89-filter-labels-loaded");
        dialog.Close();
        await PumpAsync();

        rig.Catalog.Hold = null;
        rig.Catalog.Result = CatalogLoadResult.Unavailable("the file is missing.");
        (dialog, editor) = await OpenAddAsync(rig);
        Check("CAT-13 D89 fixture: no catalog (unavailable)", !editor.IsCatalogLoading && !editor.IsCatalogAvailable && StatusLine(dialog).Text == UiText.CatalogUnavailable);
        CheckLabels(dialog, "no catalog", enabled: false);
        Png(dialog, "catalog-d89-filter-labels-no-catalog");
        dialog.Close();
        await PumpAsync();
    }
}
