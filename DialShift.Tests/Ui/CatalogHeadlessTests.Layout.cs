using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
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
/// The Add dialog's D85 geometry, tiles and ink on the headless platform (docs/catalog-contracts.md §5.5, §8 CAT-10,
/// CAT-14): the overlay covers exactly the form column (B1), the monogram hides behind a logo (B2) on a light tile (P8),
/// the detail pane is a Group control element (B3) whose name is a live region (P4), the overlay's shadow is the theme's
/// (B4), a result row's texts reach 4.5:1 in every state (B5; D91: the " · " separator's brush is gated, its rendered pixels reported), the frequency column is never trimmed (P1), Clear is 36 px tall
/// (P7), the filter drop-downs are at most 280 px wide with an ellipsis (D83, D44), and the Edit dialog (D83).
/// </summary>
internal static partial class CatalogHeadlessTests
{
    /// <summary>WCAG AA for normal text (D45).</summary>
    private const double MinContrast = 4.5;

    private static StackPanel FormColumn(Window dialog) => Find<StackPanel>(dialog).Single(p => p.Name == "FormColumn");

    /// <summary>The bounds of <paramref name="visual"/> in <paramref name="window"/>'s client coordinates.</summary>
    private static Rect InWindow(Visual visual, Window window) =>
        new(visual.TranslatePoint(default, window) ?? throw new InvalidOperationException("Not in the window."), visual.Bounds.Size);

    private static string Show(Rect r) => $"({r.X:F1}, {r.Y:F1}, {r.Width:F1}×{r.Height:F1})";

    // ─── CAT-14 D85 B1: the overlay covers exactly the form column ───

    /// <summary>
    /// With the overlay open: its card has the form column's height and top (within 0.5 px) and covers the column's width;
    /// the list's first row sits at the card's top and the footer at its bottom; and every visible text of the form column
    /// (the separator, the labels, the three fields, the hint) lies wholly inside the card, so none shows half cut.
    /// </summary>
    private static void CheckOverlayCoversForm(StationEditorDialog dialog, StationEditorViewModel editor, string state)
    {
        Layout(dialog);
        var card = InWindow(Overlay(dialog), dialog);
        var form = FormColumn(dialog);
        var column = InWindow(form, dialog);
        Check($"CAT-14 D85 B1 {state}: the open overlay {Show(card)} is exactly the form column {Show(column)}: same top, height and width",
            Overlay(dialog).IsVisible && Math.Abs(card.Height - column.Height) <= 0.5 && Math.Abs(card.Y - column.Y) <= 0.5
            && Math.Abs(card.X - column.X) <= 0.5 && Math.Abs(card.Width - column.Width) <= 0.5);

        var first = Items(dialog).OrderBy(i => InWindow(i, dialog).Y).First();
        var firstTop = InWindow(first, dialog).Y - card.Y;
        var footer = Find<TextBlock>(Overlay(dialog)).Single(t => t.IsEffectivelyVisible && t.Text == editor.TotalCountText && t.Text.Length > 0);
        var footerGap = card.Bottom - InWindow(footer, dialog).Bottom;
        // Border 1 + padding 4 above the first row; the footer's 6 px margin + padding 4 + border 1 below it.
        Check($"CAT-14 D85 B1 {state}: the list starts at the card's top (first row {firstTop:F1} px down) and the footer ends at its bottom ({footerGap:F1} px up)",
            firstTop is >= 0 and <= 6 && footerGap is >= 0 and <= 12);

        var texts = Find<Control>(form).Where(c => c is TextBlock or TextBox && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.TemplatedParent == null)
            .Where(c => c is not TextBlock t || DisplayText(t).Length > 0).ToList();
        var outside = texts.Where(c => !card.Inflate(0.5).Contains(InWindow(c, dialog)))
            .Select(c => $"{(c is TextBlock t ? $"\"{DisplayText(t)}\"" : AccessibleName(c))} {Show(InWindow(c, dialog))}").ToList();
        if (outside.Count > 0) Console.WriteLine($"  form text crossing the card's edge ({state}): " + string.Join("; ", outside));
        Check($"CAT-14 D85 B1 {state}: all {texts.Count} visible texts and fields of the form column lie wholly inside the card (none crosses its edge)",
            texts.Count >= 7 && outside.Count == 0);
    }

    // ─── CAT-10 D85 B2 / P8, CAT-14 B3 / B4 / P4 / P6 / P7 ───

    private static async Task TilesAndDetailPane()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        // 96×24 at 50 % alpha (premultiplied): wide and half transparent, so a monogram behind it would show through.
        var logo = SolidLogo(0x80613C3C, 96, 24);
        rig.Catalog.Result = Loaded(Small);
        rig.Logos.Answer = url => url == Kosmos.Logo ? logo : null;
        var (dialog, editor) = await OpenAddAsync(rig);

        var detailPeer = ControlAutomationPeer.CreatePeerForElement(Detail(dialog));
        Check("CAT-14 D85 B3 the detail pane is a control element for screen readers: its peer IsControlElement(), a Group named \"Station details\"",
            detailPeer.IsControlElement() && detailPeer.GetAutomationControlType() == AutomationControlType.Group && detailPeer.GetName() == "Station details");
        Check("CAT-14 D85 B4 the overlay's shadow is the theme resource DsOverlayShadow",
            dialog.TryFindResource("DsOverlayShadow", out var shadow) && shadow is BoxShadows expected && Overlay(dialog).BoxShadow == expected && expected.Count > 0);
        var clear = ByName<Button>(dialog, "Clear search and filters");
        Check($"CAT-14 D85 P7 Clear is at least 36 px tall ({clear.Bounds.Height:F1})", clear.Bounds.Height >= 36);
        Check("CAT-13 D85 P6 the \"Or enter stream details manually\" separator shows once the catalog is loaded", Shows(dialog, UiText.ManualEntrySeparator));

        await SearchAsync(dialog, editor, "o");
        var kosmos = ItemOf(dialog, Kosmos);
        var thessaloniki = ItemOf(dialog, Thessaloniki);
        Check("CAT-10 fixture: \"o\" lists Kosmos 93.6 (logo 96×24, half transparent) and Radio Thessaloniki (no logo)",
            Find<Image>(kosmos).Single() is { IsEffectivelyVisible: true } image && ReferenceEquals(image.Source, logo) && logo.PixelSize == new PixelSize(96, 24));
        var textBrush = (ISolidColorBrush)dialog.FindResource("DsTextBrush")!;
        Check("CAT-10 D85 B2 a result showing a logo hides its monogram (\"K\" not visible in the tile)",
            MonogramOf(TileOf(kosmos)) is { Text: "K", IsEffectivelyVisible: false });
        Check("CAT-10 D85 P8 a result tile showing a logo has the DsTextBrush background and 2 px padding; a monogram tile has neither",
            TileOf(kosmos) is { Background: ISolidColorBrush lit, Padding: var padding } && lit.Color == textBrush.Color && padding == new Thickness(2)
            && TileOf(thessaloniki) is var plain && (plain.Background as ISolidColorBrush)?.Color != textBrush.Color && plain.Padding == default
            && MonogramOf(TileOf(thessaloniki)) is { Text: "R", IsEffectivelyVisible: true });

        // The pointer highlights Kosmos 93.6, so the detail pane shows it.
        dialog.MouseMove(kosmos.TranslatePoint(new Point(kosmos.Bounds.Width / 2, kosmos.Bounds.Height / 2), dialog)!.Value, RawInputModifiers.None);
        await PumpAsync();
        Layout(dialog);
        var detailTile = TileOf(Detail(dialog));
        Check("CAT-10 D85 B2 P8 the detail pane shows Kosmos 93.6's logo with its monogram hidden, on the DsTextBrush tile with 2 px padding",
            editor.DetailLogo == logo && Find<Image>(detailTile).Single() is { IsEffectivelyVisible: true } && MonogramOf(detailTile) is { IsEffectivelyVisible: false }
            && detailTile.Background is ISolidColorBrush detailLit && detailLit.Color == textBrush.Color && detailTile.Padding == new Thickness(2));
        var namePeer = ControlAutomationPeer.CreatePeerForElement(Find<TextBlock>(Detail(dialog)).Single(t => t.Classes.Contains("detailName")));
        Check("CAT-14 D85 P4 the detail name is a polite live region and reads the highlighted row (\"Kosmos 93.6\")",
            namePeer.GetLiveSetting() == AutomationLiveSetting.Polite && namePeer.GetName() == "Kosmos 93.6");

        var kosmosRow = editor.Results.Single(r => r.Entry == Kosmos);
        kosmosRow.Logo = null;
        Layout(dialog);
        Check("CAT-10 D85 B2 the row's monogram shows again once its Logo is null (no image, the plain tile)",
            MonogramOf(TileOf(ItemOf(dialog, Kosmos))) is { Text: "K", IsEffectivelyVisible: true } && Find<Image>(ItemOf(dialog, Kosmos)).Single() is { IsEffectivelyVisible: false }
            && TileOf(ItemOf(dialog, Kosmos)).Padding == default);
        editor.HighlightedResult = editor.Results.Single(r => r.Entry == Thessaloniki);
        Layout(dialog);
        Check("CAT-10 D85 B2 the detail pane's monogram shows again for a row without a logo (\"R\", no image), and the live name follows (\"Radio Thessaloniki\")",
            MonogramOf(TileOf(Detail(dialog))) is { Text: "R", IsEffectivelyVisible: true } && Find<Image>(TileOf(Detail(dialog))).Single() is { IsEffectivelyVisible: false }
            && namePeer.GetName() == "Radio Thessaloniki");
        Png(dialog, "catalog-d85-tiles");
        dialog.Close();
        await PumpAsync();

        // P6: the separator shows while the catalog loads, and not without a catalog.
        var hold = new TaskCompletionSource();
        rig.Catalog.Hold = hold;
        (dialog, _) = await OpenAddAsync(rig, settle: false);
        Check("CAT-13 D85 P6 the separator shows while the catalog loads", Shows(dialog, UiText.ManualEntrySeparator) && StatusLine(dialog).Text == UiText.CatalogLoading);
        hold.SetResult();
        dialog.Close();
        await PumpAsync();
        rig.Catalog.Hold = null;
        rig.Catalog.Result = CatalogLoadResult.Unavailable("the file is missing.");
        (dialog, _) = await OpenAddAsync(rig);
        Check("CAT-13 D85 P6 without a catalog the separator is not shown (nothing to choose \"or\" from)",
            !Shows(dialog, UiText.ManualEntrySeparator) && StatusLine(dialog).Text == UiText.CatalogUnavailable);
        dialog.Close();
        await PumpAsync();

        static Border TileOf(Visual root) => Find<Border>(root).First(b => b.Classes.Contains("tile") && b.IsEffectivelyVisible);
        static TextBlock MonogramOf(Border tile) => Find<TextBlock>(tile).Single();
    }

    // ─── CAT-14 D85 B5: a result row's contrast in every state ───

    /// <summary>An ink over a background: the colour a text is drawn in, and how opaque it is (brush and element opacity).</summary>
    private readonly record struct Ink(Color Color, double Alpha);

    /// <summary>WCAG 2 relative luminance of an sRGB colour.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="ink"/> composited over the opaque <paramref name="under"/>, as the renderer blends it.</summary>
    private static Color Over(Ink ink, Color under)
    {
        var a = ink.Alpha * ink.Color.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(ink.Color.R, under.R), Mix(ink.Color.G, under.G), Mix(ink.Color.B, under.B));
    }

    /// <summary>The ink of <paramref name="brush"/> drawn by <paramref name="element"/>: the brush's opacity times every element opacity up to <paramref name="top"/>.</summary>
    private static Ink InkOf(IBrush? brush, Visual element, Visual top)
    {
        var solid = brush as ISolidColorBrush ?? throw new InvalidOperationException($"Not a solid colour: {brush}.");
        var alpha = solid.Opacity;
        for (var v = element; v != null && v != top.GetVisualParent(); v = v.GetVisualParent()) alpha *= v.Opacity;
        return new Ink(solid.Color, alpha);
    }

    /// <summary>
    /// The largest contrast any pixel of <paramref name="area"/> (window coordinates) has against <paramref name="background"/>
    /// in <paramref name="frame"/>: what the most-covered glyph pixels actually show.
    /// </summary>
    private static double RenderedContrast(Bitmap frame, Rect area, Color background)
    {
        var rect = new PixelRect((int)Math.Floor(area.X), (int)Math.Floor(area.Y), (int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height))
            .Intersect(new PixelRect(frame.PixelSize));
        if (rect.Width <= 0 || rect.Height <= 0) return 0;
        var stride = rect.Width * 4;
        var bytes = new byte[stride * rect.Height];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { frame.CopyPixels(rect, handle.AddrOfPinnedObject(), bytes.Length, stride); }
        finally { handle.Free(); }
        var bgra = frame.Format == PixelFormat.Bgra8888;
        var best = 1.0;
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var pixel = bgra ? Color.FromRgb(bytes[i + 2], bytes[i + 1], bytes[i]) : Color.FromRgb(bytes[i], bytes[i + 1], bytes[i + 2]);
            best = Math.Max(best, Contrast(pixel, background));
        }
        return best;
    }

    /// <summary>
    /// A result row's five texts (the name, the separator, the place, line 2 and the frequency), each with its ink
    /// composited over the row's fill composited over the card, as the brushes resolve on the live controls; plus the
    /// rendered frame's most contrasting glyph pixel of each against the same background.
    /// </summary>
    private static List<(string Part, string Text, double Contrast, double Rendered)> RowContrast(Window dialog, ListBoxItem item)
    {
        var card = ((ISolidColorBrush)Overlay(dialog).Background!).Color;
        var presenter = Find<ContentPresenter>(item).First(p => p.Name == "PART_ContentPresenter" && p.TemplatedParent == item);
        var highlight = Over(InkOf(presenter.Background, presenter, item), card);
        var title = Find<TextBlock>(item).Single(t => t.Classes.Contains("resultTitle"));
        var kind = Find<TextBlock>(item).Single(t => t.Classes.Contains("resultKind"));
        var frequency = Find<TextBlock>(item).Single(t => t.Classes.Contains("resultFrequency"));
        using var frame = ((Window)TopLevel.GetTopLevel(item)!).CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        var parts = new List<(string, string, double, double)>();
        var start = 0;
        foreach (var (part, run) in new[] { "name", "separator", "place" }.Zip(title.Inlines!.Cast<Run>()))
        {
            var length = run.Text!.Length;
            var areas = title.TextLayout.HitTestTextRange(start, length).Select(r => r.Translate(InWindow(title, dialog).Position)).ToList();
            start += length;
            // A run the ellipsis cuts reports the collapsed rest as further boxes at the ellipsis: its pixels are left out.
            var visible = areas[0];
            foreach (var cut in areas.Skip(1).Where(a => a.X > visible.X && a.X < visible.Right)) visible = visible.WithWidth(cut.X - visible.X);
            var ink = Over(InkOf(run.Foreground ?? title.Foreground, title, item), highlight);
            parts.Add((part, run.Text, Contrast(ink, highlight), RenderedContrast(frame, visible, highlight)));
        }
        foreach (var (part, block) in new[] { ("line 2", kind), ("frequency", frequency) })
        {
            var ink = Over(InkOf(block.Foreground, block, item), highlight);
            var area = new Rect(InWindow(block, dialog).Position, new Size(block.TextLayout.WidthIncludingTrailingWhitespace, block.TextLayout.Height));
            parts.Add((part, block.Text!, Contrast(ink, highlight), RenderedContrast(frame, area, highlight)));
        }
        return parts;
    }

    /// <summary>The pseudo-classes a result row's fill and inks are styled on, as the row has them now.</summary>
    private static string RowState(ListBoxItem item) =>
        string.Concat(new[] { ":selected", ":pointerover", ":pressed" }.Where(item.Classes.Contains)) is { Length: > 0 } state ? state : "normal";

    /// <summary>The part name <see cref="RowContrast"/> gives line 1's " · " between the name and the place.</summary>
    private const string SeparatorPart = "separator";

    /// <summary>
    /// The B5 gate (D91): every text of the row reaches 4.5:1 as its brush resolves; every word (the name, the place, line 2,
    /// the frequency) also in the rendered pixels. The " · " separator is a decorative punctuation mark between words whose
    /// tiny glyph each OS antialiases its own way (Windows measured its brush 5.02:1, its rendered pixels 4.22:1): its
    /// rendered pixels are reported, not gated.
    /// </summary>
    private static bool RowContrastPasses(IReadOnlyList<(string Part, string Text, double Contrast, double Rendered)> parts) =>
        parts.Select(p => p.Part).SequenceEqual(["name", SeparatorPart, "place", "line 2", "frequency"])
        && parts.All(p => p.Contrast >= MinContrast && (p.Part == SeparatorPart || p.Rendered >= MinContrast));

    /// <summary>
    /// Measures <paramref name="item"/> in its current state, prints its line of the contrast table, and checks the B5 gate
    /// (<see cref="RowContrastPasses"/>). Returns the row's fill over the card.
    /// </summary>
    private static Color CheckRowContrast(Window dialog, ListBoxItem item, string how)
    {
        var fill = RowFill(dialog, item);
        var parts = RowContrast(dialog, item);
        var state = RowState(item);
        Console.WriteLine($"  row {state} ({how}) on {fill}, brush contrast / rendered pixels: " +
                          string.Join("; ", parts.Select(p => $"{p.Part} \"{p.Text}\" {p.Contrast:F2}:1 / {p.Rendered:F2}:1")));
        Check($"CAT-14 D85 B5 D91 row {state} ({how}): every text reaches 4.5:1 against the row's fill over the card as the brushes resolve, " +
              "every word also in the rendered pixels (the separator's pixels reported, not gated): " +
              string.Join(", ", parts.Select(p => p.Part == SeparatorPart ? $"{p.Part} {p.Contrast:F2}/({p.Rendered:F2})" : $"{p.Part} {p.Contrast:F2}/{p.Rendered:F2}")),
            RowContrastPasses(parts));
        return fill;
    }

    /// <summary>The fill of <paramref name="item"/> in its current state, composited over the card.</summary>
    private static Color RowFill(Window dialog, ListBoxItem item)
    {
        var card = ((ISolidColorBrush)Overlay(dialog).Background!).Color;
        var presenter = Find<ContentPresenter>(item).First(p => p.Name == "PART_ContentPresenter" && p.TemplatedParent == item);
        return Over(InkOf(presenter.Background, presenter, item), card);
    }

    /// <summary>
    /// The D91 gate's mutation proof on <paramref name="item"/> in its current state: its separator's brush lowered to just
    /// under 4.5:1 against the row's fill fails the gate; its XAML brush put back, the gate passes again. And the gate's word
    /// rule on the Windows measurement: the separator's antialiased pixels at 4.22:1 (brush 5.02:1) pass, the same 4.22:1 in
    /// a word's rendered pixels fails.
    /// </summary>
    private static async Task CheckSeparatorMutation(Window dialog, ListBoxItem item)
    {
        var fill = RowFill(dialog, item);
        var separator = Find<TextBlock>(item).Single(t => t.Classes.Contains("resultTitle")).Inlines!.Cast<Run>().ElementAt(1);
        var original = separator.Foreground;
        var ink = (ISolidColorBrush)original!;
        var opacity = 1.0;
        while (opacity > 0 && Contrast(Over(new Ink(ink.Color, opacity * ink.Opacity), fill), fill) >= MinContrast) opacity -= 0.01;
        var lowered = new SolidColorBrush(ink.Color, opacity * ink.Opacity);
        separator.Foreground = lowered;
        await PumpAsync();
        Layout(dialog);
        var mutated = RowContrast(dialog, item);
        var dot = mutated.Single(p => p.Part == SeparatorPart);
        separator.Foreground = original;
        await PumpAsync();
        Layout(dialog);
        var restored = RowContrast(dialog, item);
        Console.WriteLine($"  D91 mutation ({RowState(item)}): separator {lowered.Color} at {lowered.Opacity:F2} opacity, {dot.Contrast:F2}:1 / {dot.Rendered:F2}:1; " +
                          $"restored {restored.Single(p => p.Part == SeparatorPart).Contrast:F2}:1");
        Check($"CAT-14 D91 mutation: the separator's brush lowered to {dot.Contrast:F2}:1 (under 4.5) fails the B5 gate; its XAML brush put back, the gate passes",
            dot.Contrast is < MinContrast and > 4.0 && !RowContrastPasses(mutated) && ReferenceEquals(separator.Foreground, original) && RowContrastPasses(restored));

        (string Part, string Text, double Contrast, double Rendered)[] windows =
            [("name", "Radio", 10.81, 10.81), (SeparatorPart, " · ", 5.02, 4.22), ("place", "Place", 4.76, 4.76), ("line 2", "Kind", 4.76, 4.76), ("frequency", "101.5 FM", 8.37, 8.37)];
        var wordPixels = windows.Select(p => p.Part == "place" ? p with { Rendered = 4.22 } : p).ToArray();
        Check("CAT-14 D91 the gate on the Windows measurement: the separator's antialiased pixels (4.22:1, brush 5.02:1) pass; " +
              "the same 4.22:1 in a word's rendered pixels (the place, brush 4.76:1) fails",
            RowContrastPasses(windows) && !RowContrastPasses(wordPixels));
    }

    private static async Task HighlightContrast()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        rig.Catalog.Result = Loaded(Small);
        var (dialog, editor) = await OpenAddAsync(rig);
        await SearchAsync(dialog, editor, "radio");
        var items = Items(dialog);
        var item = items.Single(i => i.IsSelected);
        Check("CAT-14 fixture: the first row (Radio Thessaloniki) is highlighted by the keyboard; three rows, each with a place, a line 2 and a frequency",
            item.DataContext == editor.Results[0] && editor.Results[0] is { Entry.Name: "Radio Thessaloniki" } && items.Count >= 3
            && editor.Results.Take(3).All(r => r is { Place.Length: > 0, Kind.Length: > 0, FrequencyText.Length: > 0 }));
        var other = items[2];

        // Normal, and the keyboard highlight.
        var card = CheckRowContrast(dialog, other, "neither highlighted nor under the pointer");
        var highlight = CheckRowContrast(dialog, item, "highlighted by the keyboard");
        Png(dialog, "catalog-d85-highlight");

        // The pointer highlights the row it moves over: the same row, now selected and under the pointer, keeps the
        // keyboard's highlight (Fluent's lighter selected + pointer-over fill measured the separator 4.14:1, the place and
        // line 2 3.93:1 before).
        var center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseMove(center, RawInputModifiers.None);
        await PumpAsync();
        Layout(dialog);
        Check("CAT-14 fixture: the pointer is over the highlighted row", item.IsPointerOver && item.IsSelected && editor.HighlightedResult == editor.Results[0]);
        var pointer = CheckRowContrast(dialog, item, "highlighted, under the pointer");
        Check("CAT-14 D85 B5 the highlighted row under the pointer has the keyboard highlight's fill", pointer == highlight);
        Png(dialog, "catalog-d85-highlight-pointer");

        // The keyboard moves on while the pointer rests: that row keeps only the hover, the next row the highlight.
        await PressAsync(dialog, Key.Down);
        Layout(dialog);
        Check("CAT-14 fixture: Down moved the highlight to the second row; the pointer still rests on the first",
            item.IsPointerOver && !item.IsSelected && items[1].IsSelected && editor.HighlightedResult == editor.Results[1]);
        var hover = CheckRowContrast(dialog, item, "under the pointer, the highlight on the next row");
        Check("CAT-14 D85 B5 the hover fill differs from both the highlight and the card, so the highlighted row stays the one that stands out",
            hover != highlight && hover != card);
        CheckRowContrast(dialog, items[1], "highlighted by the keyboard, the pointer on the row above");
        Png(dialog, "catalog-d85-hover-and-highlight");

        // Every combination of the three states the row's styles read, on the third row (really in the normal state): the
        // pressed ones do not hold still under real input.
        var pseudo = (IPseudoClasses)other.Classes;
        foreach (var selected in new[] { false, true })
        foreach (var over in new[] { false, true })
        foreach (var pressed in new[] { false, true })
        {
            pseudo.Set(":selected", selected);
            pseudo.Set(":pointerover", over);
            pseudo.Set(":pressed", pressed);
            await PumpAsync();
            Layout(dialog);
            CheckRowContrast(dialog, other, "state set directly");
        }
        // D91's mutation proof, on the state Windows failed before D91 (selected, set directly).
        pseudo.Set(":selected", true);
        pseudo.Set(":pointerover", false);
        pseudo.Set(":pressed", false);
        await PumpAsync();
        Layout(dialog);
        await CheckSeparatorMutation(dialog, other);
        pseudo.Set(":selected", false);

        // A real press on the row under the pointer.
        dialog.MouseDown(center, MouseButton.Left);
        await PumpAsync();
        Layout(dialog);
        Check("CAT-14 fixture: the row under the pointer is pressed", item.Classes.Contains(":pressed"));
        CheckRowContrast(dialog, item, "pressed by the pointer");
        dialog.MouseUp(center, MouseButton.Left);
        await PumpAsync();
        dialog.Close();
        await PumpAsync();
    }

    // ─── CAT-14 D83 D44: the filter drop-downs ───

    private static async Task FilterDropDowns()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        const string longCity = "Saint-Rémy-de-la-Très-Longue-Vallée-sur-Mer-et-Environs";
        var far = new StationCatalogEntry
        {
            Name = "Radio Vallée", Country = "FR", CountryLabel = "France", City = longCity, StreamUrl = "https://streams.example.org/vallee", Votes = 3, Tag = "Radio"
        };
        rig.Catalog.Result = Loaded([.. Small, far]);
        var (dialog, editor) = await OpenAddAsync(rig);
        var city = ByName<ComboBox>(dialog, "City filter");
        await ClickAsync(city);
        var popup = Find<Popup>(city).Single(p => p.Name == "PART_Popup");
        var border = (Control)popup.Child!;
        Layout(dialog);
        border.UpdateLayout();
        var item = Find<ComboBoxItem>(border).Single(i => i.DataContext is CatalogFilterOption { Value: longCity });
        var itemText = Find<TextBlock>(item).Single(t => t.Text == longCity);
        Console.WriteLine($"  City picker {city.Bounds.Width:F1} px, its drop-down {border.Bounds.Width:F1} px; \"{longCity}\" needs {Unconstrained(itemText):F1} px");
        Check($"CAT-14 D83 the City drop-down is wider than its picker ({border.Bounds.Width:F1} > {city.Bounds.Width:F1} px) and at most 280 px",
            city.IsDropDownOpen && border.Bounds.Width > city.Bounds.Width + 0.5 && border.Bounds.Width <= 280.5);
        Check("CAT-14 D44 in the drop-down the long city ends in an ellipsis inside its item (not cut at the edge), with the whole value as its tooltip",
            Unconstrained(itemText) > itemText.Bounds.Width && itemText.TextLayout.TextLines.Any(l => l.HasCollapsed)
            && itemText.TranslatePoint(new Point(itemText.Bounds.Width, 0), border)!.Value.X <= border.Bounds.Width + 0.5
            && ToolTip.GetTip(itemText) as string == longCity);

        await ClickAsync(item);
        Layout(dialog);
        var shown = Find<TextBlock>(city).Single(t => t.IsEffectivelyVisible && t.Text == longCity);
        Check("CAT-14 D44 picked, the closed City picker shows the long city ending in an ellipsis within the picker; the filter applies (Radio Vallée)",
            !city.IsDropDownOpen && editor.SelectedCity.Value == longCity && shown.TextLayout.TextLines.Any(l => l.HasCollapsed)
            && InWindow(shown, dialog).Right <= InWindow(city, dialog).Right + 0.5
            && await WaitAsync(() => editor.PendingSearch.IsCompleted) && editor.Results.Select(r => r.Entry).SequenceEqual([far]));
        var clipped = ClippedTexts(dialog, t => t == shown || t.Classes.Contains("resultTitle"));
        if (clipped.Count > 0) Console.WriteLine("  clipped: " + string.Join("; ", clipped));
        Check("CAT-14 D44 nothing else in the dialog is cut (the picked city's ellipsis is its designed overflow)", clipped.Count == 0);
        Png(dialog, "catalog-d83-long-city");
        dialog.Close();
        await PumpAsync();

        static double Unconstrained(TextBlock t)
        {
            var probe = new TextBlock { Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily, FontWeight = t.FontWeight };
            probe.Measure(Size.Infinity);
            return probe.DesiredSize.Width;
        }
    }

    // ─── CAT-10 D85 P1: the frequency column ───

    /// <summary>
    /// Walks every result row of the open list (scrolling each into view, since the list is virtualized) and names each
    /// row whose frequency is not in its own column right of line 1, not in the accent colour, or trimmed. Returns the rows
    /// walked, those with a frequency, and those whose first line ends in its designed ellipsis.
    /// </summary>
    private static (int Rows, int WithFrequency, int Ellipsized, List<string> Problems) FrequencyColumn(Window dialog, StationEditorViewModel editor)
    {
        var list = ByName<ListBox>(dialog, "Station catalog results");
        var accent = ((ISolidColorBrush)dialog.FindResource("DsAccentBrush")!).Color;
        var problems = new List<string>();
        int rows = 0, withFrequency = 0, ellipsized = 0;
        for (var i = 0; i < editor.Results.Count; i++)
        {
            list.ScrollIntoView(i);
            Layout(dialog);
            var row = editor.Results[i];
            if (list.ContainerFromIndex(i) is not ListBoxItem item) { problems.Add($"row {i} ({row.Name}): not realized"); continue; }
            rows++;
            var frequency = Find<TextBlock>(item).Single(t => t.Classes.Contains("resultFrequency"));
            var title = Find<TextBlock>(item).Single(t => t.Classes.Contains("resultTitle"));
            if (title.TextLayout.TextLines.Any(l => l.HasCollapsed)) ellipsized++;
            if (row.FrequencyText.Length == 0)
            {
                if (frequency.IsEffectivelyVisible) problems.Add($"row {i} ({row.Name}): an empty frequency is shown");
                continue;
            }
            withFrequency++;
            var probe = new TextBlock { Text = frequency.Text, FontSize = frequency.FontSize, FontFamily = frequency.FontFamily, FontWeight = frequency.FontWeight };
            probe.Measure(Size.Infinity);
            var f = InWindow(frequency, dialog);
            var t = InWindow(title, dialog);
            if (!frequency.IsEffectivelyVisible || frequency.Text != row.FrequencyText) problems.Add($"row {i} ({row.Name}): the frequency is not shown");
            else if (probe.DesiredSize.Width > frequency.Bounds.Width + 0.5 || frequency.TextLayout.TextLines.Any(l => l.HasCollapsed))
                problems.Add($"row {i} ({row.Name}): \"{row.FrequencyText}\" needs {probe.DesiredSize.Width:F1} px, has {frequency.Bounds.Width:F1}");
            else if (f.X < t.Right - 0.5 || f.Right > InWindow(item, dialog).Right + 0.5)
                problems.Add($"row {i} ({row.Name}): the frequency {Show(f)} is not in its own column right of line 1 {Show(t)}");
            else if ((frequency.Foreground as ISolidColorBrush)?.Color != accent)
                problems.Add($"row {i} ({row.Name}): the frequency is not in the accent colour");
        }
        foreach (var p in problems.Take(10)) Console.WriteLine("  " + p);
        return (rows, withFrequency, ellipsized, problems);
    }

    /// <summary>A 100-character name (the Name field's limit) on an FM and a medium-wave station: line 1 must give way, the frequency never.</summary>
    private static async Task LongNameFrequencyColumn()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        var name = "Radio " + string.Join(" ", Enumerable.Repeat("Longwave Harbour Community", 4));
        name = name[..StationEditorViewModel.NameMaxLength];
        var fm = new StationCatalogEntry
        {
            Name = name, Country = "GR", CountryLabel = "Greece", City = "Thessaloniki", FrequencyFm = "101.5", StreamUrl = "https://streams.example.org/long-fm", Votes = 2, Tag = "Long"
        };
        var am = new StationCatalogEntry
        {
            Name = name, Country = "DE", CountryLabel = "Germany", City = "Köln", FrequencyFm = "1593", StreamUrl = "https://streams.example.org/long-am", Votes = 1, Tag = "Long"
        };
        rig.Catalog.Result = Loaded([fm, am]);
        foreach (var width in new[] { 620, 680 })
        {
            var (dialog, editor) = await OpenAddAsync(rig);
            dialog.Width = width;
            await PressAsync(dialog, Key.Down);
            Layout(dialog);
            var (rows, withFrequency, ellipsized, problems) = FrequencyColumn(dialog, editor);
            var titles = Items(dialog).Select(i => Find<TextBlock>(i).Single(t => t.Classes.Contains("resultTitle"))).ToList();
            var frequencies = Items(dialog).Select(i => Find<TextBlock>(i).Single(t => t.Classes.Contains("resultFrequency"))).ToList();
            // The detail pane shows the highlighted name in three lines and then its designed ellipsis.
            var cut = ClippedTexts(dialog, t => t.Classes.Contains("resultTitle") || t.Classes.Contains("detailName"));
            if (cut.Count > 0) Console.WriteLine("  clipped: " + string.Join("; ", cut));
            Check($"CAT-10 D85 P1 dialog {width} wide, a {name.Length}-character name: \"101.5 FM\" and \"1593 kHz\" stand whole in their own right-hand column, " +
                  "in the accent colour; the clipping helper reports nothing",
                rows == 2 && withFrequency == 2 && problems.Count == 0 && cut.Count == 0
                && frequencies.Select(f => f.Text).SequenceEqual(["101.5 FM", "1593 kHz"]));
            Check($"CAT-10 D85 P1 dialog {width} wide: line 1 reads \"Name · Place\" and ends in its ellipsis instead",
                ellipsized == 2 && titles.Select(DisplayText).SequenceEqual([$"{name} · Thessaloniki · Greece", $"{name} · Köln · Germany"]));
            Png(dialog, $"catalog-d85-long-name-{width}");
            dialog.Close();
            await PumpAsync();
        }
    }

    /// <summary>The same over the real catalog's 50 most-voted stations, at 620 and 680 wide.</summary>
    private static async Task RealCatalogFrequencyColumn()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(width: 780, height: 650);
        rig.Catalog.Result = await RealAsync();
        foreach (var width in new[] { 620, 680 })
        {
            var (dialog, editor) = await OpenAddAsync(rig);
            dialog.Width = width;
            await PressAsync(dialog, Key.Down);
            Layout(dialog);
            var (rows, withFrequency, ellipsized, problems) = FrequencyColumn(dialog, editor);
            Check($"CAT-10 D85 P1 real catalog, dialog {width} wide: in all {rows} of the 50 most-voted rows the frequency ({withFrequency} rows have one) " +
                  $"sits in its own column right of line 1, in the accent colour, never trimmed ({ellipsized} first lines end in their ellipsis instead)",
                rows == 50 && editor.Results.Count == 50 && withFrequency > 0 && problems.Count == 0);
            dialog.Close();
            await PumpAsync();
        }
    }
}
