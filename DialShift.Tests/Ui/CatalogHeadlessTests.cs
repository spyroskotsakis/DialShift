using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
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

    /// <summary>The status line, found by its declared automation name (a text block's peer announces its text instead).</summary>
    private static TextBlock StatusLine(Window dialog) => Find<TextBlock>(dialog).Single(t => AutomationProperties.GetName(t) == "Catalog status");

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
        Check("CAT-14 §5.5 the status line declares the automation name \"Catalog status\"; Avalonia's text-block peer announces its text (\"7 stations · …\")",
            AutomationProperties.GetName(StatusLine(dialog)) == "Catalog status" && AccessibleName(StatusLine(dialog)) == StatusLine(dialog).Text);
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
        Check("CAT-14 §5.5 each result is announced by its CatalogResultRow.AutomationName (\"Radio Thessaloniki, Thessaloniki · 94.5 FM · Greece\", …)",
            Items(dialog).Select(AccessibleName).SequenceEqual(rows.Select(r => r.AutomationName)) && AccessibleName(Items(dialog)[0]) == "Radio Thessaloniki, Thessaloniki · 94.5 FM · Greece");

        var title = Find<TextBlock>(Items(dialog)[0]).Single(t => t.Classes.Contains("resultTitle"));
        Check("[quirk] CAT-14 a result's first line reads \"Radio Thessaloniki  ·  Thessaloniki · 94.5 FM · Greece\" (two spaces each side of the dot): " +
            "the line breaks between the template's three Runs add a \" \" run on each side of \" · \" (5 inlines, not 3)",
            title.Inlines!.Count == 5 && DisplayText(title) == "Radio Thessaloniki  ·  Thessaloniki · 94.5 FM · Greece");

        await PressAsync(dialog, Key.Down);
        Check("CAT-12 Down highlights the first result (the list selection follows); the detail pane shows it, notes included",
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
        Check("CAT-10 the detail pane shows the full long notes (wrapped, in a scroller) with \"4210 votes\", \"93.6 FM\", \"Public · World\"",
            Find<TextBlock>(Detail(dialog)).Any(t => t.Text == KosmosNotes && t.IsEffectivelyVisible && t.TextWrapping != Avalonia.Media.TextWrapping.NoWrap)
            && Shows(Detail(dialog), "4210 votes") && Shows(Detail(dialog), "93.6 FM") && Shows(Detail(dialog), "Public · World"));
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

    // ─── CAT-12: Enter without a highlight saves; Escape closes ───

    private static async Task EnterAndEscape()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "melodia");
        Check("CAT-12 fixture: results open, nothing highlighted", Overlay(dialog).IsVisible && editor.HighlightedResult == null);
        await PressAsync(dialog, Key.Enter);
        Check("CAT-12 BHV-52 Enter with the results open but nothing highlighted is Save: \"Give this station a name.\", the name field focused, the results closed",
            dialog.IsVisible && VisibleError(dialog)?.Text == "Give this station a name." && Focused(dialog) == Field(dialog, StationEditorViewModel.NameLabel)
            && !Overlay(dialog).IsVisible && rig.Settings.Stations.Count == 3);

        var search = ByName<TextBox>(dialog, "Search stations");
        search.Focus();
        await SearchAsync(dialog, editor, "radio");
        await PressAsync(dialog, Key.Down);
        Check("CAT-12 fixture: results open with a highlight", Overlay(dialog).IsVisible && editor.HighlightedResult != null);
        await PressAsync(dialog, Key.Escape);
        Check("CAT-12 §5.5 Escape is never the catalog's: with the results open and a highlight it closes the dialog, nothing added, nothing saved",
            !dialog.IsVisible && editor.Result == EditorResult.Cancelled && rig.Settings.Stations.Count == 3 && !rig.SavedToDisk);
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
        Check("CAT-13 no match: the overlay shows \"No stations match — adjust filters or enter the stream manually\", no list, no count",
            Overlay(dialog).IsVisible && Shows(Overlay(dialog), UiText.CatalogNoMatch) && editor.HasNoMatches
            && !Find<ListBox>(dialog).Single().IsVisible && !Shows(dialog, "0 matches"));
        Png(dialog, "catalog-add-no-match");
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
        Check($"CAT-17 real catalog: the status line reads \"{count} stations · catalog updated {real.GeneratedUtc?.UtcDateTime:yyyy-MM-dd}\"",
            real.GeneratedUtc is { } generated && StatusLine(dialog).Text == $"{count} stations · catalog updated {generated.UtcDateTime:yyyy-MM-dd}");
        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check($"CAT-13 CAT-12 real catalog: Down opens the 50 most-voted with the footer \"Showing 50 of {count} matches\", the first highlighted",
            Overlay(dialog).IsVisible && Shows(dialog, $"Showing 50 of {count} matches") && editor.HighlightedResult == editor.Results[0]);
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
