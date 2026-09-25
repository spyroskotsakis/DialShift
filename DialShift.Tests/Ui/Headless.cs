using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DialShift.Tests.Ui;

/// <summary>
/// The Avalonia app the headless suites run: the real <see cref="DialShift.App.App"/> (its XAML theme and styles, D1) on the
/// headless platform with the Skia renderer, so frames can be captured to PNG (D5). <see cref="HeadlessUnitTestSession"/>
/// finds <see cref="BuildAvaloniaApp"/> by name.
/// </summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DialShift.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Runs headless UI scenarios inside the console runner (no xunit). One <see cref="HeadlessUnitTestSession"/> per process,
/// with <see cref="AvaloniaTestIsolationLevel.PerTest"/>: every <see cref="RunAsync"/> call gets a fresh
/// <see cref="Application"/> and dispatcher, so an <c>App.Run</c> scenario that quits cannot leak into the next one.
/// </summary>
/// <remarks>
/// The session is deliberately never disposed: in Avalonia 12.1.2 <c>HeadlessUnitTestSession.Dispose</c> blocks forever
/// once a scenario has shown windows, while its dispatcher thread is a background thread and ends with the process.
/// </remarks>
public static class Headless
{
    private static readonly Lock gate = new();
    private static readonly List<Window> opened = [];
    private static HeadlessUnitTestSession? session;
    private static bool hooked;

    /// <summary>A generous bound for UI waits; scenarios finish in milliseconds, this only catches hangs.</summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs <paramref name="scenario"/> on the headless UI thread with a fresh app. Exceptions (failed checks) propagate.</summary>
    public static async Task RunAsync(Func<Task> scenario)
    {
        HeadlessUnitTestSession current;
        lock (gate) current = session ??= HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry), AvaloniaTestIsolationLevel.PerTest);
        await current.Dispatch(async () =>
        {
            HookWindowOpened();
            lock (gate) opened.Clear();
            try { await scenario(); }
            finally { CloseAll(); }
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>Every window opened in the current scenario, in opening order.</summary>
    public static IReadOnlyList<Window> OpenedWindows { get { lock (gate) return [.. opened]; } }

    /// <summary>Lets posted work, bindings and layout run: a few dispatcher turns.</summary>
    public static async Task PumpAsync(int turns = 3)
    {
        for (var i = 0; i < turns; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps until <paramref name="condition"/> holds; false after <see cref="WaitTimeout"/>.</summary>
    public static async Task<bool> WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout; // real time on purpose: bounds a test's wait only
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await PumpAsync(1);
        }
        return true;
    }

    /// <summary>Waits for the next visible window of type <typeparamref name="T"/> opened after <paramref name="alreadyOpen"/> windows.</summary>
    public static async Task<T> WaitForWindowAsync<T>(int alreadyOpen) where T : Window
    {
        T? found = null;
        var ok = await WaitAsync(() => (found = OpenedWindows.Skip(alreadyOpen).OfType<T>().LastOrDefault(w => w.IsVisible)) != null);
        if (!ok || found == null) throw new TimeoutException($"No {typeof(T).Name} opened within {WaitTimeout.TotalSeconds:0} s.");
        await PumpAsync();
        return found;
    }

    /// <summary>Waits for <paramref name="task"/> while pumping the dispatcher; false on timeout.</summary>
    public static Task<bool> CompletesAsync(Task task) => WaitAsync(() => task.IsCompleted);

    /// <summary>Runs a layout pass on <paramref name="window"/> now, then renders a frame (hit testing uses the rendered scene).</summary>
    public static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static IEnumerable<T> Find<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    /// <summary>The single visible control of type <typeparamref name="T"/> whose effective accessible name is <paramref name="name"/>.</summary>
    public static T ByName<T>(Visual root, string name) where T : Control
    {
        var matches = Find<T>(root).Where(c => c.IsEffectivelyVisible && AccessibleName(c) == name).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"Expected one visible {typeof(T).Name} named \"{name}\", found {matches.Count}.");
    }

    /// <summary>The visible button whose content text is <paramref name="text"/>.</summary>
    public static Button ButtonWithText(Visual root, string text)
    {
        var matches = Find<Button>(root).Where(b => b.IsEffectivelyVisible && b.Content as string == text).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"Expected one visible button \"{text}\", found {matches.Count}.");
    }

    /// <summary>What assistive technology announces: the automation peer's name (explicit name, else content text).</summary>
    public static string AccessibleName(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control).GetName() is { Length: > 0 } name ? name : AutomationProperties.GetName(control) ?? "";

    /// <summary>
    /// A real left click through the headless input pipeline at the control's center. It first brings the control into
    /// view and checks the hit test lands on it (or inside it), so a covered or clipped button fails loudly.
    /// </summary>
    /// <remarks>
    /// Hit testing reads the rendered scene. After <see cref="Control.BringIntoView"/> scrolls, the scene can lag the layout
    /// by a frame, so the check is retried (re-laying out and re-rendering) until it lands, for at most <see cref="WaitTimeout"/>.
    /// </remarks>
    public static async Task ClickAsync(Control control)
    {
        var window = TopLevel.GetTopLevel(control) as Window ?? throw new InvalidOperationException("The control is not in a window.");
        control.BringIntoView();
        Point center = default;
        Visual? hit = null;
        bool Lands()
        {
            Layout(window);
            center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("The control has no position in its window.");
            hit = window.InputHitTest(center) as Visual;
            return hit != null && (hit == control || control.IsVisualAncestorOf(hit));
        }
        if (!await WaitAsync(Lands))
            throw new InvalidOperationException($"A click at the center of {control.GetType().Name} would hit {hit?.GetType().Name ?? "nothing"}.");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await PumpAsync();
    }

    /// <summary>Presses and releases <paramref name="key"/> on <paramref name="window"/> (routed to the focused element). A key that closed the window is not released.</summary>
    public static async Task PressAsync(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible) window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        await PumpAsync();
    }

    /// <summary>Focuses <paramref name="box"/> and replaces its text as typing would (select all, then text input).</summary>
    public static async Task TypeAsync(TextBox box, string text)
    {
        box.Focus();
        box.SelectAll();
        var window = (Window)TopLevel.GetTopLevel(box)!;
        if (text.Length == 0) await PressAsync(window, Key.Back);
        else window.KeyTextInput(text);
        await PumpAsync();
    }

    /// <summary>Saves the last rendered frame of <paramref name="window"/> as a PNG under <see cref="ScreenshotDirectory"/>.</summary>
    public static string Screenshot(Window window, string name)
    {
        Directory.CreateDirectory(ScreenshotDirectory);
        var path = Path.Combine(ScreenshotDirectory, name + ".png");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return path;
    }

    /// <summary>Where HS-01/HS-02 PNGs go: <c>DIALSHIFT_HEADLESS_SCREENSHOTS</c> when set, else next to the test binaries.</summary>
    public static string ScreenshotDirectory =>
        Environment.GetEnvironmentVariable("DIALSHIFT_HEADLESS_SCREENSHOTS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "headless-screenshots");

    private static void HookWindowOpened()
    {
        lock (gate)
        {
            if (hooked) return;
            hooked = true;
        }
        // Class handlers are process-wide, so this runs once and records windows of every later scenario.
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => { lock (gate) opened.Add(window); });
    }

    /// <summary>Closes whatever a scenario left open (a failed check mid-dialog), newest first, so its tasks end.</summary>
    private static void CloseAll()
    {
        foreach (var window in OpenedWindows.Reverse())
        {
            try { window.Close(); }
            catch (Exception) { /* best effort: a scenario's own failure is what gets reported */ }
        }
    }
}
