using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DialShift.App.ViewModels;
using DialShift.App.Views.Dialogs;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.CatalogUiFixtures;
using static DialShift.Tests.Ui.Headless;

namespace DialShift.Tests.Ui;

/// <summary>
/// The Add dialog's D87 ink and rule on the headless platform (docs/catalog-contracts.md §8 CAT-13, CAT-14): the results
/// footer's 1 px <c>DsDialRingBrush</c> top divider sits between the list and the footer text and shows only with the
/// footer; the no-match status line takes <c>DsSecondaryBrush</c> through the <c>noMatch</c> class at 4.5:1 or more on the
/// dialog background, while the plain status line keeps the <c>hint</c> ink, <c>DsSubtleBrush</c>.
/// </summary>
internal static partial class CatalogHeadlessTests
{
    private static ISolidColorBrush ThemeBrush(Window dialog, string key) =>
        dialog.TryFindResource(key, out var value) && value is ISolidColorBrush brush ? brush : throw new KeyNotFoundException(key);

    /// <summary>The results footer's text and the border that carries its top divider.</summary>
    private static (TextBlock Text, Border Box) Footer(Window dialog)
    {
        var text = Find<TextBlock>(Overlay(dialog)).Single(t => t.GetVisualParent() is Border && t.Classes.Contains("hint"));
        return (text, (Border)text.GetVisualParent()!);
    }

    /// <summary>
    /// The opaque colour under <paramref name="element"/>: every solid background from <paramref name="top"/> down to the
    /// element itself, each composited over the ones above it as the renderer blends them.
    /// </summary>
    private static Color BackgroundUnder(Visual element, Window top)
    {
        var chain = element.GetSelfAndVisualAncestors().TakeWhile(v => v != top.GetVisualParent()).Reverse();
        var color = Colors.Black;
        foreach (var v in chain)
        {
            var brush = v switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                ContentPresenter c => c.Background,
                TemplatedControl t => t.Background,
                TextBlock t => t.Background,
                _ => null
            };
            if (brush is ISolidColorBrush) color = Over(InkOf(brush, v, top), color);
        }
        return color;
    }

    /// <summary>The colour of the rendered pixel at <paramref name="x"/>, <paramref name="y"/> (window coordinates).</summary>
    private static Color PixelAt(Bitmap frame, double x, double y)
    {
        var bytes = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect((int)Math.Floor(x), (int)Math.Floor(y), 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return frame.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? Color.FromRgb(bytes[2], bytes[1], bytes[0]) : Color.FromRgb(bytes[0], bytes[1], bytes[2]);
    }

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    /// <summary>The status line's ink: its brush, and its contrast on what lies under it (resolved brushes, and the rendered frame's glyph pixels).</summary>
    private static (ISolidColorBrush Brush, double Contrast, double Rendered) StatusInk(Window dialog)
    {
        var line = StatusLine(dialog);
        var brush = (ISolidColorBrush)line.Foreground!;
        var under = BackgroundUnder(line, dialog);
        var ink = Over(InkOf(brush, line, dialog), under);
        using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        var area = new Rect(InWindow(line, dialog).Position, new Size(line.TextLayout.WidthIncludingTrailingWhitespace, line.TextLayout.Height));
        return (brush, Contrast(ink, under), RenderedContrast(frame, area, under));
    }

    /// <summary>
    /// With the overlay open, the footer's top border is the divider: 1 px of <c>DsDialRingBrush</c> on top only, drawn as
    /// that colour, across the card's inner width (less the footer's 6 px side margins), with the list above it and the
    /// footer text directly below it.
    /// </summary>
    private static void CheckFooterDivider(StationEditorDialog dialog, StationEditorViewModel editor, string state)
    {
        Layout(dialog);
        var ring = ThemeBrush(dialog, "DsDialRingBrush");
        var (text, box) = Footer(dialog);
        var card = InWindow(Overlay(dialog), dialog);
        var inner = card.Deflate(Overlay(dialog).BorderThickness).Deflate(Overlay(dialog).Padding);
        var divider = InWindow(box, dialog).WithHeight(box.BorderThickness.Top);
        var words = InWindow(text, dialog);
        var list = InWindow(Find<ListBox>(Overlay(dialog)).Single(l => l.Name == "ResultsList"), dialog);
        Check($"CAT-14 D87 {state}: the footer \"{text.Text}\" has a 1 px top divider (top only) in the DsDialRingBrush resource ({(box.BorderBrush as ISolidColorBrush)?.Color})",
            Overlay(dialog).IsVisible && text.IsEffectivelyVisible && text.Text == editor.TotalCountText && text.Text!.Length > 0 && box.IsEffectivelyVisible
            && box.BorderBrush is ISolidColorBrush drawn && (ReferenceEquals(drawn, ring) || drawn.Color == ring.Color) && drawn.Opacity == 1
            && box.BorderThickness == new Thickness(0, 1, 0, 0));
        Check($"CAT-14 D87 {state}: the divider {Show(divider)} spans the card's inner width {Show(inner)} but for the footer's 6 px margins",
            divider.X - inner.X is >= 0 and <= 6.5 && inner.Right - divider.Right is >= 0 and <= 6.5 && divider.X <= words.X && divider.Right >= words.Right);
        Check($"CAT-14 D87 {state}: the divider sits between the list {Show(list)} and the footer text {Show(words)}: the list ends at or above it, the text starts right below it",
            list.Bottom <= divider.Y + 0.5 && words.Y >= divider.Bottom - 0.5 && words.Y - divider.Bottom <= 10);

        using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        var pixel = PixelAt(frame, divider.Center.X, divider.Y + 0.5);
        var cardColor = BackgroundUnder(box, dialog);
        Console.WriteLine($"  footer divider ({state}): rendered {pixel} at ({divider.Center.X:F0}, {divider.Y:F1}); DsDialRingBrush {ring.Color}; card {cardColor}");
        Check($"CAT-14 D87 {state}: the divider is drawn: the rendered pixel on it is the DsDialRingBrush colour, not the card's",
            Near(pixel, ring.Color, 6) && !Near(pixel, cardColor, 6));
    }

    private static async Task FooterDividerAndStatusInk()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        var subtle = ThemeBrush(dialog, "DsSubtleBrush");
        var secondary = ThemeBrush(dialog, "DsSecondaryBrush");

        var loaded = StatusInk(dialog);
        Console.WriteLine($"  status line (loaded) \"{StatusLine(dialog).Text}\": {loaded.Brush.Color} {loaded.Contrast:F2}:1 / rendered {loaded.Rendered:F2}:1");
        Check($"CAT-13 D87 the plain status line (loaded, \"{StatusLine(dialog).Text}\") has the hint ink DsSubtleBrush, not the noMatch class ({loaded.Contrast:F2}:1)",
            StatusLine(dialog).Text == editor.CatalogStatusText && StatusLine(dialog).Classes.Contains("hint") && !StatusLine(dialog).Classes.Contains("noMatch")
            && loaded.Brush.Color == subtle.Color);

        await SearchAsync(dialog, editor, "radio");
        var matches = StatusInk(dialog);
        Check($"CAT-13 D87 the plain status line with matches (\"radio\", 3 rows) keeps the hint ink DsSubtleBrush, not the noMatch class ({matches.Contrast:F2}:1)",
            editor.Results.Count == 3 && StatusLine(dialog).Text == editor.CatalogStatusText && !StatusLine(dialog).Classes.Contains("noMatch")
            && matches.Brush.Color == subtle.Color);
        CheckFooterDivider(dialog, editor, "3 rows");
        Png(dialog, "catalog-d87-footer-divider");

        // All seven rows: more than the card holds, so the last one is cut by the divider, not by the card's edge.
        await ClickAsync(ByName<Button>(dialog, "Clear search and filters"));
        if (!await WaitAsync(() => editor.PendingSearch.IsCompleted && editor.SearchText.Length == 0 && editor.Results.Count == Small.Count))
            throw new TimeoutException("Clear's search did not land.");
        Layout(dialog);
        var rows = Find<ScrollViewer>(Overlay(dialog)).Single();
        Check("CAT-14 fixture: Clear lists all 7 stations, more than the card holds (the list scrolls)",
            Overlay(dialog).IsVisible && rows.Extent.Height > rows.Viewport.Height + 1);
        CheckFooterDivider(dialog, editor, "all 7 rows (the list scrolls)");

        await SearchAsync(dialog, editor, "zzz no such station");
        var (_, box) = Footer(dialog);
        Check("CAT-14 D87 no match: no footer text, so no divider (it shows only with the footer)",
            editor.TotalCountText.Length == 0 && !box.IsVisible && !Overlay(dialog).IsVisible);
        var noMatch = StatusInk(dialog);
        Console.WriteLine($"  status line (no match) \"{StatusLine(dialog).Text}\": {noMatch.Brush.Color} {noMatch.Contrast:F2}:1 / rendered {noMatch.Rendered:F2}:1" +
                          $" (plain {matches.Contrast:F2}:1 / rendered {matches.Rendered:F2}:1)");
        Check($"CAT-13 D87 no match: the status line has the noMatch class and the DsSecondaryBrush ink ({noMatch.Brush.Color})",
            StatusLine(dialog) is { Text: UiText.CatalogNoMatch } line && line.Classes.Contains("hint") && line.Classes.Contains("noMatch")
            && noMatch.Brush.Color == secondary.Color);
        Check($"CAT-13 D87 no match: the message reaches 4.5:1 on the dialog background, as the brushes resolve and composite: {noMatch.Contrast:F2}:1 (rendered {noMatch.Rendered:F2}:1)",
            noMatch.Contrast >= MinContrast);
        Check($"CAT-13 D87 no match: the message reads brighter than the plain status line ({noMatch.Contrast:F2}:1 against {matches.Contrast:F2}:1)",
            noMatch.Contrast > matches.Contrast);
        Png(dialog, "catalog-d87-no-match-ink");

        await SearchAsync(dialog, editor, "kosmos");
        Check("CAT-13 D87 a match again: the status line drops the noMatch class and is back in DsSubtleBrush; the footer and its divider show",
            !StatusLine(dialog).Classes.Contains("noMatch") && StatusInk(dialog).Brush.Color == subtle.Color && Footer(dialog).Box.IsEffectivelyVisible);
        dialog.Close();
        await PumpAsync();
    }
}
