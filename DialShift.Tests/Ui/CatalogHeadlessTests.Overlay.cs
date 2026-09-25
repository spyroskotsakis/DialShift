using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DialShift.App.ViewModels;
using DialShift.App.Views.Dialogs;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.CatalogUiFixtures;
using static DialShift.Tests.Ui.Headless;
using static DialShift.Tests.Ui.HeadlessUiTests;

namespace DialShift.Tests.Ui;

/// <summary>
/// When the Add dialog's results overlay opens and closes, through real input on the headless platform
/// (docs/catalog-contracts.md §5.5 as amended by D83 and D85; §8 CAT-12, CAT-10 M1, CAT-14 P10): Down, Clear, a tap in the
/// search box, Tab into the form, a press on the status line or the detail pane, new results, Escape layer by layer, the
/// title-bar close, and Page Down / Page Up on the detail pane.
/// </summary>
internal static partial class CatalogHeadlessTests
{
    /// <summary>
    /// A real left press and release at <paramref name="local"/> in <paramref name="control"/> (its center when null). Returns
    /// what the press hit, so a check can prove the press landed on the control and not on something drawn over it.
    /// </summary>
    private static async Task<Visual?> PressAtAsync(Control control, Point? local = null)
    {
        var window = (Window)TopLevel.GetTopLevel(control)!;
        Layout(window);
        var at = control.TranslatePoint(local ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The control has no position in its window.");
        var hit = window.InputHitTest(at) as Visual;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        await PumpAsync();
        Layout(window);
        return hit;
    }

    private static bool Within(Visual? hit, Visual control) => hit != null && (hit == control || control.IsVisualAncestorOf(hit));

    private static string DetailName(Window dialog) =>
        Find<TextBlock>(Detail(dialog)).SingleOrDefault(t => t.Classes.Contains("detailName") && t.IsEffectivelyVisible)?.Text ?? "(none)";

    // ─── CAT-12 D83/D85: what opens and closes the overlay; CAT-14 P10 footers; CAT-10 M1 ───

    private static async Task OverlayOpensAndCloses()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        var search = ByName<TextBox>(dialog, "Search stations");
        var clear = ByName<Button>(dialog, "Clear search and filters");
        var name = Field(dialog, StationEditorViewModel.NameLabel);
        var list = Find<ListBox>(dialog).Single(l => l.Name == "ResultsList");

        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check("CAT-12 D85 Down with the results closed (nothing typed) opens them on the first row (Kosmos 93.6), the focus stays in the search box",
            Overlay(dialog).IsVisible && editor.HighlightedResult == editor.Results[0] && editor.Results[0].Entry == Kosmos
            && list.SelectedItem == editor.Results[0] && Focused(dialog) == search && DetailName(dialog) == "Kosmos 93.6");
        Check("CAT-14 D85 P10 the unfiltered footer of the whole 7-station catalog reads \"All 7 stations by votes\"",
            Shows(Overlay(dialog), "All 7 stations by votes") && editor.TotalCountText == "All 7 stations by votes");

        await PressAsync(dialog, Key.Escape);
        Check("CAT-12 fixture: Escape closed the results", !Overlay(dialog).IsVisible && dialog.IsVisible);
        var hit = await PressAtAsync(clear);
        Check("CAT-12 D83 a click on Clear opens the results (search \"\" and every filter All): the first row highlighted, the footer \"All 7 stations by votes\"",
            Within(hit, clear) && await WaitAsync(() => editor.PendingSearch.IsCompleted && Overlay(dialog).IsVisible)
            && editor.HighlightedResult == editor.Results[0] && Shows(Overlay(dialog), "All 7 stations by votes"));

        clear.Focus();
        await PumpAsync();
        Check("CAT-12 fixture: the results are open with the focus on Clear", Overlay(dialog).IsVisible && Focused(dialog) == clear);
        await PressAsync(dialog, Key.Tab);
        Layout(dialog);
        Check("CAT-12 D83 Tab from Clear into the name field closes the results and drops the highlight; the detail pane shows its placeholder again",
            Focused(dialog) == name && !Overlay(dialog).IsVisible && editor.HighlightedResult == null && Shows(Detail(dialog), UiText.CatalogDetailPlaceholder));

        hit = await PressAtAsync(search);
        Check("CAT-12 D83 D85 a tap in the search box reopens the last results, on the first row",
            Within(hit, search) && Overlay(dialog).IsVisible && editor.HighlightedResult == editor.Results[0] && Focused(dialog) == search);

        // The status line is as wide as the dialog, its text is not: press on its first glyph.
        var status = StatusLine(dialog);
        hit = await PressAtAsync(status, new Point(4, status.Bounds.Height / 2));
        Check("CAT-12 D83 a press on the status line closes the results; the focus stays in the search box",
            Within(hit, StatusRegion(dialog)) && !Overlay(dialog).IsVisible && editor.HighlightedResult == null && Focused(dialog) == search);

        await PressAtAsync(search);
        Check("CAT-12 fixture: a tap in the search box reopened the results", Overlay(dialog).IsVisible);
        hit = await PressAtAsync(Detail(dialog));
        Check("CAT-12 D83 a press on the detail pane closes the results",
            Within(hit, Detail(dialog)) && !Overlay(dialog).IsVisible && editor.HighlightedResult == null);

        // New results while the list is open: the top match is highlighted again (D85).
        await SearchAsync(dialog, editor, "radio");
        await PressAsync(dialog, Key.Down);
        Check("CAT-12 fixture: \"radio\" is open with its second row (Radio Köln AM) highlighted", Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == KolnAm);
        await SearchAsync(dialog, editor, "o");
        Check("CAT-12 D85 results applied while the list is open highlight their first row (\"o\": " +
              $"{editor.Results.FirstOrDefault()?.Name}), the list selection and the detail pane follow",
            Overlay(dialog).IsVisible && editor.Results.Count > 1 && editor.HighlightedResult == editor.Results[0]
            && list.SelectedItem == editor.Results[0] && DetailName(dialog) == editor.Results[0].Name);

        // M1: after a pick, walking the reopened list and closing it shows the picked station again.
        await SearchAsync(dialog, editor, "radio");
        await PressAsync(dialog, Key.Enter);
        Check("CAT-10 fixture: typing then Enter picked Radio Thessaloniki", editor.SelectedEntry == Thessaloniki && !Overlay(dialog).IsVisible);
        await PressAtAsync(search);
        await PressAsync(dialog, Key.Down);
        Check("CAT-10 fixture: the reopened list is on its second row, Radio Köln AM, and the detail pane shows it",
            Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == KolnAm && DetailName(dialog) == "Radio Köln AM");
        await PressAsync(dialog, Key.Escape);
        Check("CAT-10 D85 M1 closing the list clears the highlight, so the detail pane shows the picked station (Radio Thessaloniki), not the row it left",
            !Overlay(dialog).IsVisible && editor.HighlightedResult == null && list.SelectedItem == null
            && editor.DetailRow?.Entry == Thessaloniki && DetailName(dialog) == "Radio Thessaloniki" && Shows(Detail(dialog), "A regional station."));
        dialog.Close();
        await PumpAsync();

        // A one-station catalog: the unfiltered footer is singular.
        rig.Catalog.Result = Loaded([Melodia]);
        (dialog, editor) = await OpenAddAsync(rig);
        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check("CAT-14 D85 P10 the unfiltered footer of a one-station catalog reads \"1 station\"",
            Overlay(dialog).IsVisible && Shows(Overlay(dialog), "1 station") && editor.TotalCountText == "1 station");
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-12 D85 P3: Escape closes the innermost layer first ───

    private static async Task EscapeClosesTheInnermostLayer()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var stations = rig.Settings.Stations.Count;
        var (dialog, editor) = await OpenAddAsync(rig);
        var cancel = ButtonWithText(dialog, "Cancel");

        // Focus in a text field closes the results (D83), so the form's other stop is its buttons: Shift+Tab from the search
        // box wraps to Cancel, and the results stay open.
        await SearchAsync(dialog, editor, "radio");
        dialog.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.None, null);
        dialog.KeyRelease(Key.Tab, RawInputModifiers.Shift, PhysicalKey.None, null);
        await PumpAsync();
        Layout(dialog);
        Check("CAT-12 fixture: the results are open with the focus on the form's Cancel button", Overlay(dialog).IsVisible && Focused(dialog) == cancel && editor.HighlightedResult != null);
        await PressAsync(dialog, Key.Escape);
        Layout(dialog);
        Check("CAT-12 D85 Escape with the focus in the form (its Cancel button) closes only the results: the dialog stays open, the focus stays, nothing added",
            dialog.IsVisible && !Overlay(dialog).IsVisible && editor.HighlightedResult == null && Focused(dialog) == cancel
            && rig.Settings.Stations.Count == stations);
        await PressAsync(dialog, Key.Escape);
        Check("CAT-12 §5.5 the next Escape (results closed) cancels the dialog: nothing added",
            await WaitAsync(() => !dialog.IsVisible) && editor.Result == EditorResult.Cancelled && rig.Settings.Stations.Count == stations);

        // An open filter drop-down takes the Escape first.
        (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "radio");
        var city = ByName<ComboBox>(dialog, "City filter");
        await ClickAsync(city);
        Check("CAT-12 fixture: the results are open and the City drop-down is open over them", Overlay(dialog).IsVisible && city.IsDropDownOpen);
        await PressAsync(dialog, Key.Escape);
        Layout(dialog);
        Check("CAT-12 D85 with a filter drop-down open, Escape closes only the drop-down: the results stay open on their highlight, the dialog open, the filter still All",
            !city.IsDropDownOpen && Overlay(dialog).IsVisible && editor.HighlightedResult == editor.Results[0] && dialog.IsVisible
            && editor.SelectedCity == CatalogFilterOption.All);
        await PressAsync(dialog, Key.Escape);
        Layout(dialog);
        Check("CAT-12 D85 the second Escape closes the results; the dialog stays open",
            !Overlay(dialog).IsVisible && editor.HighlightedResult == null && dialog.IsVisible);
        await PressAsync(dialog, Key.Escape);
        Check("CAT-12 D85 the third Escape cancels the dialog: nothing added",
            await WaitAsync(() => !dialog.IsVisible) && editor.Result == EditorResult.Cancelled && rig.Settings.Stations.Count == stations);
    }

    // ─── CAT-12 D83: the title-bar close is Cancel ───

    private static async Task TitleBarCloseIsCancel()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var stations = rig.Settings.Stations.Count;
        var (dialog, editor) = await OpenAddAsync(rig);
        var before = editor.Results;
        var closeRequests = 0;
        editor.CloseRequested += (_, _) => closeRequests++;
        var search = ByName<TextBox>(dialog, "Search stations");
        search.Focus();
        dialog.KeyTextInput("melodia");
        Check("CAT-12 fixture: a search is scheduled (debounced) and not applied yet", !editor.PendingSearch.IsCompleted && ReferenceEquals(editor.Results, before));

        dialog.Close(); // the title bar's close button: no command runs first
        Check("CAT-12 D83 a title-bar close ends as Cancel: the dialog closed, CancelCommand ran (one CloseRequested), Result Cancelled, nothing added",
            await WaitAsync(() => !dialog.IsVisible) && closeRequests == 1 && editor.Result == EditorResult.Cancelled && rig.Settings.Stations.Count == stations
            && rig.OnDisk().Stations.Count == stations);
        Check("CAT-12 D83 … and the search scheduled before it is never applied: PendingSearch completes, Results unchanged, the overlay never opened",
            await CompletesAsync(editor.PendingSearch) && ReferenceEquals(editor.Results, before) && !editor.IsResultsOpen);
        // Past the 200 ms debounce: a search that was not cancelled would land now.
        await Task.Delay(StationEditorViewModel.DefaultSearchDelay * 2);
        await PumpAsync();
        Check("CAT-12 D83 … still unchanged after the debounce delay has passed twice over", ReferenceEquals(editor.Results, before) && !editor.IsResultsOpen);
    }

    // ─── CAT-10 D85 P9: Page Down / Page Up in the search box scroll the detail pane ───

    private static async Task DetailPaneScrollsFromTheSearchBox()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        dialog.Width = 620;
        await SearchAsync(dialog, editor, "kosmos");
        await PressAsync(dialog, Key.Enter);
        Layout(dialog);
        var search = ByName<TextBox>(dialog, "Search stations");
        var scroll = Find<ScrollViewer>(Detail(dialog)).Single();
        Check("CAT-10 fixture: Kosmos 93.6 picked at 620 wide; its notes overflow the detail pane (extent taller than the viewport)",
            editor.SelectedEntry == Kosmos && !Overlay(dialog).IsVisible && scroll.Extent.Height > scroll.Viewport.Height + 1 && scroll.Offset.Y == 0);

        // P9: no text is drawn under the scroll bar, at rest or expanded. At rest (no pointer over it) Fluent draws only the
        // thumb, narrowed at the bar's right edge; a pointer over the bar expands it to its full width, over the content.
        var bar = Find<ScrollBar>(scroll).Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = Find<Thumb>(bar).Single();
        Rect Drawn() => new Rect(thumb.Bounds.Size).TransformToAABB(thumb.TransformToVisual(dialog)!.Value);
        var rest = Drawn();
        var texts = Find<TextBlock>(scroll).Where(t => t.IsEffectivelyVisible && DisplayText(t).Length > 0 && !bar.IsVisualAncestorOf(t)).ToList();
        var right = texts.Max(t => t.TranslatePoint(new Point(t.Bounds.Width, 0), dialog)!.Value.X);
        var barBox = new Rect(bar.TranslatePoint(default, dialog)!.Value, bar.Bounds.Size);
        Check($"CAT-10 D85 P9 every text in the detail content ends left of the scroll bar at rest (texts end at x={right:F1}, the thumb starts at x={rest.X:F1})",
            !bar.IsExpanded && bar.IsEffectivelyVisible && thumb.IsEffectivelyVisible && rest.Width > 0 && texts.Count > 0 && right <= rest.X + 0.5);
        dialog.MouseMove(barBox.Center, RawInputModifiers.None);
        // The thumb widens over a few frames: render until it holds still.
        var (last, still) = (-1.0, 0);
        var expanded = await WaitAsync(() =>
        {
            Layout(dialog);
            var width = Drawn().Width;
            (still, last) = (Math.Abs(width - last) < 0.01 ? still + 1 : 0, width);
            return bar.IsExpanded && still >= 3;
        });
        var wide = Drawn();
        Console.WriteLine($"  detail pane: texts end at x={right:F1}; the thumb at rest is drawn at {Show(rest)}, expanded at {Show(wide)}; the expanded bar spans x={barBox.X:F1}–{barBox.Right:F1} ({barBox.Width:F1} px)");
        Check($"CAT-10 D85 P9 under the pointer the scroll bar expands (the thumb {rest.Width:F1} → {wide.Width:F1} px, the bar {barBox.Width:F1} px), and every text still ends left of it " +
              $"(texts end at x={right:F1}, the expanded bar starts at x={barBox.X:F1}, its thumb at x={wide.X:F1})",
            expanded && bar.IsPointerOver && wide.Width > rest.Width * 4 && right <= Math.Min(wide.X, barBox.X) + 0.5);
        Png(dialog, "catalog-d85-detail-scrollbar-hover");

        search.Focus();
        var handled = new List<(Key Key, bool Handled)>();
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add((e.Key, e.Handled)), RoutingStrategies.Bubble, handledEventsToo: true);
        var page = Math.Min(scroll.Viewport.Height, scroll.Extent.Height - scroll.Viewport.Height);
        await PressAsync(dialog, Key.PageDown);
        Layout(dialog);
        var down = scroll.Offset.Y;
        Check($"CAT-10 D85 P9 Page Down in the search box scrolls the detail pane down one page ({down:F1} of {page:F1} px), handled, the focus still in the search box",
            Math.Abs(down - page) < 1 && handled.SequenceEqual([(Key.PageDown, true)]) && Focused(dialog) == search);
        await PressAsync(dialog, Key.PageUp);
        Layout(dialog);
        Check("CAT-10 D85 P9 Page Up scrolls it back to the top, handled, the focus still in the search box",
            scroll.Offset.Y == 0 && handled.Count == 2 && handled[1] == (Key.PageUp, true) && Focused(dialog) == search && search.Text == "kosmos");
        dialog.Close();
        await PumpAsync();
    }
}
