using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DialShift.App.ViewModels;
using DialShift.Core.Catalog;
using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.CatalogUiFixtures;
using static DialShift.Tests.Ui.Headless;
using static DialShift.Tests.Ui.HeadlessUiTests;

namespace DialShift.Tests.Ui;

/// <summary>
/// The Add dialog's two 2026-09-29 features through the real <see cref="DialShift.App.Views.Dialogs.StationEditorDialog"/>
/// on the headless platform (spec <c>.claude/specs/quickplay-showmore.md</c>, acceptance-matrix §15): "Show more" paging
/// (SM-01..03, SM-06, SM-07) and the quick-play preview (QM-01..04, QM-06, QM-07). The preview plays through the
/// coordinator the dialog was given — never a second engine — so the routing checks read the journal of the rig's
/// coordinator, and the lifecycle checks (Cancel stops the preview, Save lets the caller adopt it without a restart) run
/// over the real coordinator and its fake engine.
/// </summary>
internal static partial class CatalogHeadlessTests
{
    /// <summary>A result row's quick-play button. Every row has one in its template, and the detail pane's twin carries the
    /// same spoken name while that row is shown, so the row's is found by its <c>inRow</c> class.</summary>
    private static Button RowPreview(Window dialog, string name) =>
        Find<Button>(dialog).Single(b => b.Classes.Contains("inRow") && b.IsEffectivelyVisible && AccessibleName(b) == name);

    /// <summary>The detail pane's quick-play button: the one in the pane, never the row class.</summary>
    private static Button DetailPreview(Window dialog) =>
        Find<Button>(Detail(dialog)).Single(b => b.Classes.Contains("preview") && b.IsEffectivelyVisible);

    /// <summary>"Show more", visible or not: the footer's control (the only one with that class).</summary>
    private static Button ShowMore(Window dialog) => Find<Button>(dialog).Single(b => b.Classes.Contains("showMore"));

    /// <summary>The two icons of a quick-play button, play first: the template's <c>Panel</c> holds them in that order.</summary>
    private static (bool Play, bool Stop) IconsOf(Button preview) =>
        (Find<PathIcon>(preview).First().IsEffectivelyVisible, Find<PathIcon>(preview).Last().IsEffectivelyVisible);

    // ─── SM-01..03, SM-06, SM-07: "Show more" pages the results without losing the place ───

    private static async Task ShowMorePagesTheResults()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        rig.Catalog.Result = Loaded(Numbered(120));
        var (dialog, editor) = await OpenAddAsync(rig);
        var list = Find<ListBox>(dialog).Single(l => l.Name == "ResultsList");

        await PressAsync(dialog, Key.Down); // opens the browse results on their first row
        Layout(dialog);
        var scroll = Find<ScrollViewer>(Overlay(dialog)).Single();
        Check("SM-01 SM-03 fixture: the browse (no text, no filters) results of 120 stations open on their first row, the first page of 50 shown",
            Overlay(dialog).IsVisible && editor.HighlightedResult == editor.Results[0] && editor.Results.Count == 50 && list.ItemCount == 50);
        Check("SM-01 SM-03 the footer counts the page against the whole query (\"Top 50 of 120 stations by votes\"), drawn, not clipped",
            editor.TotalCountText == "Top 50 of 120 stations by votes" && Shows(Overlay(dialog), editor.TotalCountText));
        Check("SM-01 SM-07 the footer offers \"Show more\": its label is the UiText one, its spoken name exactly \"ShowMore\", and it is visible",
            ShowMore(dialog) is { IsEffectivelyVisible: true, Content: var content } && Equals(content, UiText.ShowMore)
            && AccessibleName(ShowMore(dialog)) == UiText.ShowMoreAutomationName && UiText.ShowMoreAutomationName == "ShowMore");
        Check("SM-06 the \"Show more\" control at 780×650 is wholly inside the overlay card and inside the dialog's client area: shown, not clipped at the card's edge",
            InWindow(Overlay(dialog), dialog).Contains(InWindow(ShowMore(dialog), dialog))
            && InWindow(ShowMore(dialog), dialog).Right <= dialog.ClientSize.Width + 0.5
            && ShowMore(dialog).Bounds is { Width: >= 72, Height: > 0 });
        CheckUnclipped(dialog, "window 780×650, 50 of 120 rows, the footer with \"Show more\"");
        Png(dialog, "catalog-show-more-first-page");

        // Scrolled down the list, the next page is appended under the user's feet: the offset, the highlight and the list
        // selection all stay where they were, and the overlay stays open.
        scroll.Offset = new Vector(0, 300);
        Layout(dialog);
        var offset = scroll.Offset.Y;
        var first = editor.Results[0];
        var tenth = editor.Results[10];
        var highlighted = editor.HighlightedResult;
        Check($"SM-02 fixture: the list is scrolled down ({offset:F1} px of its {scroll.Extent.Height:F0} px), the first row highlighted",
            offset > 200 && !editor.IsPreviewing && highlighted == first);

        await ClickAsync(ShowMore(dialog));
        if (!await WaitAsync(() => editor.Results.Count == 100)) throw new TimeoutException("\"Show more\" never appended the second page.");
        Layout(dialog);
        Check("SM-02 activating \"Show more\" appends the next page to the rows already listed (50 → 100, the same row objects), the overlay stays open",
            editor.Results.Count == 100 && list.ItemCount == 100 && Overlay(dialog).IsVisible && editor.IsResultsOpen
            && ReferenceEquals(editor.Results[0], first) && ReferenceEquals(editor.Results[10], tenth));
        Check("SM-02 … and it never moves the user: the highlight and the list selection are the same row, the scroll offset is the one it was",
            ReferenceEquals(editor.HighlightedResult, highlighted) && ReferenceEquals(list.SelectedItem, highlighted)
            && Math.Abs(scroll.Offset.Y - offset) <= 0.5);
        Check("SM-03 the footer tracks the larger page (\"Top 100 of 120 stations by votes\") and the control is still offered",
            editor.TotalCountText == "Top 100 of 120 stations by votes" && editor.HasMoreResults && ShowMore(dialog).IsEffectivelyVisible);
        CheckUnclipped(dialog, "window 780×650, 100 of 120 rows, the footer with \"Show more\"");
        Png(dialog, "catalog-show-more-second-page");

        await ClickAsync(ShowMore(dialog));
        if (!await WaitAsync(() => editor.Results.Count == 120)) throw new TimeoutException("The last page of \"Show more\" never landed.");
        Layout(dialog);
        Check("SM-03 a second \"Show more\" brings the last page: all 120 rows listed and the footer says the whole catalog is shown (\"All 120 stations by votes\")",
            editor.Results.Count == 120 && list.ItemCount == 120 && editor.TotalCountText == "All 120 stations by votes" && !editor.HasMoreResults);
        Check("SM-03 once Results.Count == TotalCount the control is gone (hidden, not merely disabled), and the footer still counts the rows",
            !ShowMore(dialog).IsEffectivelyVisible && !editor.HasMoreResults && editor.TotalCountText.Length > 0);
        CheckUnclipped(dialog, "window 780×650, all 120 rows, the footer without \"Show more\"");
        Png(dialog, "catalog-show-more-last-page");

        // A new query is a new page: the cap is back to 50 whatever the list had grown to.
        await SearchAsync(dialog, editor, "station");
        Check("SM-04 a new query (here the text \"station\", which matches every name) resets the page to the top 50: \"Showing 50 of 120 matches\"",
            editor.Results.Count == 50 && list.ItemCount == 50 && editor.TotalCountText == "Showing 50 of 120 matches" && editor.HasMoreResults
            && ShowMore(dialog).IsEffectivelyVisible);
        await SearchAsync(dialog, editor, "Station 12");
        Check("SM-01 a query that matches fewer than a page (\"Station 12\": Station 12 and Station 120) shows the plain count and no \"Show more\" control at all",
            editor.Results.Count == 2 && editor.TotalCountText == "2 matches" && !editor.HasMoreResults && !ShowMore(dialog).IsEffectivelyVisible);
        CheckUnclipped(dialog, "window 780×650, 2 rows, no \"Show more\"");
        dialog.Close();
        await PumpAsync();
    }

    // ─── QM-01, QM-02, QM-03, QM-07: the row's and the detail pane's quick play ───

    private static async Task QuickPlayFromTheRowAndTheDetailPane()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "radio"); // Thessaloniki, Köln AM, Shortwave; the first is highlighted
        Layout(dialog);
        var row = ItemOf(dialog, Thessaloniki);
        var rowPlay = RowPreview(dialog, "Listen Radio Thessaloniki");
        var detailPlay = DetailPreview(dialog);
        var frequency = Find<TextBlock>(row).Single(t => t.Classes.Contains("resultFrequency"));
        var detailName = Find<TextBlock>(Detail(dialog)).Single(t => t.Classes.Contains("detailName") && t.IsEffectivelyVisible);

        Check("QM-01 the highlighted row offers a play button whose spoken name is \"Listen <name>\"; it shows the play icon",
            editor.HighlightedResult?.Entry == Thessaloniki && AccessibleName(rowPlay) == "Listen Radio Thessaloniki"
            && IconsOf(rowPlay) == (true, false) && ToolTip.GetTip(rowPlay) as string == "Listen Radio Thessaloniki");
        Check("QM-02 the detail pane of the same station carries its own play button with that name: two visible controls share it, one in a row and one in the pane",
            detailPlay.IsEffectivelyVisible && AccessibleName(detailPlay) == "Listen Radio Thessaloniki" && IconsOf(detailPlay) == (true, false)
            && !detailPlay.Classes.Contains("inRow") && Find<Button>(dialog).Count(b => b.IsEffectivelyVisible && AccessibleName(b) == "Listen Radio Thessaloniki") == 2);
        Check("QM-02 the detail pane's button sits in the pane's top-right corner: inside the pane, at its right edge, clear of the station's name",
            InWindow(Detail(dialog), dialog).Contains(InWindow(detailPlay, dialog))
            && InWindow(detailPlay, dialog).Right <= InWindow(Detail(dialog), dialog).Right + 0.5
            && !InWindow(detailPlay, dialog).Intersects(InWindow(detailName, dialog))
            && InWindow(detailPlay, dialog).Y < InWindow(detailName, dialog).Y);
        Check("QM-07 at 780×650 the row's play button is clear of the frequency column (the button ends before it starts, both on the row)",
            InWindow(row, dialog).Contains(InWindow(rowPlay, dialog))
            && InWindow(rowPlay, dialog).Right <= InWindow(frequency, dialog).X + 0.5);
        CheckUnclipped(dialog, "window 780×650, \"radio\" listed, the row and detail play buttons showing");
        Png(dialog, "catalog-quickplay-listen");

        var mark = rig.Journal.Count;
        await ClickAsync(rowPlay);
        if (!await WaitAsync(() => editor.IsPreviewing)) throw new TimeoutException("The row's play button never started a preview.");
        Layout(dialog);
        Check("QM-01 clicking the row's play button previews exactly that row's stream through the coordinator (PlayPreviewAsync with its URL and name) and nothing else",
            rig.Since(mark) == $"coordinator.PlayPreviewAsync:Radio Thessaloniki:{Thessaloniki.StreamUrl}"
            && editor.PreviewingUrl == Thessaloniki.StreamUrl && editor.IsPreviewing);
        Check("QM-01 the button becomes stop in place: same control, spoken name \"Stop Radio Thessaloniki\", the stop icon, the playing class (row and detail pane alike)",
            RowPreview(dialog, "Stop Radio Thessaloniki") is { IsEffectivelyVisible: true } rowStop && ReferenceEquals(rowStop, rowPlay)
            && IconsOf(rowStop) == (false, true) && rowStop.Classes.Contains("playing")
            && AccessibleName(DetailPreview(dialog)) == "Stop Radio Thessaloniki" && IconsOf(DetailPreview(dialog)) == (false, true));
        Check("QM-01 pressing a row's quick play is not a pick: the results stay open on their highlight and the form stays empty",
            Overlay(dialog).IsVisible && editor.HighlightedResult?.Entry == Thessaloniki && editor.SelectedEntry == null
            && string.IsNullOrEmpty(Field(dialog, StationEditorViewModel.UrlLabel).Text) && rig.Settings.Stations.Count == 3 && !rig.SavedToDisk);
        CheckUnclipped(dialog, "window 780×650, a row previewing (stop icon, detail pane included)");
        Png(dialog, "catalog-quickplay-stop");

        mark = rig.Journal.Count;
        await ClickAsync(RowPreview(dialog, "Stop Radio Thessaloniki"));
        if (!await WaitAsync(() => !editor.IsPreviewing)) throw new TimeoutException("The row's stop button never ended the preview.");
        Layout(dialog);
        Check("QM-03 clicking stop on the row ends the preview with one StopAsync (no station picked, nothing added) and the button is a play button again, still on the highlighted row",
            rig.Since(mark) == "coordinator.StopAsync" && editor.PreviewingUrl == null
            && RowPreview(dialog, "Listen Radio Thessaloniki") is { IsEffectivelyVisible: true } && IconsOf(RowPreview(dialog, "Listen Radio Thessaloniki")) == (true, false)
            && editor.HighlightedResult?.Entry == Thessaloniki && Overlay(dialog).IsVisible && rig.Settings.Stations.Count == 3);
        Check("QM-03 both buttons return to play: the detail pane's button too, for as long as the pane shows that station",
            AccessibleName(DetailPreview(dialog)) == "Listen Radio Thessaloniki" && IconsOf(DetailPreview(dialog)) == (true, false));

        // Picked: the pane keeps the station with its results closed, and its button is the whole quick play from then on.
        await PressAsync(dialog, Key.Enter);
        Layout(dialog);
        Check("QM-02 fixture: Enter picked Radio Thessaloniki (the form holds its URL, the results are closed, the detail pane keeps it)",
            editor.SelectedEntry == Thessaloniki && !Overlay(dialog).IsVisible && editor.DetailRow?.Entry == Thessaloniki
            && Field(dialog, StationEditorViewModel.UrlLabel).Text == Thessaloniki.StreamUrl);
        Check("QM-02 QM-07 with the results closed the pane's button is still there, in the same top-right corner, and the row buttons are gone",
            AccessibleName(DetailPreview(dialog)) == "Listen Radio Thessaloniki"
            && !Find<Button>(dialog).Any(b => b.Classes.Contains("inRow") && b.IsEffectivelyVisible)
            && InWindow(DetailPreview(dialog), dialog).Right <= InWindow(Detail(dialog), dialog).Right + 0.5);
        CheckUnclipped(dialog, "window 780×650, a picked station, the detail pane's play button");
        Png(dialog, "catalog-quickplay-detail-listen");

        mark = rig.Journal.Count;
        await ClickAsync(DetailPreview(dialog));
        if (!await WaitAsync(() => editor.IsPreviewing)) throw new TimeoutException("The detail pane's play button never started a preview.");
        Layout(dialog);
        Check("QM-02 clicking the detail pane's button previews the picked entry's own stream (PlayPreviewAsync with its URL and name), the pane staying on the station",
            rig.Since(mark) == $"coordinator.PlayPreviewAsync:Radio Thessaloniki:{Thessaloniki.StreamUrl}" && editor.PreviewingUrl == Thessaloniki.StreamUrl
            && AccessibleName(DetailPreview(dialog)) == "Stop Radio Thessaloniki" && IconsOf(DetailPreview(dialog)) == (false, true));
        mark = rig.Journal.Count;
        await ClickAsync(DetailPreview(dialog));
        Check("QM-03 clicking it again stops the preview (StopAsync) and the pane's button returns to \"Listen <name>\", the picked station still shown",
            await WaitAsync(() => !editor.IsPreviewing) && rig.Since(mark) == "coordinator.StopAsync"
            && AccessibleName(DetailPreview(dialog)) == "Listen Radio Thessaloniki" && IconsOf(DetailPreview(dialog)) == (true, false)
            && editor.DetailRow?.Entry == Thessaloniki && rig.Settings.Stations.Count == 3 && !rig.SavedToDisk);
        dialog.Close();
        await PumpAsync();
    }

    // ─── QM-02, QM-03: the detail pane's quick play with the results overlay still open (regression) ───

    /// <summary>
    /// The detail pane is the overlay's sibling (it sits beside the covered form column, not inside it), so a press starting
    /// in its quick-play button must be left to the button: <c>OnWindowPointerPressed</c> used to fall through and close the
    /// results on the tunnelling press, clearing the highlight before the button saw its release and leaving the preview
    /// playing with no visible stop. The earlier QM checks drove this button only once the results were closed (a pick), so
    /// they worked around the bug; this one holds the overlay open on the highlighted row throughout the whole listen/stop
    /// cycle.
    /// </summary>
    private static async Task QuickPlayFromTheDetailPaneWithTheResultsOpen()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "radio"); // Thessaloniki, Köln AM, Shortwave; the first is highlighted
        Layout(dialog);
        var highlighted = editor.HighlightedResult;
        var detailPlay = DetailPreview(dialog);

        Check("QM-02 fixture: the results are open on the highlighted Radio Thessaloniki, the overlay drawn, and the detail pane's quick-play button offers \"Listen Radio Thessaloniki\" with the play icon",
            Overlay(dialog).IsVisible && editor.IsResultsOpen && highlighted?.Entry == Thessaloniki
            && detailPlay.IsEffectivelyVisible && AccessibleName(detailPlay) == "Listen Radio Thessaloniki" && IconsOf(detailPlay) == (true, false));

        // The press alone: it must land on the button and leave the results open on the same row, so the button still gets
        // its release. (Pre-fix this is where the overlay closed and the highlight was dropped.)
        var at = detailPlay.TranslatePoint(new Point(detailPlay.Bounds.Width / 2, detailPlay.Bounds.Height / 2), dialog)
            ?? throw new InvalidOperationException("The detail pane's quick-play button has no position in its window.");
        var hit = dialog.InputHitTest(at) as Visual;
        var mark = rig.Journal.Count;
        dialog.MouseDown(at, MouseButton.Left);
        await PumpAsync();
        Layout(dialog);
        Check("QM-02 the press on the detail pane's quick play (the press point hits the button, clear of the overlay) does not close the results and does not clear the highlight: the overlay stays open on the same row identity, nothing picked",
            Within(hit, detailPlay) && Overlay(dialog).IsVisible && editor.IsResultsOpen
            && ReferenceEquals(editor.HighlightedResult, highlighted) && editor.SelectedEntry == null && editor.DetailRow?.Entry == Thessaloniki);

        dialog.MouseUp(at, MouseButton.Left);
        if (!await WaitAsync(() => editor.IsPreviewing)) throw new TimeoutException("The detail pane's quick play never started a preview with the results open.");
        Layout(dialog);
        Check("QM-02 the release previews the highlighted station through the coordinator (PlayPreviewAsync with its URL and name) and nothing else, the results still open on the same row",
            rig.Since(mark) == $"coordinator.PlayPreviewAsync:Radio Thessaloniki:{Thessaloniki.StreamUrl}"
            && editor.PreviewingUrl == Thessaloniki.StreamUrl && Overlay(dialog).IsVisible && editor.IsResultsOpen
            && ReferenceEquals(editor.HighlightedResult, highlighted) && editor.SelectedEntry == null);
        Check("QM-02 the pane's button flips to stop in place with the overlay open: same control, spoken name \"Stop Radio Thessaloniki\", the stop icon, the playing class",
            ReferenceEquals(DetailPreview(dialog), detailPlay) && AccessibleName(detailPlay) == "Stop Radio Thessaloniki"
            && IconsOf(detailPlay) == (false, true) && detailPlay.Classes.Contains("playing") && Overlay(dialog).IsVisible);

        mark = rig.Journal.Count;
        await ClickAsync(DetailPreview(dialog));
        if (!await WaitAsync(() => !editor.IsPreviewing)) throw new TimeoutException("The detail pane's stop button never ended the preview with the results open.");
        Layout(dialog);
        Check("QM-03 pressing it again stops the preview (one StopAsync, no station picked, nothing added) and the pane's button returns to \"Listen <name>\", the overlay still open on the same highlight",
            rig.Since(mark) == "coordinator.StopAsync" && editor.PreviewingUrl == null
            && AccessibleName(DetailPreview(dialog)) == "Listen Radio Thessaloniki" && IconsOf(DetailPreview(dialog)) == (true, false)
            && Overlay(dialog).IsVisible && editor.IsResultsOpen && ReferenceEquals(editor.HighlightedResult, highlighted)
            && editor.SelectedEntry == null && rig.Settings.Stations.Count == 3 && !rig.SavedToDisk);
        dialog.Close();
        await PumpAsync();
    }

    // ─── QM-01, QM-04, QM-05, QM-06, QM-08 over the real coordinator: the preview drives the app's one engine ───

    private static async Task QuickPlayLifecycleOverTheRealCoordinator()
    {
        // Cancel stops the preview: the engine session ends, the snapshot is the idle one (not "Paused") and the card returns.
        await using (var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650, realCoordinator: true))
        {
            rig.Catalog.Result = Loaded(Small);
            var (dialog, editor) = await OpenAddAsync(rig);
            var engine = rig.Engine!;
            await SearchAsync(dialog, editor, "radio");
            await ClickAsync(RowPreview(dialog, "Listen Radio Thessaloniki"));
            Check("QM-01 through the real coordinator: the row's play button starts one engine session on that stream, under the entry's name",
                await WaitAsync(() => engine.Starts.Count == 1) && engine.Starts[0].Source.Url == new Uri(Thessaloniki.StreamUrl)
                && engine.Starts[0].Source.DisplayName == "Radio Thessaloniki" && engine.ActiveSessionId == 1 && engine.CallLog.SequenceEqual(["start:1"]));
            Check("QM-06 while a preview plays the now-playing card names the transient station, and no saved station or last-played id was touched",
                await WaitAsync(() => Shows(rig.Window!, "Radio Thessaloniki")) && rig.Real!.Snapshot.CurrentStationId is not null
                && rig.Settings.Stations.Count == 3 && rig.Settings.LastStationId is null && !rig.SavedToDisk);

            await ClickAsync(ButtonWithText(dialog, "Cancel"));
            Check("QM-06 Cancel stops the preview through the coordinator: the engine session is stopped and the snapshot is the idle one (no station, \"Ready when you are\"), not a pause",
                await WaitAsync(() => !dialog.IsVisible && engine.StopCount == 1)
                && rig.Real!.Snapshot is { IsActive: false, CurrentStationId: null, DesiredStationId: null } idle
                && idle.StatusText == DialShift.Tests.Core.Texts.Ready && idle.TrackText == DialShift.Tests.Core.Texts.ReadyTrack
                && engine.ActiveSessionId is null);
            Check("QM-06 QM-08 the card returns to its idle placeholder and nothing was added or saved",
                await WaitAsync(() => Shows(rig.Window!, UiText.DefaultTitle)) && !Shows(rig.Window!, "Radio Thessaloniki")
                && rig.Settings.Stations.Count == 3 && rig.Settings.LastStationId is null && !rig.SavedToDisk && rig.OnDisk().Stations.Count == 3);
        }

        // Save keeps it playing: the caller adopts the very stream, one session for the whole scenario.
        await using (var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650, realCoordinator: true))
        {
            rig.Catalog.Result = Loaded(Small);
            var (dialog, editor) = await OpenAddAsync(rig);
            var engine = rig.Engine!;
            await SearchAsync(dialog, editor, "radio");
            await PressAsync(dialog, Key.Enter); // picks the highlighted Radio Thessaloniki
            Check("QM-04 fixture: the pick filled the form from the entry and closed the results, the detail pane keeps the station",
                editor.SelectedEntry == Thessaloniki && !Overlay(dialog).IsVisible && editor.DetailRow?.Entry == Thessaloniki
                && Field(dialog, StationEditorViewModel.NameLabel).Text == "Radio Thessaloniki"
                && Field(dialog, StationEditorViewModel.UrlLabel).Text == Thessaloniki.StreamUrl);

            await ClickAsync(DetailPreview(dialog));
            Check("QM-04 the detail pane's play button previews the picked entry's stream (one engine session on its URL)",
                await WaitAsync(() => engine.Starts.Count == 1) && engine.Starts[0].Source.Url == new Uri(Thessaloniki.StreamUrl));

            await ClickAsync(ButtonWithText(dialog, "Save"));
            var saved = await WaitAsync(() => !dialog.IsVisible && rig.Settings.Stations.Count == 4);
            var added = rig.Settings.Stations[^1];
            Check("QM-04 QM-05 Save adds the station the user listened to, then the caller plays it: the engine was never restarted (one start, no stop) and the running stream is adopted by the saved station",
                saved && added.Name == "Radio Thessaloniki" && added.Url == Thessaloniki.StreamUrl && added.Id != default
                && engine.Starts.Count == 1 && engine.StopCount == 0 && engine.CallLog.SequenceEqual(["start:1"]) && engine.ActiveSessionId == 1
                && rig.Real!.Snapshot is { IsActive: true } adopted && adopted.CurrentStationId == added.Id && adopted.DesiredStationId == added.Id
                && !adopted.IsFallback && adopted.RetryInSeconds is null);
            Check("QM-04 … and LastStationId reaches the settings on disk, not just the snapshot",
                await WaitAsync(() => rig.Settings.LastStationId == added.Id && rig.OnDisk().LastStationId == added.Id)
                && rig.OnDisk().Stations.Any(s => s.Id == added.Id && s.Name == "Radio Thessaloniki"));
            Check("QM-04 QM-08 the preview itself wrote no LastStationId: it is the save that did",
                rig.Settings.LastStationId == added.Id && rig.Settings.Stations.Count(s => s.Url == Thessaloniki.StreamUrl) == 1);

            engine.RaiseState(engine.ActiveSessionId!.Value, PlaybackEngineState.Playing);
            Check("QM-04 the adopted stream keeps playing: the same session's Playing renders \"LIVE BROADCAST\" for the saved station, no second start anywhere",
                await WaitAsync(() => Shows(rig.Window!, "LIVE BROADCAST")) && Shows(rig.Window!, "Radio Thessaloniki")
                && engine.Starts.Count == 1 && engine.ActiveSessionId == 1);
            Png(rig.Window!, "catalog-quickplay-adopted");
        }
    }
}
