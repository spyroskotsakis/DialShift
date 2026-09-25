using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DialShift.App.Platform;
using DialShift.App.Tray;
using DialShift.App.ViewModels;
using DialShift.App.Views;
using DialShift.App.Views.Dialogs;
using DialShift.Core;
using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.Headless;

namespace DialShift.Tests.Ui;

/// <summary>
/// Headless UI suites on the real views, dialogs and tray (Avalonia.Headless 12.1.2 + Skia, D5): HS-01 rendering and PNGs,
/// HS-02 editor flows through the real dialogs located by accessible name, HS-03 tray-menu identity after every editor
/// operation, HS-04 tray routing, HS-05 close/minimize/hide, HS-07 dialog ownership, HS-13 rendered player state,
/// the compact 780×650 layout and accessible names. The App startup/quit sequence (HS-05/06/08, BHV-11) is in
/// <see cref="AppLifecycleTests"/> and the real heartbeat (HS-14) in <see cref="PlaybackLoopTests"/>; this suite runs all three.
/// </summary>
public static class HeadlessUiTests
{
    public static async Task RunAsync()
    {
        await Headless.RunAsync(WindowShellAndPages);
        await Headless.RunAsync(CompactLayout);
        await Headless.RunAsync(AccessibleNames);
        await Headless.RunAsync(PlayerCard);
        await Headless.RunAsync(CloseMinimizeHide);
        await Headless.RunAsync(DialogOwnershipAndKeys);
        await Headless.RunAsync(StationEditorFlow);
        await Headless.RunAsync(ScheduleEditorFlow);
        await Headless.RunAsync(TrayMenuItemsAndRouting);
        await Headless.RunAsync(SettingsPageControls);
        await AppLifecycleTests.RunAsync();
        await PlaybackLoopTests.RunAsync();
        await Headless.RunAsync(LongStationNameInPickers);
    }

    // ─── helpers ───

    /// <summary>Texts of the effectively visible text blocks under <paramref name="root"/> (buttons' generated text blocks included).</summary>
    internal static List<string> Texts(Visual root) =>
        Find<TextBlock>(root).Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!).ToList();

    internal static bool Shows(Visual root, string text) => Texts(root).Contains(text);

    internal static TextBlock? VisibleError(Window window) =>
        Find<TextBlock>(window).FirstOrDefault(t => t.Classes.Contains("error") && t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));

    internal static IInputElement? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    internal static Task ShowPage(UiRig rig, string tab) => ShowPage(rig.Window!, tab);

    internal static async Task ShowPage(Window window, string tab)
    {
        await ClickAsync(ButtonWithText(window, tab));
        Layout(window);
    }

    /// <summary>
    /// Text blocks whose text does not fit: trimmed with an ellipsis, wider than their arranged box (single line), taller
    /// than their box (wrapping), or running past the window's right edge. Measured with an unconstrained copy that carries
    /// the effective font properties.
    /// </summary>
    internal static List<string> ClippedTexts(Window window)
    {
        var clipped = new List<string>();
        foreach (var tb in Find<TextBlock>(window).Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 0))
        {
            var probe = new TextBlock
            {
                Text = tb.Text, FontSize = tb.FontSize, FontFamily = tb.FontFamily, FontWeight = tb.FontWeight, FontStyle = tb.FontStyle,
                FontStretch = tb.FontStretch, LetterSpacing = tb.LetterSpacing, Padding = tb.Padding, TextWrapping = tb.TextWrapping
            };
            var wraps = tb.TextWrapping != TextWrapping.NoWrap;
            probe.Measure(wraps ? new Size(tb.Bounds.Width, double.PositiveInfinity) : Size.Infinity);
            var right = tb.TranslatePoint(new Point(tb.Bounds.Width, 0), window)?.X ?? 0;
            string? reason = null;
            if (tb.TextLayout.TextLines.Any(l => l.HasCollapsed)) reason = "trimmed";
            else if (!wraps && probe.DesiredSize.Width > tb.Bounds.Width + 0.5) reason = $"needs {probe.DesiredSize.Width:F1} px, has {tb.Bounds.Width:F1}";
            else if (wraps && probe.DesiredSize.Height > tb.Bounds.Height + 0.5) reason = $"needs {probe.DesiredSize.Height:F1} px high, has {tb.Bounds.Height:F1}";
            else if (right > window.ClientSize.Width + 0.5) reason = $"ends at x={right:F1}, window is {window.ClientSize.Width:F0} wide";
            if (reason != null) clipped.Add($"\"{tb.Text}\" ({reason})");
        }
        return clipped;
    }

    // ─── HS-01: shell, pages, PNGs ───

    private static async Task WindowShellAndPages()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(realCoordinator: true);
        var window = rig.Window!;
        Check("HS-01 BHV-24 window: title \"DialShift\", 1050×860, minimum 780×650, centered",
            window.Title == "DialShift" && window.Width == 1050 && window.Height == 860 && window.MinWidth == 780 && window.MinHeight == 650
            && window.WindowStartupLocation == WindowStartupLocation.CenterScreen);
        Check("HS-01 BHV-24 D1 dark Fluent theme with the DialShift palette (#10191B window, #C2F278 accent)",
            Application.Current!.ActualThemeVariant == ThemeVariant.Dark && window.Background is ISolidColorBrush { Color: var bg } && bg == Color.Parse("#10191B")
            && window.TryFindResource("DsAccentBrush", out var accent) && accent is ISolidColorBrush { Color: var ac } && ac == Color.Parse("#C2F278"));
        Check("HS-01 BHV-24 header: ◴ brand, \"DialShift\", \"YOUR RADIO, ON TIME.\", \"↘  Hide to tray\"",
            Shows(window, "◴") && Shows(window, "DialShift") && Shows(window, "YOUR RADIO, ON TIME.") && Shows(window, "↘  Hide to tray"));
        Check("HS-01 BHV-24 tabs Stations / Schedule / Settings, Stations selected",
            new[] { "Stations", "Schedule", "Settings" }.All(t => ButtonWithText(window, t).Classes.Contains("tab"))
            && ButtonWithText(window, "Stations").Classes.Contains("selected") && !ButtonWithText(window, "Schedule").Classes.Contains("selected"));
        Check("HS-01 BHV-01 BHV-25 first launch renders \"READY WHEN YOU ARE\", the default title and track, \"▶  Play\", \"Skip  →\", 60%",
            Shows(window, "READY WHEN YOU ARE") && Shows(window, "Your next favorite frequency.") && Shows(window, "Choose a station and make yourself at home.")
            && Shows(window, "▶  Play") && Shows(window, "Skip  →") && Shows(window, "60%") && Find<Slider>(window).Single().Value == 60);
        Check("HS-01 BHV-29 footer: \"SCHEDULE OFF · You're in control\" and \"LOCAL TIME · <standard name>\"",
            Shows(window, "SCHEDULE OFF · You're in control") && Shows(window, "LOCAL TIME · " + TimeZoneInfo.Utc.StandardName));
        Check("HS-01 BHV-51 Stations page: title, \"03  SAVED FREQUENCIES\", \"+  Add station\", credit line",
            Shows(window, "Your stations") && Shows(window, "03  SAVED FREQUENCIES") && Shows(window, "+  Add station")
            && Shows(window, "Starter stations by SomaFM. Add your Greek favorites with their direct stream URLs."));
        Check("HS-01 BHV-51 each row: initial tile, name, tag, \"▶  Listen\" and \"Edit\"",
            new[] { ("G", "Groove Salad", "SomaFM · Ambient / downtempo"), ("D", "Drone Zone", "SomaFM · Atmospheric"), ("S", "Secret Agent", "SomaFM · Cinematic grooves") }
                .All(r => Shows(window, r.Item1) && Shows(window, r.Item2) && Shows(window, r.Item3))
            && Find<Button>(window).Count(b => b.Content as string == "▶  Listen") == 3 && Find<Button>(window).Count(b => b.Content as string == "Edit") == 3);
        Check("HS-01 BHV-01 nothing played while rendering", rig.Engine!.Starts.Count == 0);
        Console.WriteLine("  PNG: " + Screenshot(window, "stations"));

        await ShowPage(rig, "Schedule");
        Check("HS-01 BHV-55 Schedule page: title, \"Follow my schedule\" (off), \"+  Add time slot\" (enabled), Mon–Sun tabs with Mon selected",
            Shows(window, "Make radio a routine") && Find<CheckBox>(window).Single(c => c.Content as string == "Follow my schedule").IsChecked == false
            && ButtonWithText(window, "+  Add time slot").IsEffectivelyEnabled
            && UiText.Week.All(d => Shows(window, UiText.ShortDay(d))) && ButtonWithText(window, "Mon").Classes.Contains("selected"));
        Check("HS-01 BHV-55 empty Monday: \"A little room for spontaneity.\" / \"No switches on Monday. …\" and the helper text",
            Shows(window, "A little room for spontaneity.") && Shows(window, "No switches on Monday. Add a time slot to tune in automatically.")
            && Texts(window).Any(t => t.StartsWith("Each slot runs in its own time zone (Local time by default).", StringComparison.Ordinal)));
        Check("HS-01 BHV-24 the selected tab moves to Schedule", ButtonWithText(window, "Schedule").Classes.Contains("selected") && !ButtonWithText(window, "Stations").Classes.Contains("selected"));
        Console.WriteLine("  PNG: " + Screenshot(window, "schedule"));

        await ShowPage(rig, "Settings");
        Check("HS-01 BHV-59 BHV-60 BHV-61 BHV-62 Settings page: both checkboxes, fallback picker, About version, \"Open settings folder ↗\"",
            Shows(window, "Set it. Forget it.") && Find<CheckBox>(window).Any(c => c.Content as string == "Launch DialShift in the tray when I sign in")
            && Find<CheckBox>(window).Any(c => c.Content as string == "Start in the tray when opened normally")
            && ByName<ComboBox>(window, "Fallback station").SelectedItem is FallbackOption { Id: null }
            && Shows(window, "DialShift  /  " + UiRig.Version) && Shows(window, "Open settings folder ↗"));
        Console.WriteLine("  PNG: " + Screenshot(window, "settings"));

        var mark = rig.Journal.Count;
        await ClickAsync(ButtonWithText(window, "Open settings folder ↗"));
        Check("HS-01 BHV-63 the button reveals the data directory", rig.Reveal.Paths.SequenceEqual([rig.Paths.DataDirectory]) && rig.Journal.Count == mark);
    }

    /// <summary>
    /// UI-D1 (fixed in b37f8ec, regression guard): station names may be 100 characters (BHV-52). Like everywhere else (player
    /// title, row title, footer), the slot editor's station picker and the Settings fallback picker end a long name in an
    /// ellipsis instead of cutting it mid-glyph at the picker's edge.
    /// </summary>
    private static async Task LongStationNameInPickers()
    {
        var longName = "Radio " + new string('W', 94);
        await using var rig = await UiRig.CreateHeadlessAsync(seed: s =>
        {
            s.Stations[0].Name = longName;
            s.FallbackStationId = s.Stations[0].Id;
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[0].Id, Time = "11:00", Days = [DayOfWeek.Monday] });
        });
        var window = rig.Window!;
        await ShowPage(rig, "Settings");
        ByName<ComboBox>(window, "Fallback station").BringIntoView();
        Layout(window);
        var fallbackClipped = ClippedTexts(window).Where(t => t.StartsWith("\"Radio WWW", StringComparison.Ordinal) && !t.Contains("trimmed", StringComparison.Ordinal)).ToList();
        Console.WriteLine("  Settings: " + string.Join("; ", fallbackClipped));
        Check("UI-D1 the Settings fallback picker trims a long station name with an ellipsis instead of cutting it", fallbackClipped.Count == 0);

        await ShowPage(rig, "Schedule");
        var count = OpenedWindows.Count;
        _ = rig.ViewModel.Schedule.Slots[0].EditCommand.ExecuteAsync();
        var slot = await WaitForWindowAsync<ScheduleEditorDialog>(count);
        Layout(slot);
        // The name cannot fit the fixed-width dialog: trimmed with an ellipsis is the expected outcome, anything else is not.
        var slotClipped = ClippedTexts(slot).Where(t => !(t.StartsWith("\"Radio WWW", StringComparison.Ordinal) && t.Contains("(trimmed)", StringComparison.Ordinal))).ToList();
        Console.WriteLine("  slot editor: " + string.Join("; ", slotClipped));
        Console.WriteLine("  PNG: " + Screenshot(slot, "ui-d1-slot-editor"));
        Check("UI-D1 the slot editor's station picker trims a long station name with an ellipsis instead of cutting it", slotClipped.Count == 0);
    }

    // ─── compact 780×650: nothing clipped ───

    private static async Task CompactLayout()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(realCoordinator: true, width: 780, height: 650, seed: s =>
        {
            s.ScheduleEnabled = true;
            s.FallbackStationId = s.Stations[1].Id;
            s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[0].Id, Time = "11:00", Days = [.. UiText.Week], Label = "Late-morning ambient set" });
        });
        var window = rig.Window!;

        var control = new Window
        {
            Width = 300, Height = 120,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "A text that cannot fit in eighty pixels", Width = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left },
                    new TextBlock { Text = "Trimmed text that is far too long", Width = 80, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left },
                    new TextBlock { Text = "Fits" }
                }
            }
        };
        control.Show();
        Layout(control);
        var probe = ClippedTexts(control);
        Check("HS-01 compact: the clipping probe flags an overflowing and a trimmed text block, not one that fits (negative control)",
            probe.Count == 2 && probe.Any(p => p.Contains("trimmed", StringComparison.Ordinal)) && !probe.Any(p => p.StartsWith("\"Fits\"", StringComparison.Ordinal)));
        control.Close();

        await rig.Coordinator.StartScheduleAsync();
        await rig.ViewModel.Stations.Rows[2].ListenCommand.ExecuteAsync();
        rig.Engine!.RaiseState(rig.Engine.LastSessionId, PlaybackEngineState.Playing);
        await WaitAsync(() => rig.ViewModel.StatusText == "LIVE BROADCAST");
        Layout(window);
        Check("HS-01 compact: the window is at its 780×650 minimum", window.ClientSize == new Size(780, 650));
        foreach (var page in new[] { "Stations", "Schedule", "Settings" })
        {
            await ShowPage(rig, page);
            var clipped = ClippedTexts(window);
            var examined = Find<TextBlock>(window).Count(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));
            if (clipped.Count > 0) Console.WriteLine($"  clipped on {page}: " + string.Join("; ", clipped));
            Check($"HS-01 compact 780×650 {page} page: none of its {examined} visible text blocks is trimmed or clipped (desired ≤ arranged size)",
                clipped.Count == 0 && examined >= 15);
            Console.WriteLine("  PNG: " + Screenshot(window, "compact-" + page.ToLowerInvariant()));
        }

        var count = OpenedWindows.Count;
        _ = rig.ViewModel.Stations.Rows[0].EditCommand.ExecuteAsync();
        var station = await WaitForWindowAsync<StationEditorDialog>(count);
        Layout(station);
        Check("HS-02 compact: the station editor shows every label unclipped", ClippedTexts(station).Count == 0);
        Console.WriteLine("  PNG: " + Screenshot(station, "station-editor"));
        station.Close();
        await PumpAsync();

        await ShowPage(rig, "Schedule");
        count = OpenedWindows.Count;
        _ = rig.ViewModel.Schedule.Slots[0].EditCommand.ExecuteAsync();
        var slot = await WaitForWindowAsync<ScheduleEditorDialog>(count);
        Layout(slot);
        var slotClipped = ClippedTexts(slot);
        if (slotClipped.Count > 0) Console.WriteLine("  clipped in the slot editor: " + string.Join("; ", slotClipped));
        Check("HS-02 compact: the schedule editor shows every label unclipped", slotClipped.Count == 0);
        Console.WriteLine("  PNG: " + Screenshot(slot, "schedule-editor"));
        slot.Close();
        await PumpAsync();
    }

    // ─── BHV-65: accessible names ───

    /// <summary>
    /// Visible interactive controls the views declare (template parts such as a slider's track buttons are part of their
    /// owner) whose spoken name has no letters: empty, or only a symbol such as "▶". Printed when not empty.
    /// </summary>
    internal static List<string> Unnamed(Visual root)
    {
        var unnamed = Find<Control>(root)
            .Where(c => c.IsEffectivelyVisible && c.TemplatedParent == null && c is Button or ToggleButton or Slider or ComboBox or TextBox or AutoCompleteBox)
            .Where(c => !AccessibleName(c).Any(char.IsLetter))
            .Select(c => $"{c.GetType().Name} \"{(c as ContentControl)?.Content}\" named \"{AccessibleName(c)}\"")
            .ToList();
        if (unnamed.Count > 0) Console.WriteLine("  without a spoken name: " + string.Join("; ", unnamed));
        return unnamed;
    }

    private static async Task AccessibleNames()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(seed: s => s.Schedule.Add(new ScheduleEntry { StationId = s.Stations[0].Id, Time = "08:00", Days = [DayOfWeek.Monday] }));
        var window = rig.Window!;
        Check("HS-02 BHV-65 icon-led buttons carry plain names: Play/Pause \"Play\", Skip \"Next station\", hide \"Hide to tray\"",
            ByName<Button>(window, "Play").Content as string == "▶  Play" && ByName<Button>(window, "Next station").Content as string == "Skip  →"
            && ByName<Button>(window, "Hide to tray").Content as string == "↘  Hide to tray");
        Check("HS-02 BHV-65 BHV-28 the volume slider is \"Playback volume\"", ByName<Slider>(window, "Playback volume") != null);
        Check("HS-02 BHV-65 row buttons name their station: \"Listen to Groove Salad\", \"Edit Groove Salad\"",
            ByName<Button>(window, "Listen to Groove Salad").Content as string == "▶  Listen" && ByName<Button>(window, "Edit Groove Salad").Content as string == "Edit");
        Check("HS-02 BHV-65 every button, slider and input on the Stations page has a spoken name", Unnamed(window).Count == 0);
        await ShowPage(rig, "Schedule");
        Check("HS-02 BHV-65 day tabs and slot rows: \"Show Monday slots\", \"Edit the 08:00 slot\"",
            ByName<Button>(window, "Show Monday slots").Content as string == "Mon" && ByName<Button>(window, "Edit the 08:00 slot") != null);
        Check("HS-02 BHV-65 every control on the Schedule page has a spoken name", Unnamed(window).Count == 0);
        await ShowPage(rig, "Settings");
        Check("HS-02 BHV-65 BHV-61 the fallback picker is \"Fallback station\"", ByName<ComboBox>(window, "Fallback station") != null);
        Check("HS-02 BHV-65 every control on the Settings page has a spoken name", Unnamed(window).Count == 0);

        var count = OpenedWindows.Count;
        _ = rig.ViewModel.Stations.AddCommand.ExecuteAsync();
        var station = await WaitForWindowAsync<StationEditorDialog>(count);
        Check("HS-02 BHV-52 BHV-65 station editor fields are named by their labels",
            ByName<TextBox>(station, StationEditorViewModel.NameLabel) != null && ByName<TextBox>(station, StationEditorViewModel.TagLabel) != null
            && ByName<TextBox>(station, StationEditorViewModel.UrlLabel) != null && Unnamed(station).Count == 0);
        station.Close();
        await PumpAsync();

        count = OpenedWindows.Count;
        _ = rig.ViewModel.Schedule.AddCommand.ExecuteAsync();
        var slot = await WaitForWindowAsync<ScheduleEditorDialog>(count);
        var unnamed = Unnamed(slot);
        Check("HS-02 BHV-56 BHV-65 slot editor: label, \"Scheduled station\" combo, time field, \"Time zone\" picker, \"Show all time zones\" and day boxes are named",
            ByName<TextBox>(slot, ScheduleEditorViewModel.LabelLabel) != null && ByName<ComboBox>(slot, ScheduleEditorViewModel.StationAutomationName) != null
            && ByName<TextBox>(slot, ScheduleEditorViewModel.TimeLabel) != null && ByName<AutoCompleteBox>(slot, ScheduleEditorViewModel.TimeZoneLabel) != null
            && ByName<Button>(slot, ScheduleEditorViewModel.BrowseTimeZonesName).Content as string == "Browse" && unnamed.Count == 0);
        var zoneText = Find<TextBox>(ByName<AutoCompleteBox>(slot, ScheduleEditorViewModel.TimeZoneLabel)).Single();
        Check("HS-02 BHV-65 brief 2 §4.5 the picker's inner text box (the part that takes focus) is also named \"Time zone\"",
            AccessibleName(zoneText) == ScheduleEditorViewModel.TimeZoneLabel);
        slot.Close();
        await PumpAsync();
    }

    // ─── HS-13: the rendered player card follows the coordinator ───

    private static async Task PlayerCard()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(realCoordinator: true);
        var window = rig.Window!;
        var engine = rig.Engine!;

        await ClickAsync(ByName<Button>(window, "Listen to Drone Zone"));
        await WaitAsync(() => Shows(window, "CONNECTING…"));
        Check("HS-13 HS-14 BHV-30 a real click on Listen renders \"CONNECTING…\", the station title and \"Ⅱ  Pause\"",
            Shows(window, "CONNECTING…") && Shows(window, "Drone Zone") && Shows(window, "Opening the live stream") && ByName<Button>(window, "Pause").Content as string == "Ⅱ  Pause");
        Check("HS-13 BHV-51 the row on air is highlighted", Find<Border>(window).Count(b => b.Classes.Contains("card") && b.Classes.Contains("current")) == 1);
        Check("HS-13 BHV-25 the status dot is busy while connecting", Find<Avalonia.Controls.Shapes.Ellipse>(window).Single(e => e.Classes.Contains("statusDot")).Classes.Contains("busy"));

        engine.RaiseState(engine.LastSessionId, PlaybackEngineState.Playing);
        Check("HS-13 HS-14 BHV-31 engine Playing renders \"LIVE BROADCAST\" with the live dot",
            await WaitAsync(() => Shows(window, "LIVE BROADCAST")) && Find<Avalonia.Controls.Shapes.Ellipse>(window).Single(e => e.Classes.Contains("statusDot")).Classes.Contains("live"));

        var slider = ByName<Slider>(window, "Playback volume");
        var mark = rig.Journal.Count;
        slider.Value = 25;
        await WaitAsync(() => engine.VolumeCalls.Count > 0 && engine.VolumeCalls[^1] == 0.25);
        Check("HS-13 BHV-28 moving the slider forwards v/100 to the engine and renders \"25%\"", Shows(window, "25%") && engine.VolumeCalls[^1] == 0.25);
        Check("HS-13 BHV-28 moving alone does not save (persist on release)", rig.Journal.Since(mark).All(e => e != "settings.save"));
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right, Source = slider });
        await WaitAsync(() => rig.Journal.Since(mark).Contains("settings.save"));
        Check("HS-13 BHV-28 BHV-17 releasing a key on the slider persists the volume", rig.OnDisk().Volume == 25);
        slider.Value = 0;
        await WaitAsync(() => engine.VolumeCalls[^1] == 0.0);
        var pressAt = slider.TranslatePoint(new Point(2, slider.Bounds.Height / 2), window)!.Value;
        window.MouseDown(pressAt, MouseButton.Left);
        window.MouseUp(pressAt, MouseButton.Left);
        await WaitAsync(() => rig.OnDisk().Volume == 0);
        Check("HS-13 BHV-28 volume 0 mutes the engine, renders \"0%\", and a pointer release persists it", engine.VolumeCalls[^1] == 0.0 && Shows(window, "0%") && rig.OnDisk().Volume == 0);

        await ClickAsync(ByName<Button>(window, "Pause"));
        Check("HS-13 HS-14 BHV-33 Pause renders \"PAUSED\", the guidance text and \"▶  Play\"",
            await WaitAsync(() => Shows(window, "PAUSED")) && Shows(window, "Press play to return to the live broadcast.") && ByName<Button>(window, "Play") != null);
        Check("HS-13 BHV-25 BHV-33 paused: the title still names the station (the coordinator keeps it for the next play); no row is highlighted",
            Shows(window, "Drone Zone") && !Shows(window, UiText.DefaultTitle) && !Find<Border>(window).Any(b => b.Classes.Contains("card") && b.Classes.Contains("current")));
        Console.WriteLine("  PNG: " + Screenshot(window, "player-paused"));
    }

    // ─── HS-05: close / minimize / hide keep running ───

    private static async Task CloseMinimizeHide()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(realCoordinator: true);
        var window = rig.Window!;
        await rig.ViewModel.Stations.Rows[0].ListenCommand.ExecuteAsync();
        var session = rig.Engine!.ActiveSessionId;

        window.Close();
        await PumpAsync();
        Check("HS-05 BHV-12 closing the window hides it instead (close to tray)", !window.IsVisible);
        Check("HS-05 BHV-12 audio keeps running: same engine session, still active, no stop", rig.Engine.ActiveSessionId == session && rig.Real!.Snapshot.IsActive && rig.Engine.StopCount == 0);
        Check("HS-05 BHV-12 closing does not quit", rig.Journal.Entries.All(e => e != "shell.Quit"));

        window.Show();
        await PumpAsync();
        window.WindowState = WindowState.Minimized;
        await PumpAsync();
        Check("HS-05 BHV-13 minimizing hides the window, audio unaffected", !window.IsVisible && rig.Engine.ActiveSessionId == session);

        window.Show();
        window.WindowState = WindowState.Normal;
        await PumpAsync();
        Layout(window);
        var mark = rig.Journal.Count;
        await ClickAsync(ByName<Button>(window, "Hide to tray"));
        Check("HS-05 BHV-14 the header button asks the shell to hide (the App hides the window; HS-05 App part)", rig.Since(mark) == "shell.HideMainWindow" && rig.Engine.ActiveSessionId == session);
        Check("HS-05 BHV-14 OQ-3 no balloon: the button's tooltip explains instead", ToolTip.GetTip(ByName<Button>(window, "Hide to tray")) as string == "Keep DialShift running in the tray");
    }

    // ─── BHV-64: dialogs have a safe owner; Enter / Escape ───

    private static async Task DialogOwnershipAndKeys()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        var dialogs = rig.AvaloniaDialogs!;
        var window = rig.Window!;

        var count = OpenedWindows.Count;
        var message = dialogs.ShowMessageAsync("DialShift · Test", "A message long enough to wrap across more than one line of the dialog, so its wrapping is visible.");
        var dialog = await WaitForWindowAsync<MessageDialog>(count);
        Check("HS-07 BHV-64 with the main window visible, a message is owned by it and centered on it",
            dialog.Owner == window && dialog.Owner != dialog && dialog.WindowStartupLocation == WindowStartupLocation.CenterOwner);
        Check("HS-07 BHV-64 the text is selectable and wraps; a plain message shows only OK",
            Find<SelectableTextBlock>(dialog).Count() == 2 && Find<SelectableTextBlock>(dialog).All(t => t.TextWrapping == TextWrapping.Wrap)
            && Find<Button>(dialog).Count(b => b.IsEffectivelyVisible) == 1 && Shows(dialog, "OK"));
        Check("HS-07 BHV-65 OK is the default button and has focus", Focused(dialog) is Button { IsDefault: true, Content: "OK" });
        await PressAsync(dialog, Key.Escape);
        Check("HS-07 BHV-65 Escape dismisses a plain message", await CompletesAsync(message) && !dialog.IsVisible);

        count = OpenedWindows.Count;
        var confirm = dialogs.ConfirmAsync("Delete station", "Delete Drone Zone?", "Delete", "Cancel");
        dialog = await WaitForWindowAsync<MessageDialog>(count);
        Check("HS-07 BHV-64 a confirmation shows Cancel and Delete, Delete focused", Shows(dialog, "Cancel") && Shows(dialog, "Delete") && Focused(dialog) is Button { Content: "Delete" });
        await PressAsync(dialog, Key.Escape);
        Check("HS-07 BHV-65 Escape answers the cancel button (false)", await CompletesAsync(confirm) && !confirm.Result);

        count = OpenedWindows.Count;
        confirm = dialogs.ConfirmAsync("Delete station", "Delete Drone Zone?", "Delete", "Cancel");
        dialog = await WaitForWindowAsync<MessageDialog>(count);
        await PressAsync(dialog, Key.Enter);
        Check("HS-07 BHV-65 Enter answers the default button (true)", await CompletesAsync(confirm) && confirm.Result);

        count = OpenedWindows.Count;
        confirm = dialogs.ConfirmAsync("Q", "Question?");
        dialog = await WaitForWindowAsync<MessageDialog>(count);
        await ClickAsync(ButtonWithText(dialog, "No"));
        Check("HS-07 BHV-64 default wording No/Yes; clicking No answers false", await CompletesAsync(confirm) && !confirm.Result);

        window.Hide();
        await PumpAsync();
        count = OpenedWindows.Count;
        message = dialogs.ShowMessageAsync("DialShift · Settings recovered", "Your settings could not be read.");
        dialog = await WaitForWindowAsync<MessageDialog>(count);
        Check("HS-07 BHV-64 with no visible window (start in tray) the dialog is ownerless, centered on screen, on top — never its own owner",
            dialog.Owner == null && dialog.WindowStartupLocation == WindowStartupLocation.CenterScreen && dialog.Topmost);
        await PressAsync(dialog, Key.Enter);
        Check("HS-07 BHV-64 Enter closes the ownerless message", await CompletesAsync(message) && !dialog.IsVisible && !window.IsVisible);
    }

    // ─── HS-02 + HS-03: station editor through the real dialogs; tray identity after every operation ───

    /// <summary>HS-03 tray identity: the tray's menu and icon stay the instances it started with, and the menu follows <paramref name="settings"/>.</summary>
    internal sealed class TrayProbe(TrayMenuController tray, Settings settings)
    {
        private readonly NativeMenu root = tray.RootMenu;
        private readonly TrayIcon icon = tray.TrayIcon;

        public TrayProbe(UiRig rig) : this(rig.Tray!, rig.Settings) { }

        public async Task CheckAfter(string operation)
        {
            await PumpAsync();
            var items = root.Items.OfType<NativeMenuItem>().ToList();
            var stations = items.Single(i => i.Header == "Stations").Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
            var follow = items.Single(i => i.Header == "Follow schedule");
            var expected = settings.Stations.Count == 0 ? ["No saved stations"] : settings.Stations.Select(s => s.Name).ToList();
            Check($"HS-03 MX-05 BHV-22 after {operation}: TrayIcon.Menu is still the original RootMenu instance",
                ReferenceEquals(icon.Menu, root) && ReferenceEquals(tray.RootMenu, root) && ReferenceEquals(tray.TrayIcon, icon));
            Check($"HS-03 MX-05 BHV-22 after {operation}: the icon is registered once (the same TrayIcon)",
                TrayIcon.GetIcons(Application.Current!) is { Count: 1 } icons && ReferenceEquals(icons[0], icon));
            Check($"HS-03 BHV-22 after {operation}: Stations ▸ lists the current stations ({string.Join(", ", expected)})", stations.SequenceEqual(expected));
            Check($"HS-03 BHV-50 after {operation}: \"Follow schedule\" check matches the setting ({settings.ScheduleEnabled})",
                follow.IsChecked == settings.ScheduleEnabled && follow.ToggleType == MenuItemToggleType.CheckBox);
        }
    }

    private static async Task StationEditorFlow()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(tray: true);
        var window = rig.Window!;
        var tray = new TrayProbe(rig);
        await tray.CheckAfter("startup");

        // Cancel with Escape.
        var count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(window, "+  Add station"));
        var editor = await WaitForWindowAsync<StationEditorDialog>(count);
        var name = ByName<TextBox>(editor, StationEditorViewModel.NameLabel);
        var tag = ByName<TextBox>(editor, StationEditorViewModel.TagLabel);
        var url = ByName<TextBox>(editor, StationEditorViewModel.UrlLabel);
        Check("HS-02 BHV-64 the editor is owned by the main window, never by itself", editor.Owner == window && editor.Owner != editor);
        Check("HS-02 BHV-52 the add dialog: \"Add a frequency · DialShift\", no delete button, name field focused",
            editor.Title == "Add a frequency · DialShift" && !Find<Button>(editor).Any(b => b.IsEffectivelyVisible && b.Content as string == "Delete station") && Focused(editor) == name);
        Check("HS-02 BHV-52 the fields enforce 100 / 160 / 2048 characters", name.MaxLength == 100 && tag.MaxLength == 160 && url.MaxLength == 2048);
        Check("HS-02 BHV-65 Save is the default button and Cancel the cancel button",
            ButtonWithText(editor, "Save").IsDefault && ButtonWithText(editor, "Cancel").IsCancel);
        await TypeAsync(name, "Typed then cancelled");
        await PressAsync(editor, Key.Escape);
        Check("HS-02 BHV-52 Escape cancels: dialog closed, nothing added, nothing saved", !editor.IsVisible && rig.Settings.Stations.Count == 3 && !rig.SavedToDisk);
        await tray.CheckAfter("station cancel (Escape)");

        // Cancel button.
        count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(window, "+  Add station"));
        editor = await WaitForWindowAsync<StationEditorDialog>(count);
        await ClickAsync(ButtonWithText(editor, "Cancel"));
        Check("HS-02 BHV-52 the Cancel button closes without changes", !editor.IsVisible && rig.Settings.Stations.Count == 3);
        await tray.CheckAfter("station cancel (button)");

        // Validation, then add with Enter.
        count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(window, "+  Add station"));
        editor = await WaitForWindowAsync<StationEditorDialog>(count);
        name = ByName<TextBox>(editor, StationEditorViewModel.NameLabel);
        url = ByName<TextBox>(editor, StationEditorViewModel.UrlLabel);
        await TypeAsync(url, "https://radio.example.org/kosmos");
        await PressAsync(editor, Key.Enter);
        Check("HS-02 BHV-52 Enter with no name keeps the dialog open: \"Give this station a name.\", name field focused",
            editor.IsVisible && VisibleError(editor)?.Text == "Give this station a name." && Focused(editor) == name);
        await TypeAsync(name, "Kosmos");
        Check("HS-02 BHV-52 typing clears the message", VisibleError(editor) == null);
        await TypeAsync(url, "radio.example.org/kosmos");
        await PressAsync(editor, Key.Enter);
        Check("HS-02 BHV-52 Enter with a bad URL: \"Enter a valid HTTP or HTTPS stream URL.\", URL field focused",
            editor.IsVisible && VisibleError(editor)?.Text == "Enter a valid HTTP or HTTPS stream URL." && Focused(editor) == url);
        Console.WriteLine("  PNG: " + Screenshot(editor, "station-editor-error"));
        await TypeAsync(url, "https://radio.example.org/kosmos");
        await PressAsync(editor, Key.Enter);
        Check("HS-02 BHV-52 Enter saves: dialog closed, \"Kosmos\" listed, \"04  SAVED FREQUENCIES\"",
            await WaitAsync(() => !editor.IsVisible && Shows(window, "Kosmos")) && Shows(window, "04  SAVED FREQUENCIES") && Shows(window, "Internet radio"));
        Check("HS-02 MX-01 the new station is on disk", rig.OnDisk().Stations.Any(s => s.Name == "Kosmos" && s.Url == "https://radio.example.org/kosmos"));
        await tray.CheckAfter("station add");

        // Edit with the Save button.
        count = OpenedWindows.Count;
        await ClickAsync(ByName<Button>(window, "Edit Kosmos"));
        editor = await WaitForWindowAsync<StationEditorDialog>(count);
        name = ByName<TextBox>(editor, StationEditorViewModel.NameLabel);
        Check("HS-02 BHV-52 the edit dialog is prefilled and offers \"Delete station\"",
            editor.Title == "Edit station · DialShift" && name.Text == "Kosmos" && ByName<TextBox>(editor, StationEditorViewModel.UrlLabel).Text == "https://radio.example.org/kosmos"
            && ButtonWithText(editor, "Delete station").IsEffectivelyVisible);
        await TypeAsync(name, "Kosmos 93.6");
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check("HS-02 BHV-52 MX-01 Save renames it on screen and on disk",
            await WaitAsync(() => !editor.IsVisible && Shows(window, "Kosmos 93.6")) && rig.OnDisk().Stations.Any(s => s.Name == "Kosmos 93.6"));
        await tray.CheckAfter("station edit");

        // Delete: confirmation owned by the editor; Escape keeps, Enter deletes.
        rig.Settings.Schedule.Add(new ScheduleEntry { StationId = rig.Settings.Stations[^1].Id, Time = "06:00", Days = [DayOfWeek.Monday] });
        count = OpenedWindows.Count;
        await ClickAsync(ByName<Button>(window, "Edit Kosmos 93.6"));
        editor = await WaitForWindowAsync<StationEditorDialog>(count);
        count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(editor, "Delete station"));
        var confirm = await WaitForWindowAsync<MessageDialog>(count);
        Check("HS-02 BHV-53 BHV-64 the delete confirmation is owned by the editor and asks \"Delete Kosmos 93.6 and its 1 schedule slot(s)?\"",
            confirm.Owner == editor && Shows(confirm, "Delete Kosmos 93.6 and its 1 schedule slot(s)?") && Shows(confirm, "Delete") && Shows(confirm, "Cancel"));
        await PressAsync(confirm, Key.Escape);
        Check("HS-02 BHV-53 Escape on the confirmation keeps the station and the editor open", !confirm.IsVisible && editor.IsVisible && rig.Settings.Stations.Count == 4);
        count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(editor, "Delete station"));
        confirm = await WaitForWindowAsync<MessageDialog>(count);
        await PressAsync(confirm, Key.Enter);
        Check("HS-02 BHV-53 Enter confirms: both dialogs close, the station and its slot are gone",
            await WaitAsync(() => !editor.IsVisible && !Shows(window, "Kosmos 93.6")) && rig.Settings.Schedule.Count == 0 && Shows(window, "03  SAVED FREQUENCIES"));
        Check("HS-02 BHV-53 MX-01 deletion persisted", rig.OnDisk().Stations.Count == 3 && rig.OnDisk().Schedule.Count == 0);
        Check("HS-02 BHV-53 the coordinator was told to forget it before the commit",
            rig.Journal.Entries.SkipWhile(e => !e.StartsWith("coordinator.ForgetStationAsync", StringComparison.Ordinal)).Skip(1).FirstOrDefault() == "settings.commit:Stations, Schedule");
        await tray.CheckAfter("station delete");
    }

    // ─── HS-02 + HS-03: slot editor ───

    private static async Task ScheduleEditorFlow()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(tray: true);
        var window = rig.Window!;
        var tray = new TrayProbe(rig);
        await ShowPage(rig, "Schedule");

        var follow = Find<CheckBox>(window).Single(c => c.Content as string == "Follow my schedule");
        var mark = rig.Journal.Count;
        await ClickAsync(follow);
        Check("HS-03 HS-04 BHV-50 the page checkbox turns the schedule on through commit(Schedule)",
            rig.Settings.ScheduleEnabled && rig.Since(mark) == "settings.commit:Schedule > coordinator.RefreshScheduleAsync" && rig.OnDisk().ScheduleEnabled);
        await tray.CheckAfter("schedule toggle (page)");
        var trayFollow = rig.Tray!.RootMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Follow schedule");
        mark = rig.Journal.Count;
        trayFollow.Command!.Execute(null);
        await WaitAsync(() => !rig.Settings.ScheduleEnabled && rig.Journal.Count > mark + 1);
        Check("HS-03 HS-04 BHV-50 the tray item turns it off through the same commit path and the page checkbox follows",
            !rig.Settings.ScheduleEnabled && rig.Since(mark) == "settings.commit:Schedule > coordinator.RefreshScheduleAsync" && follow.IsChecked == false);
        await tray.CheckAfter("schedule toggle (tray)");

        // Add: invalid time, then Enter.
        var count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(window, "+  Add time slot"));
        var editor = await WaitForWindowAsync<ScheduleEditorDialog>(count);
        var label = ByName<TextBox>(editor, ScheduleEditorViewModel.LabelLabel);
        var time = ByName<TextBox>(editor, ScheduleEditorViewModel.TimeLabel);
        var station = ByName<ComboBox>(editor, ScheduleEditorViewModel.StationAutomationName);
        Check("HS-02 BHV-56 BHV-64 the slot editor is owned by the main window; label field focused; first station; 08:00; Monday checked",
            editor.Owner == window && Focused(editor) == label && (station.SelectedItem as Station)?.Name == "Groove Salad" && time.Text == "08:00"
            && Find<CheckBox>(editor).Where(c => c.IsChecked == true).Select(c => c.Content as string).SequenceEqual(["Mon", "Enable this time slot"]));
        Check("HS-02 BHV-56 limits: label 150, time 5", label.MaxLength == 150 && time.MaxLength == 5);
        await TypeAsync(time, "25:00");
        await PressAsync(editor, Key.Enter);
        Check("HS-02 BHV-56 an invalid time keeps the dialog open with \"Use a 24-hour time, such as 08:30 or 21:00.\" and focuses the time",
            editor.IsVisible && VisibleError(editor)?.Text == "Use a 24-hour time, such as 08:30 or 21:00." && Focused(editor) == time);
        await TypeAsync(time, "08:00");
        await TypeAsync(label, "Breakfast");
        await PressAsync(editor, Key.Enter);
        Check("HS-02 BHV-56 Enter saves: 08:00 Groove Salad \"Breakfast · Mon\" is listed",
            await WaitAsync(() => !editor.IsVisible && Shows(window, "08:00")) && Shows(window, "Breakfast · Mon") && rig.OnDisk().Schedule.Single().Time == "08:00");
        await tray.CheckAfter("slot add");

        // Conflict warning.
        count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(window, "+  Add time slot"));
        editor = await WaitForWindowAsync<ScheduleEditorDialog>(count);
        await PressAsync(editor, Key.Enter);
        Check("HS-02 BHV-57 same time and day as an enabled slot: \"Another enabled slot already starts at this time on one of those days.\"",
            editor.IsVisible && VisibleError(editor)?.Text == "Another enabled slot already starts at this time on one of those days.");
        Console.WriteLine("  PNG: " + Screenshot(editor, "schedule-editor-conflict"));
        await PressAsync(editor, Key.Escape);
        Check("HS-02 BHV-57 Escape leaves the conflicting slot unsaved", !editor.IsVisible && rig.Settings.Schedule.Count == 1);
        await tray.CheckAfter("slot conflict");

        // Edit with a preset and the Save button.
        count = OpenedWindows.Count;
        await ClickAsync(ByName<Button>(window, "Edit the 08:00 slot"));
        editor = await WaitForWindowAsync<ScheduleEditorDialog>(count);
        Check("HS-02 BHV-56 the edit dialog is prefilled and offers \"Delete slot\"",
            editor.Title == "Edit time slot · DialShift" && ByName<TextBox>(editor, ScheduleEditorViewModel.TimeLabel).Text == "08:00" && ButtonWithText(editor, "Delete slot").IsEffectivelyVisible);
        await TypeAsync(ByName<TextBox>(editor, ScheduleEditorViewModel.TimeLabel), "09:30");
        await ClickAsync(ButtonWithText(editor, "Weekdays"));
        await ClickAsync(ButtonWithText(editor, "Save"));
        Check("HS-02 BHV-56 Save updates the row: 09:30 on Mon–Fri, same slot id",
            await WaitAsync(() => !editor.IsVisible && Shows(window, "09:30")) && Shows(window, "Breakfast · Mon, Tue, Wed, Thu, Fri") && rig.OnDisk().Schedule.Single().Time == "09:30");
        await tray.CheckAfter("slot edit");

        // Delete with confirmation.
        count = OpenedWindows.Count;
        await ClickAsync(ByName<Button>(window, "Edit the 09:30 slot"));
        editor = await WaitForWindowAsync<ScheduleEditorDialog>(count);
        count = OpenedWindows.Count;
        await ClickAsync(ButtonWithText(editor, "Delete slot"));
        var confirm = await WaitForWindowAsync<MessageDialog>(count);
        Check("HS-02 BHV-58 BHV-64 deleting a slot confirms, owned by the editor",
            confirm.Owner == editor && Shows(confirm, "Delete the 09:30 switch to Groove Salad? It repeats on Mon, Tue, Wed, Thu, Fri."));
        await PressAsync(confirm, Key.Enter);
        Check("HS-02 BHV-58 confirming removes the slot and shows Monday's empty state",
            await WaitAsync(() => !editor.IsVisible && Shows(window, "No switches on Monday. Add a time slot to tune in automatically.")) && rig.OnDisk().Schedule.Count == 0);
        await tray.CheckAfter("slot delete");
    }

    // ─── HS-04: tray items, order, routing, tooltip ───

    private static async Task TrayMenuItemsAndRouting()
    {
        await using var rig = await UiRig.CreateHeadlessAsync(tray: true);
        var tray = rig.Tray!;
        string Header(NativeMenuItemBase item) => item switch { NativeMenuItemSeparator => "—", NativeMenuItem m => m.Header ?? "", _ => "?" };
        Check("HS-04 BHV-21 tray items in order: Open, Play, Next station, Stations ▸, Follow schedule, —, Volume +10, Volume −10, —, Quit DialShift",
            tray.RootMenu.Items.Select(Header).SequenceEqual(["Open DialShift", "Play", "Next station", "Stations", "Follow schedule", "—", "Volume +10", "Volume −10", "—", "Quit DialShift"]));
        var stationItems = tray.RootMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Stations").Menu!.Items.OfType<NativeMenuItem>().ToList();
        Check("HS-04 BHV-21 Stations ▸ has one radio item per station", stationItems.Count == 3 && stationItems.All(i => i.ToggleType == MenuItemToggleType.Radio && !i.IsChecked));
        Check("HS-04 BHV-19 D4 the icon is visible; macOS uses the template image", tray.TrayIcon.IsVisible && MacOSProperties.GetIsTemplateIcon(tray.TrayIcon) == OperatingSystem.IsMacOS());
        Check("HS-13 BHV-23 initial tooltip \"DialShift · Paused\"", tray.TrayIcon.ToolTipText == "DialShift · Paused");

        async Task<string> Run(string header, NativeMenu? menu = null)
        {
            var item = (menu ?? tray.RootMenu).Items.OfType<NativeMenuItem>().Single(i => i.Header == header);
            var mark = rig.Journal.Count;
            item.Command!.Execute(null);
            await WaitAsync(() => rig.Journal.Count > mark && (!rig.Journal.Entries[^1].StartsWith("coordinator.", StringComparison.Ordinal) || rig.Journal.Entries[^1] == "coordinator.RefreshScheduleAsync"));
            await PumpAsync();
            return rig.Since(mark);
        }

        Check("HS-04 MX-04 BHV-15 \"Open DialShift\" → shell.ShowMainWindow", await Run("Open DialShift") == "shell.ShowMainWindow");
        Check("HS-04 MX-04 BHV-26 BHV-17 OQ-11 \"Play\" → ToggleAsync, then a save", await Run("Play") == "coordinator.ToggleAsync > settings.save");
        Check("HS-04 MX-04 BHV-27 BHV-17 OQ-11 \"Next station\" → NextStationAsync, then a save", await Run("Next station") == "coordinator.NextStationAsync > settings.save");
        var stations = tray.RootMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Stations").Menu!;
        Check("HS-04 MX-04 BHV-17 a station item → PlayAsync(that station), then a save",
            await Run("Drone Zone", stations) == $"coordinator.PlayAsync:{rig.Settings.Stations[1].Id} > settings.save");
        Check("HS-04 MX-04 BHV-50 \"Follow schedule\" → commit(Schedule) → RefreshScheduleAsync", await Run("Follow schedule") == "settings.commit:Schedule > coordinator.RefreshScheduleAsync" && rig.OnDisk().ScheduleEnabled);
        Check("HS-04 MX-04 BHV-28 BHV-17 \"Volume +10\" → SetVolumeAsync(70), then a save", await Run("Volume +10") == "coordinator.SetVolumeAsync:70 > settings.save");
        Check("HS-04 MX-04 BHV-28 BHV-17 \"Volume −10\" → SetVolumeAsync(50), then a save", await Run("Volume −10") == "coordinator.SetVolumeAsync:50 > settings.save");
        Check("HS-04 MX-04 BHV-11 \"Quit DialShift\" → shell.Quit (the one quit path)", await Run("Quit DialShift") == "shell.Quit");

        var root = tray.RootMenu;
        var rebuilds = tray.RebuildCount;
        var drone = rig.Settings.Stations[1];
        var live = new PlaybackSnapshot(PlaybackStatus.Playing, drone.Id, drone.Name, drone.Id, drone.Name, true, true, false, "Live broadcast", "", null, null, null, 60);
        for (var i = 0; i < 5; i++) rig.Fake!.Publish(live with { TrackText = "tick " + i });
        await PumpAsync();
        Check("HS-04 BHV-21 a burst of snapshots rebuilds the menu once (coalesced), in place",
            tray.RebuildCount == rebuilds + 1 && ReferenceEquals(tray.TrayIcon.Menu, root) && ReferenceEquals(tray.RootMenu, root));
        Check("HS-04 BHV-21 while active the second item reads \"Pause\" and the station on air is checked",
            root.Items.OfType<NativeMenuItem>().ElementAt(1).Header == "Pause"
            && root.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Stations").Menu!.Items.OfType<NativeMenuItem>().Single(i => i.IsChecked).Header == "Drone Zone");
        Check("HS-13 BHV-23 tooltip follows the snapshot: \"DialShift · Drone Zone\"", tray.TrayIcon.ToolTipText == "DialShift · Drone Zone");
        rig.Fake!.Publish(live with { CurrentStationName = new string('n', 90) });
        await PumpAsync();
        Check("HS-13 BHV-23 a long station name is cut to 63 characters in the tooltip", tray.TrayIcon.ToolTipText!.Length == 63);
        rig.Fake.Publish(live with { TrackText = "only the track changed" });
        await PumpAsync();
        Check("HS-04 BHV-21 a snapshot that changes nothing the menu shows does not rebuild it", tray.RebuildCount == rebuilds + 1);

        tray.Dispose();
        Check("HS-04 BHV-11 disposing the tray hides the icon", !tray.TrayIcon.IsVisible);
    }

    // ─── Settings page controls (BHV-59 inline diagnostic, BHV-60, BHV-61) ───

    private static async Task SettingsPageControls()
    {
        await using var rig = await UiRig.CreateHeadlessAsync();
        var window = rig.Window!;
        await rig.ViewModel.InitializeAsync();
        await ShowPage(rig, "Settings");
        var login = Find<CheckBox>(window).Single(c => c.Content as string == "Launch DialShift in the tray when I sign in");
        var inTray = Find<CheckBox>(window).Single(c => c.Content as string == "Start in the tray when opened normally");

        rig.Startup.Hold = new TaskCompletionSource();
        rig.Startup.SetResult = _ => new StartupRegistrationStatus(false, "Couldn't write the login item: permission denied.");
        await ClickAsync(login);
        Check("HS-13 BHV-59 while the OS write runs the checkbox is disabled", !login.IsEffectivelyEnabled && rig.Startup.SetCalls.SequenceEqual([true]));
        rig.Startup.Hold.SetResult();
        await WaitAsync(() => login.IsEffectivelyEnabled);
        Check("HS-13 BHV-59 a failed write reverts the checkbox, with no second registration call",
            login.IsChecked == false && rig.Startup.SetCalls.SequenceEqual([true]));
        Check("HS-13 BHV-59 the diagnostic is shown inline under the checkbox", VisibleError(window)?.Text == "Couldn't write the login item: permission denied.");
        Console.WriteLine("  PNG: " + Screenshot(window, "settings-startup-diagnostic"));
        rig.Startup.SetResult = null;
        rig.Startup.Hold = null;
        await ClickAsync(login);
        Check("HS-13 BHV-59 a successful write checks it and clears the diagnostic",
            await WaitAsync(() => login.IsChecked == true && VisibleError(window) == null) && rig.OnDisk().LaunchAtLogin && rig.Startup.SetCalls.SequenceEqual([true, true]));
        Check("HS-13 DOD-09 startup_registration.result was logged for the check and both writes",
            rig.Log.Entries.Count(e => e.EventName == "startup_registration.result") == 3);

        var mark = rig.Journal.Count;
        await ClickAsync(inTray);
        Check("HS-06 BHV-60 the start-in-tray checkbox saves the setting", inTray.IsChecked == true && rig.Since(mark) == "settings.save" && rig.OnDisk().StartInTray);

        var combo = ByName<ComboBox>(window, "Fallback station");
        combo.SelectedIndex = 3;
        await WaitAsync(() => rig.Settings.FallbackStationId != null);
        await ShowPage(rig, "Stations");
        Check("HS-02 BHV-61 picking a fallback in the combo persists it and marks the row \" · Fallback\"",
            rig.OnDisk().FallbackStationId == rig.Settings.Stations[2].Id && Shows(window, "SomaFM · Cinematic grooves · Fallback"));
    }
}
