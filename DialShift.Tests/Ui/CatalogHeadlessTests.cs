using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DialShift.App.Services;
using DialShift.App.ViewModels;
using DialShift.App.Views.Dialogs;
using DialShift.Core.Catalog;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.CatalogUiFixtures;
using static DialShift.Tests.Ui.Headless;
using static DialShift.Tests.Ui.HeadlessUiTests;

namespace DialShift.Tests.Ui;

/// <summary>
/// The Add dialog's station catalog through the real <see cref="StationEditorDialog"/> on the headless platform (brief 3,
/// docs/catalog-contracts.md §5.5, §8): CAT-10 (a keyboard or pointer pick fills the real text boxes, the detail pane, the
/// logos), CAT-11 (Edit mode is today's form), CAT-12 (focus, Up/Down/Enter/Escape, Tab order), CAT-13 (loading, no match,
/// no catalog), CAT-14 (automation names, nothing clipped at 780×650 and 1366×768 with the dialog at 680 and 620 wide, the
/// dialog's height, fast typing) and CAT-17 (the status line, with the real catalog). Part of the HeadlessUi suite. Every
/// dialog opens through "+  Add station" or a row's Edit button, so the app's own wiring and 200 ms debounce are used.
/// </summary>
internal static class CatalogHeadlessTests
{
    private static readonly string[] AddTabOrder =
    [
        "Search stations", "Country filter", "City filter", "Type filter", "Genre filter", "Language filter", "Clear search and filters",
        StationEditorViewModel.NameLabel, StationEditorViewModel.TagLabel, StationEditorViewModel.UrlLabel, "Save", "Cancel"
    ];

    public static async Task RunAsync()
    {
        await Headless.RunAsync(ClipProbeMeasuresInlineRuns);
        await Headless.RunAsync(KeyboardPickAndSave);
        await Headless.RunAsync(PointerLogosAndMonograms);
        await Headless.RunAsync(EnterAndEscape);
        await Headless.RunAsync(EnterAndEscapeBeforeTheDebounce);
        await Headless.RunAsync(LoadingAndNoCatalog);
        await Headless.RunAsync(EditModeIsThePlainForm);
        await Headless.RunAsync(() => NothingClipped(780, 650));
        await Headless.RunAsync(() => NothingClipped(1366, 768));
        await Headless.RunAsync(RealCatalog);
    }

    // ─── helpers ───

    private static async Task<(StationEditorDialog Dialog, StationEditorViewModel Editor)> OpenAddAsync(UiRig rig, bool settle = true)
    {
        var count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(rig.Window!, "+  Add station"));
        var dialog = await WaitForWindowAsync<StationEditorDialog>(count);
        var editor = (StationEditorViewModel)dialog.DataContext!;
        if (settle && !await WaitAsync(() => editor.PendingSearch.IsCompleted))
            throw new TimeoutException("The Add dialog's first catalog search did not land.");
        Layout(dialog);
        return (dialog, editor);
    }

    /// <summary>Types into the search box and waits for that text's results (past the 200 ms debounce).</summary>
    private static async Task SearchAsync(StationEditorDialog dialog, StationEditorViewModel editor, string text)
    {
        await TypeAsync(ByName<TextBox>(dialog, "Search stations"), text);
        if (!await WaitAsync(() => editor.SearchText == text && editor.PendingSearch.IsCompleted))
            throw new TimeoutException($"The search for \"{text}\" did not land.");
        Layout(dialog);
    }

    private static Border Overlay(Window dialog) => Find<Border>(dialog).Single(b => b.Name == "ResultsOverlay");

    private static Border Detail(Window dialog) => Find<Border>(dialog).Single(b => AccessibleName(b) == "Station details");

    /// <summary>The status line's container: it carries the automation name "Catalog status" (a text block's peer would announce its text instead).</summary>
    private static Border StatusRegion(Window dialog) => Find<Border>(dialog).Single(b => AutomationProperties.GetName(b) == "Catalog status");

    /// <summary>The status line's text, inside <see cref="StatusRegion"/>.</summary>
    private static TextBlock StatusLine(Window dialog) => (TextBlock)StatusRegion(dialog).Child!;

    private static List<ListBoxItem> Items(Window dialog) => [.. Find<ListBoxItem>(dialog).Where(i => i.IsEffectivelyVisible)];

    private static ListBoxItem ItemOf(Window dialog, StationCatalogEntry entry) =>
        Items(dialog).Single(i => i.DataContext is CatalogResultRow row && Equals(row.Entry, entry));

    private static string FocusedName(Window dialog) => Focused(dialog) is Control c ? AccessibleName(c) : "(nothing)";

    private static TextBox Field(Window dialog, string label) => ByName<TextBox>(dialog, label);

    /// <summary>A 48×48 solid logo, made on the headless platform (Skia).</summary>
    private static WriteableBitmap SolidLogo(uint bgra)
    {
        var bitmap = new WriteableBitmap(new PixelSize(48, 48), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var row = Enumerable.Repeat(unchecked((int)bgra), buffer.RowBytes / 4).ToArray();
        for (var y = 0; y < buffer.Size.Height; y++) Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        return bitmap;
    }

    private static void Png(Window dialog, string name)
    {
        Layout(dialog);
        Console.WriteLine("  PNG: " + Screenshot(dialog, name));
    }

    // ─── the clipping probe measures inline runs (negative control for the extended helper) ───

    private static Task ClipProbeMeasuresInlineRuns()
    {
        static TextBlock Runs(double width, bool trim)
        {
            var block = new TextBlock { Width = width, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            if (trim) block.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            block.Inlines!.Add(new Run("A result title") { FontWeight = Avalonia.Media.FontWeight.SemiBold });
            block.Inlines.Add(new Run(" · "));
            block.Inlines.Add(new Run("City · 101.5 FM · Country") { FontSize = 12 });
            return block;
        }
        var probe = new Window { Width = 600, Height = 160, Content = new StackPanel { Children = { Runs(80, trim: false), Runs(80, trim: true), Runs(560, trim: false) } } };
        probe.Show();
        Layout(probe);
        var clipped = ClippedTexts(probe);
        Check("CAT-14 the clipping probe measures text built from inline runs: an overflowing and a trimmed one are flagged, one that fits is not (negative control)",
            clipped.Count == 2 && clipped.All(c => c.StartsWith("\"A result title · City · 101.5 FM · Country\"", StringComparison.Ordinal))
            && clipped.Count(c => c.Contains("(trimmed)", StringComparison.Ordinal)) == 1);
        var allowed = ClippedTexts(probe, t => t.TextTrimming != Avalonia.Media.TextTrimming.None);
        Check("CAT-14 an allowed ellipsis is not reported, while an overflow without one still is (negative control)",
            allowed.Count == 1 && !allowed[0].Contains("(trimmed)", StringComparison.Ordinal));
        probe.Close();
        return Task.CompletedTask;
    }

    // ─── CAT-12 / CAT-10 / CAT-14: keyboard walk, pick, save ───

    private static async Task KeyboardPickAndSave()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        var search = ByName<TextBox>(dialog, "Search stations");

        Check("CAT-12 D68 the Add dialog opens with \"Search stations\" focused; its placeholder is §5.3's",
            Focused(dialog) == search && search.PlaceholderText == UiText.SearchPlaceholder);
        Check("CAT-17 the status line (\"Catalog status\") reads \"7 stations · catalog updated 2026-09-25\"",
            StatusLine(dialog).Text == "7 stations · catalog updated 2026-09-25");
        var statusPeer = ControlAutomationPeer.CreatePeerForElement(StatusRegion(dialog));
        var linePeer = ControlAutomationPeer.CreatePeerForElement(StatusLine(dialog));
        Check("CAT-14 §5.5 the status line is announced as \"Catalog status\": its container's peer is a Group in the control view named " +
            "\"Catalog status\" with the line as its help text, and the line inside announces its text (\"7 stations · …\")",
            statusPeer.GetName() == "Catalog status" && statusPeer.GetHelpText() == StatusLine(dialog).Text && statusPeer.IsControlElement()
            && statusPeer.GetAutomationControlType() == AutomationControlType.Group && statusPeer.GetChildren().SequenceEqual([linePeer])
            && linePeer.GetName() == "7 stations · catalog updated 2026-09-25" && linePeer.GetAutomationControlType() == AutomationControlType.Text);
        Check("CAT-13 after the load the results stay closed (the 50 most-voted wait behind Down) and the detail pane shows its placeholder",
            !Overlay(dialog).IsVisible && editor.Results.Count == 7 && Shows(dialog, UiText.CatalogDetailPlaceholder));
        Check("CAT-14 §5.5 automation names: the search box, the five filters, Clear, the status line, the detail pane, the three fields",
            new[] { "Country filter", "City filter", "Type filter", "Genre filter", "Language filter" }.All(n => ByName<ComboBox>(dialog, n).IsEffectivelyEnabled)
            && ByName<Button>(dialog, "Clear search and filters").Content as string == "Clear" && ByName<Border>(dialog, "Station details") != null
            && Field(dialog, StationEditorViewModel.NameLabel) != null && Field(dialog, StationEditorViewModel.TagLabel) != null && Field(dialog, StationEditorViewModel.UrlLabel) != null);
        Check("CAT-14 QG-03 every input, picker and button in the Add dialog has a spoken name", Unnamed(dialog).Count == 0);
        Check("CAT-12 §5.5 each filter has a visible field label: Country, City, Type, Genre, Language; the manual-entry separator shows",
            new[] { "Country", "City", "Type", "Genre", "Language" }.All(l => Shows(dialog, l)) && Shows(dialog, UiText.ManualEntrySeparator));

        var named = AddTabOrder.Select(n => n switch
        {
            "Save" or "Cancel" => (Control)ButtonWithText(dialog, n),
            _ => Find<Control>(dialog).Single(c => c.IsEffectivelyVisible && c.TemplatedParent == null && c is TextBox or ComboBox or Button && AccessibleName(c) == n)
        }).ToList();
        Check("CAT-12 §5.5 TabIndex: search 0, Country 1 … Language 5, Clear 6, Name 7, Description 8, Stream URL 9, Save 10, Cancel 11",
            named.Select(c => c.TabIndex).SequenceEqual(Enumerable.Range(0, AddTabOrder.Length)));
        var walked = new List<string>();
        for (var i = 1; i < AddTabOrder.Length; i++)
        {
            await PressAsync(dialog, Key.Tab);
            walked.Add(FocusedName(dialog));
        }
        Console.WriteLine("  Tab walk: " + string.Join(" → ", walked));
        Check("CAT-12 real Tab presses walk search → the five filters → Clear → Name → Description → Stream URL → Save → Cancel (the results list is no tab stop)",
            walked.SequenceEqual(AddTabOrder.Skip(1)));

        search.Focus();
        await SearchAsync(dialog, editor, "radio");
        var list = ByName<ListBox>(dialog, "Station catalog results");
        var rows = editor.Results;
        Check("CAT-12 typing opens the results: \"radio\" lists three rows, the footer says \"3 matches\", focus stays in the search box",
            Overlay(dialog).IsVisible && rows.Select(r => r.Entry).SequenceEqual([Thessaloniki, KolnAm, Shortwave]) && Shows(dialog, "3 matches") && Focused(dialog) == search);
        var footer = ControlAutomationPeer.CreatePeerForElement(Find<TextBlock>(Overlay(dialog)).Single(t => t.Text == "3 matches"));
        Check("CAT-14 the results footer (\"3 matches\") is a polite live region, so the match count is announced as it changes",
            footer.GetName() == "3 matches" && footer.GetLiveSetting() == AutomationLiveSetting.Polite);
        Check("CAT-14 §5.5 each result is announced by its CatalogResultRow.AutomationName (\"Radio Thessaloniki, Thessaloniki · 94.5 FM · Greece\", …)",
            Items(dialog).Select(AccessibleName).SequenceEqual(rows.Select(r => r.AutomationName)) && AccessibleName(Items(dialog)[0]) == "Radio Thessaloniki, Thessaloniki · 94.5 FM · Greece");

        var title = Find<TextBlock>(Items(dialog)[0]).Single(t => t.Classes.Contains("resultTitle"));
        Check("CAT-14 D85 a result's first line reads \"Radio Thessaloniki · Thessaloniki · Greece\" (one space each side of the dot): " +
            "the template's three Runs are its only inlines (no whitespace runs between them), the name semibold, the place 12 px",
            title.Inlines is [Run name, Run { Text: " · " }, Run place] && DisplayText(title) == "Radio Thessaloniki · Thessaloniki · Greece"
            && name.FontWeight == Avalonia.Media.FontWeight.SemiBold && place.FontSize == 12);
        Check("CAT-14 D85 the frequency (\"94.5 FM\") has its own column at the row's right",
            Find<TextBlock>(Items(dialog)[0]).Single(t => t.Classes.Contains("resultFrequency")) is { IsEffectivelyVisible: true, Text: "94.5 FM" } frequency
            && frequency.Bounds.X > title.Bounds.X);

        Check("CAT-12 D85 typing opens the results on the first one, highlighted (the list selection follows); the detail pane shows it, notes included",
            editor.HighlightedResult == rows[0] && list.SelectedItem == rows[0] && Focused(dialog) == search
            && Detail(dialog).IsEffectivelyVisible && Shows(Detail(dialog), "Radio Thessaloniki") && Shows(Detail(dialog), "A regional station."));
        await PressAsync(dialog, Key.Down);
        Check("CAT-12 Down again → the second result (Radio Köln AM, \"1593 kHz\" in the detail pane)",
            editor.HighlightedResult == rows[1] && list.SelectedItem == rows[1] && Shows(Detail(dialog), "1593 kHz") && Shows(Detail(dialog), "Talk on medium wave."));
        await PressAsync(dialog, Key.Down);
        await PressAsync(dialog, Key.Down);
        Check("CAT-12 Down on the last result stays on it (no wrap)", editor.HighlightedResult == rows[2]);
        await PressAsync(dialog, Key.Up);
        await PressAsync(dialog, Key.Up);
        await PressAsync(dialog, Key.Up);
        Check("CAT-12 Up walks back and stops at the first result", editor.HighlightedResult == rows[0] && list.SelectedItem == rows[0]);
        Png(dialog, "catalog-add-highlighted");

        await PressAsync(dialog, Key.Enter);
        Check("CAT-10 CAT-12 Enter picks the highlighted result: the real text boxes hold its name, description and stream URL; the dialog stays open",
            dialog.IsVisible && Field(dialog, StationEditorViewModel.NameLabel).Text == "Radio Thessaloniki"
            && Field(dialog, StationEditorViewModel.TagLabel).Text == "Commercial" && Field(dialog, StationEditorViewModel.UrlLabel).Text == "https://streams.example.org/thessaloniki");
        Check("CAT-10 after the pick the results close and the detail pane keeps the picked station, its notes and chips (\"94.5 FM\", \"Greek\", \"50 votes\")",
            !Overlay(dialog).IsVisible && editor.SelectedEntry == Thessaloniki && Shows(Detail(dialog), "A regional station.")
            && Shows(Detail(dialog), "94.5 FM") && Shows(Detail(dialog), "Greek") && Shows(Detail(dialog), "50 votes") && Shows(Detail(dialog), "Thessaloniki, Central Macedonia, Greece"));
        Png(dialog, "catalog-add-picked");

        await PressAsync(dialog, Key.Enter);
        Check("CAT-12 BHV-52 Enter with the results closed saves as before: dialog closed, \"Radio Thessaloniki\" listed",
            await WaitAsync(() => !dialog.IsVisible && Shows(rig.Window!, "Radio Thessaloniki")) && Shows(rig.Window!, "04  SAVED FREQUENCIES"));
        Check("CAT-10 D73 the saved station carries the entry's notes, on disk",
            rig.OnDisk().Stations.Any(s => s is { Name: "Radio Thessaloniki", Tag: "Commercial", Url: "https://streams.example.org/thessaloniki", Notes: "A regional station." }));

        // Fast typing in the real dialog: every keystroke re-searches after the debounce; only the final text's results show.
        (dialog, editor) = await OpenAddAsync(rig);
        search = ByName<TextBox>(dialog, "Search stations");
        search.Focus();
        // No pumping between the keystrokes: a result can only land through the UI thread, which this loop keeps busy.
        foreach (var key in "melodia") dialog.KeyTextInput(key.ToString());
        Check("CAT-14 typing returns at once: seven keystrokes applied no result (the debounce and the search run off the UI thread)",
            search.Text == "melodia" && editor.SearchText == "melodia" && !editor.IsResultsOpen && editor.Results.Count == 7);
        Check("CAT-14 D72 … then only the final text's results land (Melodia 99.2, \"1 match\")",
            await WaitAsync(() => editor.PendingSearch.IsCompleted && editor.IsResultsOpen)
            && editor.Results.Select(r => r.Entry).SequenceEqual([Melodia]) && editor.TotalCountText == "1 match");
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-10: pointer, logos and monograms ───

    private static async Task PointerLogosAndMonograms()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        var logo = SolidLogo(0xFF78F2C2);
        rig.Catalog.Result = Loaded(Small);
        rig.Logos.Answer = url => url == Kosmos.Logo ? logo : null;
        var (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "o");
        Check("CAT-10 fixture: \"o\" lists Kosmos 93.6 and Radio Thessaloniki, among others", editor.Results.Any(r => r.Entry == Kosmos) && editor.Results.Any(r => r.Entry == Thessaloniki));

        var kosmos = ItemOf(dialog, Kosmos);
        var thessaloniki = ItemOf(dialog, Thessaloniki);
        Check("CAT-10 D66 a result whose logo loads shows it (the loader's bitmap)",
            Find<Image>(kosmos).Single() is { IsEffectivelyVisible: true } image && ReferenceEquals(image.Source, logo));
        Check("CAT-10 D66 a result whose logo fails shows its monogram (\"R\"), no image",
            Find<Image>(thessaloniki).Single() is { IsEffectivelyVisible: false } && Shows(thessaloniki, "R"));
        Check("CAT-10 every result logo went through the loader (no other fetch path)",
            rig.Logos.Requests.Contains(Kosmos.Logo) && rig.Logos.Requests.Contains(Thessaloniki.Logo) && rig.Logos.Requests.All(u => Small.Any(e => e.Logo == u && u.Length > 0)));

        var center = kosmos.TranslatePoint(new Point(kosmos.Bounds.Width / 2, kosmos.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseMove(center, RawInputModifiers.None);
        await PumpAsync();
        Layout(dialog);
        Check("CAT-10 §5.5 pointing at a result highlights it and fills the detail pane, with its logo",
            editor.HighlightedResult?.Entry == Kosmos && Shows(Detail(dialog), "Kosmos 93.6")
            && Find<Image>(Detail(dialog)).Single() is { IsEffectivelyVisible: true } detailImage && ReferenceEquals(detailImage.Source, logo) && editor.DetailLogo == logo);
        await ClickAsync(ItemOf(dialog, Kosmos));
        Check("CAT-10 §5.5 pressing a result picks it: the real text boxes are filled and the results close",
            Field(dialog, StationEditorViewModel.NameLabel).Text == "Kosmos 93.6" && Field(dialog, StationEditorViewModel.UrlLabel).Text == Kosmos.StreamUrl
            && Field(dialog, StationEditorViewModel.TagLabel).Text == "Public · World" && !Overlay(dialog).IsVisible && editor.SelectedEntry == Kosmos);
        Check("CAT-10 the detail pane shows the full long notes (wrapped, in a scroller) with \"4,210 votes\", \"93.6 FM\", \"Public · World\"",
            Find<TextBlock>(Detail(dialog)).Any(t => t.Text == KosmosNotes && t.IsEffectivelyVisible && t.TextWrapping != Avalonia.Media.TextWrapping.NoWrap)
            && Shows(Detail(dialog), "4,210 votes") && Shows(Detail(dialog), "93.6 FM") && Shows(Detail(dialog), "Public · World"));
        Png(dialog, "catalog-add-picked-long-notes");

        await SearchAsync(dialog, editor, "radio th");
        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check("CAT-10 D66 a highlighted result without a logo shows the monogram in the detail pane (no image)",
            editor.DetailRow?.Entry == Thessaloniki && editor.DetailLogo == null && Find<Image>(Detail(dialog)).Single() is { IsEffectivelyVisible: false }
            && Shows(Detail(dialog), "R"));
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-12: type and Enter picks the top match; Escape closes the list, then the dialog; Enter with the list closed saves ───

    private static async Task EnterAndEscape()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "melodia");
        Check("CAT-12 D85 fixture: results open on the top match, highlighted", Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == Melodia);
        await PressAsync(dialog, Key.Enter);
        Check("CAT-12 D85 typing then Enter picks the top match: the fields hold Melodia 99.2, the results closed, nothing highlighted, the dialog open",
            dialog.IsVisible && Field(dialog, StationEditorViewModel.NameLabel).Text == "Melodia 99.2" && editor.SelectedEntry == Melodia
            && !Overlay(dialog).IsVisible && editor.HighlightedResult == null && rig.Settings.Stations.Count == 3);

        var search = ByName<TextBox>(dialog, "Search stations");
        await ClickAsync(search);
        Layout(dialog);
        Check("CAT-12 D85 fixture: a click in the search box brings the results back, on the first row",
            Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == Melodia && Focused(dialog) == search);
        await PressAsync(dialog, Key.Escape);
        Layout(dialog);
        Check("CAT-12 D85 Escape with the results open closes only the results: the dialog stays open, the highlight dropped, the detail back on the pick",
            dialog.IsVisible && !Overlay(dialog).IsVisible && editor.HighlightedResult == null && editor.DetailRow?.Entry == Melodia);
        await PressAsync(dialog, Key.Enter);
        Check("CAT-12 BHV-52 Enter with the results closed is Save: the dialog closes and the picked Melodia 99.2 is saved",
            await WaitAsync(() => !dialog.IsVisible) && editor.Result == EditorResult.Saved && rig.Settings.Stations.Any(s => s.Name == "Melodia 99.2"));

        var saved = rig.Settings.Stations.Count;
        (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "radio");
        var country = ByName<ComboBox>(dialog, "Country filter");
        country.Focus();
        await PumpAsync();
        Check("CAT-12 fixture: results open with a highlight, the focus on the Country filter", Overlay(dialog).IsVisible && editor.HighlightedResult != null && Focused(dialog) == country);
        await PressAsync(dialog, Key.Escape);
        Layout(dialog);
        Check("CAT-12 D85 Escape closes the results wherever the focus is (the Country filter): the dialog stays open, the focus stays, nothing added",
            dialog.IsVisible && !Overlay(dialog).IsVisible && editor.HighlightedResult == null && Focused(dialog) == country && rig.Settings.Stations.Count == saved);
        await PressAsync(dialog, Key.Escape);
        Check("CAT-12 §5.5 Escape with the results closed cancels the dialog: nothing added",
            await WaitAsync(() => !dialog.IsVisible) && editor.Result == EditorResult.Cancelled && rig.Settings.Stations.Count == saved);
    }

    /// <summary>
    /// D87 items 6–9 against the app's real 200 ms debounce (QA M1, m2): Enter while a search is pending runs it at once and
    /// picks its first row, or nothing on no match, and never saves; a pick cancels a pending search; any close other than a
    /// pick (Escape, focus into a form field, a press outside) lets a search in flight apply without reopening the results;
    /// the overlay never opens without rows.
    /// </summary>
    private static async Task EnterAndEscapeBeforeTheDebounce()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        var search = ByName<TextBox>(dialog, "Search stations");
        var name = Field(dialog, StationEditorViewModel.NameLabel);
        var debounce = StationEditorViewModel.DefaultSearchDelay;
        var stations = rig.Settings.Stations.Count;
        Check($"CAT-12 D87 fixture: the dialog searches after a non-zero delay ({debounce.TotalMilliseconds:F0} ms)", debounce > TimeSpan.Zero);
        string State() =>
            $"name \"{name.Text}\", picked {editor.SelectedEntry?.Name ?? "none"}, open {editor.IsResultsOpen}, overlay {Overlay(dialog).IsVisible}, " +
            $"status \"{StatusLine(dialog).Text}\", error \"{VisibleError(dialog)?.Text}\", focus {FocusedName(dialog)}, pending {!editor.PendingSearch.IsCompleted}, " +
            $"rows {editor.Results.Count}, highlight {editor.HighlightedResult?.Entry.Name ?? "none"}";
        void Report(string step) => Console.WriteLine($"  {step}: {State()}");
        // Long enough for any search still in flight to land (or to show that it never will).
        async Task PastTheDelayAsync()
        {
            await Task.Delay(debounce * 3);
            await PumpAsync();
            Layout(dialog);
        }
        async Task ClearFormAsync()
        {
            await TypeAsync(name, "");
            await TypeAsync(Field(dialog, StationEditorViewModel.TagLabel), "");
            await TypeAsync(Field(dialog, StationEditorViewModel.UrlLabel), "");
        }

        // Item 6, no search pending: Enter after the results arrived picks their top match (D85, unchanged).
        await SearchAsync(dialog, editor, "thessaloniki");
        await PressAsync(dialog, Key.Enter);
        Report("Enter after the results arrived");
        Check("CAT-12 D85 D87 Enter after the results arrived (no search pending) picks the top match: Radio Thessaloniki, the results closed",
            name.Text == "Radio Thessaloniki" && editor.SelectedEntry == Thessaloniki && !Overlay(dialog).IsVisible && dialog.IsVisible);
        await ClearFormAsync();

        // Item 6, pending, no match: the stale row is not picked, Enter is handled and nothing is saved.
        await SearchAsync(dialog, editor, "melodia");
        await TypeAsync(search, "melodia xyz");
        var raced = !editor.PendingSearch.IsCompleted && editor.HighlightedResult?.Entry == Melodia && Overlay(dialog).IsVisible;
        await PressAsync(dialog, Key.Enter);
        Layout(dialog);
        Report("Enter at once after \"melodia xyz\"");
        Check("CAT-12 D87 fixture: Enter lands while the search for \"melodia xyz\" is pending, the stale Melodia 99.2 row highlighted", raced);
        Check("CAT-12 D87 item 6 Enter at once after typing \"melodia xyz\" runs the search now and picks nothing (not the stale Melodia 99.2; the earlier pick stays the selected entry): " +
              "the results closed, the status line says the no-match message, and Enter is handled: nothing saved, no name error, the focus stays in the search box",
            name.Text == "" && editor.SelectedEntry == Thessaloniki && !Overlay(dialog).IsVisible && editor.PendingSearch.IsCompleted && editor.HasNoMatches
            && StatusLine(dialog).Text == UiText.CatalogNoMatch && VisibleError(dialog) == null && Focused(dialog) == search
            && dialog.IsVisible && rig.Settings.Stations.Count == stations);
        await PastTheDelayAsync();
        Check("CAT-12 D87 item 6 past the delay nothing reopens the results or fills the fields", !Overlay(dialog).IsVisible && name.Text == "" && editor.SelectedEntry == Thessaloniki);
        await PressAsync(dialog, Key.Enter);
        Check("CAT-12 D87 item 4 Enter again, with no search pending and the overlay closed, is Save: the missing-name message in the name field",
            VisibleError(dialog)?.Text == "Give this station a name." && Focused(dialog) == name && dialog.IsVisible && rig.Settings.Stations.Count == stations);

        // Item 9(a): the overlay never opens without rows.
        editor.IsResultsOpen = true;
        Layout(dialog);
        Check("CAT-12 D87 item 9(a) asking to open the results with no rows leaves them closed", !editor.IsResultsOpen && !Overlay(dialog).IsVisible);

        // Item 6, pending, a match: the new text's first row, not the stale one.
        await SearchAsync(dialog, editor, "radio");
        await TypeAsync(search, "kosmos");
        raced = !editor.PendingSearch.IsCompleted && editor.HighlightedResult is { } stale && stale.Entry != Kosmos;
        await PressAsync(dialog, Key.Enter);
        Layout(dialog);
        Report("Enter at once after \"kosmos\"");
        Check("CAT-12 D87 fixture: Enter lands while the search for \"kosmos\" is pending, a stale \"radio\" row highlighted", raced);
        Check("CAT-12 D87 item 6 Enter at once after typing \"kosmos\" runs the search now and picks its first row, Kosmos 93.6; the results closed",
            name.Text == "Kosmos 93.6" && editor.SelectedEntry == Kosmos && !Overlay(dialog).IsVisible && dialog.IsVisible && editor.PendingSearch.IsCompleted);
        await PastTheDelayAsync();
        Check("CAT-12 D87 item 6 past the delay the pick stays and the results stay closed", !Overlay(dialog).IsVisible && name.Text == "Kosmos 93.6");

        // Item 7: a pointer pick cancels a pending search; the results stay as they were.
        await SearchAsync(dialog, editor, "radio");
        var radioRows = editor.Results;
        await TypeAsync(search, "melodia");
        raced = !editor.PendingSearch.IsCompleted && Overlay(dialog).IsVisible;
        await ClickAsync(ItemOf(dialog, KolnAm));
        Report("pointer pick while \"melodia\" is pending");
        Check("CAT-10 D87 fixture: the row is clicked while the search for \"melodia\" is pending", raced);
        Check("CAT-10 D87 item 7 a pointer pick while a search is pending picks the clicked row (Radio Köln AM) and cancels the search at once",
            name.Text == "Radio Köln AM" && editor.SelectedEntry == KolnAm && !Overlay(dialog).IsVisible && editor.PendingSearch.IsCompleted);
        await PastTheDelayAsync();
        Check("CAT-10 D87 item 7 past the delay the cancelled search never lands: the results stay as they were and closed",
            ReferenceEquals(editor.Results, radioRows) && !Overlay(dialog).IsVisible && name.Text == "Radio Köln AM");

        // Items 8 and 9(b): Escape, focus into a form field and a press outside each let the search in flight apply, closed.
        async Task CloseWhilePendingAsync(string how, Func<Task> close)
        {
            await SearchAsync(dialog, editor, "radio");
            await TypeAsync(search, "kosmos");
            var inFlight = !editor.PendingSearch.IsCompleted && Overlay(dialog).IsVisible;
            await close();
            Layout(dialog);
            Check($"CAT-12 D87 fixture: {how} lands while the search for \"kosmos\" is pending, with the results open", inFlight);
            Check($"CAT-12 D87 {how} closes the results; the dialog stays open", !Overlay(dialog).IsVisible && dialog.IsVisible);
            var landed = await CompletesAsync(editor.PendingSearch);
            await PastTheDelayAsync();
            Report($"after {how}, past the delay");
            Check($"CAT-12 D87 after {how} the search in flight still lands (Kosmos 93.6 its only row, the footer \"1 match\") without reopening the results, nothing highlighted",
                landed && editor.Results.Single().Entry == Kosmos && editor.TotalCountText == "1 match" && !editor.IsResultsOpen && !Overlay(dialog).IsVisible
                && editor.HighlightedResult == null);
        }
        await CloseWhilePendingAsync("item 8 Escape", () => PressAsync(dialog, Key.Escape));
        search.Focus();
        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check("CAT-12 D87 item 8 Down then opens the typed text's rows on Kosmos 93.6", Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == Kosmos);
        await CloseWhilePendingAsync("item 9(b) focus moving into the Description field", async () =>
        {
            Field(dialog, StationEditorViewModel.TagLabel).Focus();
            await PumpAsync();
        });
        await CloseWhilePendingAsync("item 9(b) a press outside (on the detail pane)", () => ClickAsync(Detail(dialog)));
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-13: loading, no catalog ───

    private static async Task LoadingAndNoCatalog()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        var window = rig.Window!;

        // Loading, then loaded: typing during the load is kept.
        var hold = new TaskCompletionSource();
        rig.Catalog.Hold = hold;
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig, settle: false);
        var linePeer = ControlAutomationPeer.CreatePeerForElement(StatusLine(dialog));
        var announced = new List<string>();
        linePeer.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.NameProperty) announced.Add($"{e.OldValue} → {e.NewValue}");
        };
        var search = ByName<TextBox>(dialog, "Search stations");
        Check("CAT-13 loading: status \"Loading the station catalog…\", the search box enabled and focused, the filters and Clear disabled",
            StatusLine(dialog).Text == UiText.CatalogLoading && search.IsEffectivelyEnabled && Focused(dialog) == search
            && !ByName<ComboBox>(dialog, "Country filter").IsEffectivelyEnabled && !ByName<Button>(dialog, "Clear search and filters").IsEffectivelyEnabled);
        Png(dialog, "catalog-add-loading");
        await TypeAsync(search, "kos");
        hold.SetResult();
        Check("CAT-13 when the load completes, the text typed meanwhile is searched and shown (Kosmos 93.6)",
            await WaitAsync(() => editor.PendingSearch.IsCompleted && editor.IsResultsOpen) && editor.Results.Single().Entry == Kosmos
            && ByName<ComboBox>(dialog, "Country filter").IsEffectivelyEnabled && Focused(dialog) == search);
        Check("CAT-14 the status line is a polite live region: loading → loaded raises one name change on its peer " +
            "(\"Loading the station catalog… → 7 stations · catalog updated 2026-09-25\") and the container's help text follows",
            linePeer.GetLiveSetting() == AutomationLiveSetting.Polite
            && announced.SequenceEqual([$"{UiText.CatalogLoading} → 7 stations · catalog updated 2026-09-25"])
            && ControlAutomationPeer.CreatePeerForElement(StatusRegion(dialog)).GetHelpText() == "7 stations · catalog updated 2026-09-25");
        dialog.Close();
        await PumpAsync();

        // Loading, then unavailable: the focus moves from the disabled search box to the name field.
        hold = new TaskCompletionSource();
        rig.Catalog.Hold = hold;
        rig.Catalog.Result = CatalogLoadResult.Unavailable("the file is missing.");
        (dialog, editor) = await OpenAddAsync(rig, settle: false);
        search = ByName<TextBox>(dialog, "Search stations");
        Check("CAT-13 fixture: the search box has the focus while loading", Focused(dialog) == search);
        hold.SetResult();
        Check("CAT-13 D68 the load turns out unavailable: the search box is disabled and the name field takes the focus",
            await WaitAsync(() => !editor.IsCatalogLoading) && await WaitAsync(() => Focused(dialog) == Field(dialog, StationEditorViewModel.NameLabel))
            && !search.IsEffectivelyEnabled);
        dialog.Close();
        await PumpAsync();

        // Unavailable from the start.
        rig.Catalog.Hold = null;
        (dialog, editor) = await OpenAddAsync(rig);
        search = ByName<TextBox>(dialog, "Search stations");
        var name = Field(dialog, StationEditorViewModel.NameLabel);
        Check("CAT-13 no catalog: the muted status \"Catalog unavailable — enter stream details manually\"",
            StatusLine(dialog) is { Text: UiText.CatalogUnavailable } status && status.Classes.Contains("hint"));
        Check("CAT-13 no catalog: search, filters and Clear disabled, no detail pane, no results, no \"No stations match\"",
            !search.IsEffectivelyEnabled && new[] { "Country filter", "City filter", "Type filter", "Genre filter", "Language filter" }.All(n => !ByName<ComboBox>(dialog, n).IsEffectivelyEnabled)
            && !ByName<Button>(dialog, "Clear search and filters").IsEffectivelyEnabled && !Detail(dialog).IsEffectivelyVisible
            && !Overlay(dialog).IsVisible && !Shows(dialog, UiText.CatalogNoMatch));
        Check("CAT-13 D68 no catalog: the name field has the focus, so typing starts the manual entry", Focused(dialog) == name);
        Png(dialog, "catalog-add-no-catalog");
        await TypeAsync(name, "Kosmos by hand");
        await TypeAsync(Field(dialog, StationEditorViewModel.UrlLabel), "https://radio.example.org/kosmos");
        await PressAsync(dialog, Key.Enter);
        Check("CAT-13 CAT-11 no catalog: manual entry and Enter save exactly as before (listed, on disk, no notes)",
            await WaitAsync(() => !dialog.IsVisible && Shows(window, "Kosmos by hand"))
            && rig.OnDisk().Stations.Any(s => s is { Name: "Kosmos by hand", Url: "https://radio.example.org/kosmos", Tag: "Internet radio", Notes: null }));

        // No match.
        rig.Catalog.Result = Loaded(Small);
        (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "zzz no such station");
        name = Field(dialog, StationEditorViewModel.NameLabel);
        Check("CAT-13 D87 no match: the results stay closed and the status line reads \"No stations match — adjust filters or enter the stream manually\" (its help text too), no count",
            !Overlay(dialog).IsVisible && editor.HasNoMatches && StatusLine(dialog) is { Text: UiText.CatalogNoMatch } line && line.Classes.Contains("noMatch")
            && AutomationProperties.GetHelpText(StatusRegion(dialog)) == UiText.CatalogNoMatch && !Shows(dialog, "0 matches"));
        Check("CAT-13 D87 no match: the manual form the message points to is in view", name.IsEffectivelyVisible && Field(dialog, StationEditorViewModel.UrlLabel).IsEffectivelyVisible);
        Png(dialog, "catalog-add-no-match");
        await PressAsync(dialog, Key.Down);
        Check("CAT-13 D87 no match: Down in the search box does nothing (no rows to open)", !editor.IsResultsOpen && !Overlay(dialog).IsVisible);
        await PressAsync(dialog, Key.Enter);
        Check("CAT-13 D87 no match: Enter goes to Save, which shows the missing-name message and focuses the name field",
            VisibleError(dialog)?.Text == "Give this station a name." && Focused(dialog) == name && dialog.IsVisible);
        await SearchAsync(dialog, editor, "kosmos");
        Check("CAT-13 D87 typing a matching word after no match opens the results again; the status line is the catalog status",
            Overlay(dialog).IsVisible && editor.Results.Count > 0 && StatusLine(dialog).Text == editor.CatalogStatusText
            && !StatusLine(dialog).Classes.Contains("noMatch"));
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-11: Edit mode ───

    private static async Task EditModeIsThePlainForm()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var calls = rig.Catalog.Calls;
        var count = OpenedWindows.Count;
        await ClickAsync(ByName<Button>(rig.Window!, "Edit Groove Salad"));
        var dialog = await WaitForWindowAsync<StationEditorDialog>(count);
        Layout(dialog);
        var name = Field(dialog, StationEditorViewModel.NameLabel);
        Check("CAT-11 D62 the Edit dialog never loads the catalog", rig.Catalog.Calls == calls);
        Check("CAT-11 the Edit dialog has no catalog panel: its only inputs are the three fields, no pickers, no detail pane, no status, no separator",
            Find<TextBox>(dialog).Where(t => t.IsEffectivelyVisible && t.TemplatedParent == null).Select(AccessibleName)
                .SequenceEqual([StationEditorViewModel.NameLabel, StationEditorViewModel.TagLabel, StationEditorViewModel.UrlLabel])
            && !Find<ComboBox>(dialog).Any(c => c.IsEffectivelyVisible) && !Detail(dialog).IsEffectivelyVisible
            && !StatusLine(dialog).IsEffectivelyVisible && !Shows(dialog, UiText.ManualEntrySeparator)
            && !Overlay(dialog).IsVisible);
        Check("CAT-11 BHV-52 the Edit dialog focuses the name field, prefilled", Focused(dialog) == name && name.Text == "Groove Salad");
        var walked = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            await PressAsync(dialog, Key.Tab);
            walked.Add(FocusedName(dialog));
        }
        Console.WriteLine("  Tab walk (Edit): " + string.Join(" → ", walked));
        Check("CAT-11 §5.5 Edit mode Tab order: Name → Description → Stream URL → Save → Cancel → Delete station",
            walked.SequenceEqual([StationEditorViewModel.TagLabel, StationEditorViewModel.UrlLabel, "Save", "Cancel", "Delete station"]));
        Check("CAT-14 QG-03 every input and button in the Edit dialog has a spoken name", Unnamed(dialog).Count == 0);
        name.Focus();
        await TypeAsync(name, "");
        await PressAsync(dialog, Key.Enter);
        Check("CAT-11 BHV-52 Edit mode: the missing-name message and focus are unchanged",
            VisibleError(dialog)?.Text == "Give this station a name." && Focused(dialog) == name && dialog.IsVisible);
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-14: nothing clipped, the dialog's height ───

    private static async Task NothingClipped(int width, int height)
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: width, height: height);
        rig.Catalog.Result = Loaded(Small);
        var size = $"{width}×{height}";
        foreach (var dialogWidth in new[] { 680, 620 })
        {
            // A fresh dialog per width, so "empty" is the state a user opens.
            var (dialog, editor) = await OpenAddAsync(rig);
            dialog.Width = dialogWidth;
            Layout(dialog);
            var closedHeight = dialog.ClientSize.Height;
            CheckUnclipped(dialog, $"window {size}, dialog {dialogWidth} wide, Add: empty");
            Png(dialog, $"catalog-{width}x{height}-{dialogWidth}-empty");
            Check($"CAT-14 §5.5 window {size}, dialog {dialogWidth} wide: the dialog is {dialog.ClientSize.Width:F0}×{closedHeight:F0}, no taller than 640 (fits 1366×768 with its title bar)",
                dialog.ClientSize.Width == dialogWidth && closedHeight <= 640);

            await SearchAsync(dialog, editor, "radio");
            await PressAsync(dialog, Key.Down);
            Layout(dialog);
            CheckUnclipped(dialog, $"window {size}, dialog {dialogWidth} wide, Add: results");
            Check($"CAT-14 §5.5 window {size}, dialog {dialogWidth} wide: opening the results never resizes the dialog",
                Overlay(dialog).IsVisible && dialog.ClientSize.Height == closedHeight);
            Png(dialog, $"catalog-{width}x{height}-{dialogWidth}-results");

            await PressAsync(dialog, Key.Enter);
            Layout(dialog);
            CheckUnclipped(dialog, $"window {size}, dialog {dialogWidth} wide, Add: picked");
            Check($"CAT-14 window {size}, dialog {dialogWidth} wide: picking (results closed, detail filled) never resizes the dialog",
                !Overlay(dialog).IsVisible && editor.SelectedEntry != null && dialog.ClientSize.Height == closedHeight);
            Png(dialog, $"catalog-{width}x{height}-{dialogWidth}-picked");

            await SearchAsync(dialog, editor, "kosmos");
            await PressAsync(dialog, Key.Down);
            await PressAsync(dialog, Key.Enter);
            Layout(dialog);
            CheckUnclipped(dialog, $"window {size}, dialog {dialogWidth} wide, Add: long notes");
            Check($"CAT-14 window {size}, dialog {dialogWidth} wide: long notes scroll inside the detail pane instead of growing the dialog",
                editor.SelectedEntry == Kosmos && dialog.ClientSize.Height == closedHeight);
            Png(dialog, $"catalog-{width}x{height}-{dialogWidth}-long-notes");

            await SearchAsync(dialog, editor, "zzz no such station");
            CheckUnclipped(dialog, $"window {size}, dialog {dialogWidth} wide, Add: no match");
            Check($"CAT-14 D87 window {size}, dialog {dialogWidth} wide, no match: the results are closed, the form shows, the message is one line on the status line, the height is unchanged",
                !Overlay(dialog).IsVisible && Field(dialog, StationEditorViewModel.NameLabel).IsEffectivelyVisible
                && StatusLine(dialog) is { Text: UiText.CatalogNoMatch } line && line.TextLayout.TextLines.Count == 1 && dialog.ClientSize.Height == closedHeight);
            Png(dialog, $"catalog-{width}x{height}-{dialogWidth}-no-match");
            dialog.Close();
            await PumpAsync();
        }

        rig.Catalog.Result = CatalogLoadResult.Unavailable("the file is missing.");
        var (unavailable, _) = await OpenAddAsync(rig);
        foreach (var dialogWidth in new[] { 680, 620 })
        {
            unavailable.Width = dialogWidth;
            Layout(unavailable);
            CheckUnclipped(unavailable, $"window {size}, dialog {dialogWidth} wide, Add: no catalog");
            Png(unavailable, $"catalog-{width}x{height}-{dialogWidth}-no-catalog");
        }
        unavailable.Close();
        await PumpAsync();

        var count = OpenedWindows.Count;
        await ClickAsync(ByName<Button>(rig.Window!, "Edit Groove Salad"));
        var edit = await WaitForWindowAsync<StationEditorDialog>(count);
        foreach (var dialogWidth in new[] { 680, 620 })
        {
            edit.Width = dialogWidth;
            Layout(edit);
            CheckUnclipped(edit, $"window {size}, dialog {dialogWidth} wide, Edit");
            Png(edit, $"catalog-{width}x{height}-{dialogWidth}-edit");
        }
        edit.Close();
        await PumpAsync();
    }

    /// <summary>
    /// Nothing in <paramref name="dialog"/> is cut. A result's first line ("Name · City · 101.5 FM · Country") is designed to
    /// end in an ellipsis when the form column is too narrow (the detail pane shows everything), so its trimming is allowed
    /// and printed, but the station name itself must fit (the fixture names are 18 characters at most).
    /// </summary>
    private static void CheckUnclipped(Window dialog, string state)
    {
        static bool IsResultTitle(TextBlock t) => t.Classes.Contains("resultTitle");
        var clipped = ClippedTexts(dialog, IsResultTitle);
        var examined = Find<TextBlock>(dialog).Count(t => t.IsEffectivelyVisible && DisplayText(t).Length > 0);
        if (clipped.Count > 0) Console.WriteLine($"  clipped ({state}): " + string.Join("; ", clipped));
        Check($"CAT-14 {state}: none of the {examined} visible text blocks (inline-run result titles included) is clipped; result titles may only end in their designed ellipsis",
            clipped.Count == 0);
        var titles = Find<TextBlock>(dialog).Where(t => t.IsEffectivelyVisible && IsResultTitle(t)).ToList();
        if (titles.Count == 0) return;
        var ellipsized = titles.Where(t => t.TextLayout.TextLines.Any(l => l.HasCollapsed)).Select(DisplayText).ToList();
        if (ellipsized.Count > 0) Console.WriteLine($"  ellipsized result titles ({state}, {titles[0].Bounds.Width:F0} px): " + string.Join("; ", ellipsized));
        var cutNames = titles.Where(t => NameWidth(t) > t.Bounds.Width + 0.5).Select(DisplayText).ToList();
        Check($"CAT-14 {state}: every result shows its whole station name ({titles.Count} rows; {ellipsized.Count} end in the designed ellipsis after the name)",
            cutNames.Count == 0);

        // The first run is the station name, in the row's semi-bold title font.
        static double NameWidth(TextBlock title)
        {
            var name = (Run)title.Inlines![0];
            var probe = new TextBlock { Text = name.Text, FontSize = name.FontSize, FontFamily = name.FontFamily, FontWeight = name.FontWeight, FontStyle = name.FontStyle };
            probe.Measure(Size.Infinity);
            return probe.DesiredSize.Width;
        }
    }

    // ─── CAT-17 / CAT-13 / CAT-14 with the real catalog ───

    private static async Task RealCatalog()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        var real = await RealAsync();
        rig.Catalog.Result = real;
        var count = real.Catalog.Entries.Count;
        var (dialog, editor) = await OpenAddAsync(rig);
        var stations = count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " stations";
        Check($"CAT-17 D85 real catalog: the status line reads \"{stations} · catalog updated {real.GeneratedUtc?.UtcDateTime:yyyy-MM-dd}\"",
            real.GeneratedUtc is { } generated && StatusLine(dialog).Text == $"{stations} · catalog updated {generated.UtcDateTime:yyyy-MM-dd}");
        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        var footer = $"Top 50 of {count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} stations by votes";
        Check($"CAT-13 CAT-12 D85 real catalog: Down opens the 50 most-voted with the footer \"{footer}\", the first highlighted",
            Overlay(dialog).IsVisible && Shows(dialog, footer) && editor.HighlightedResult == editor.Results[0]);
        var realized = Items(dialog).Count;
        Check($"CAT-14 §5.5 the results list is virtualized: {realized} of 50 rows are realized", realized is > 0 and < 50);
        // Catalog names reach 399 characters: a result title may end in an ellipsis by design (D44); nothing may be cut without one.
        var cut = ClippedTexts(dialog, t => t.Classes.Contains("resultTitle"));
        if (cut.Count > 0) Console.WriteLine("  clipped (real catalog): " + string.Join("; ", cut));
        Check("CAT-14 real catalog at 780×650: in the open results nothing is cut; result titles end in their designed ellipsis at most", cut.Count == 0);
        Png(dialog, "catalog-real-results");

        var longest = real.Catalog.Entries.MaxBy(e => e.Notes.Length)!;
        await SearchAsync(dialog, editor, longest.Name[..Math.Min(30, longest.Name.Length)]);
        for (var i = 0; i < 50 && editor.HighlightedResult?.Entry != longest; i++) await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check("CAT-10 fixture: the entry with the longest notes is found by its name and highlighted with Down", editor.HighlightedResult?.Entry == longest);
        Check($"CAT-10 real catalog: the longest notes ({longest.Notes.Length} characters) show whole in the detail pane without growing the dialog",
            Find<TextBlock>(Detail(dialog)).Any(t => t.Text == longest.Notes && t.IsEffectivelyVisible) && dialog.ClientSize.Height <= 640);
        // The detail name wraps to three lines, then ends in an ellipsis with the whole name in its tooltip (a 399-character name).
        var detailCut = ClippedTexts(dialog, t => t.Classes.Contains("resultTitle") || t.Classes.Contains("detailName"));
        if (detailCut.Count > 0) Console.WriteLine("  clipped (real catalog, detail): " + string.Join("; ", detailCut));
        Check("CAT-14 real catalog: the longest notes and name in the detail pane are wrapped or end in their designed ellipsis, never cut", detailCut.Count == 0);
        Png(dialog, "catalog-real-long-notes");
        dialog.Close();
        await PumpAsync();
    }
}
