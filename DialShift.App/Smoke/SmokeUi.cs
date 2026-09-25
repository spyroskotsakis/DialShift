using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using DialShift.App.ViewModels;

namespace DialShift.App.Smoke;

/// <summary>
/// Drives the real Avalonia windows the way a user does: dialogs are found among the lifetime's open windows, text goes
/// into the real <see cref="TextBox"/>es (so the two-way bindings run), and buttons are pressed through their automation
/// peer's <see cref="IInvokeProvider"/>, the path a screen reader uses. That runs <c>Button.OnClick</c> and so the XAML
/// command binding; raising <c>Button.ClickEvent</c> directly would not. UI thread only.
/// </summary>
internal static class SmokeUi
{
    /// <summary>Longest wait for a dialog to open, or for a command to finish after its dialog closed.</summary>
    public static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Polls until <paramref name="condition"/> holds. Returns whether it did and how long that took.</summary>
    public static async Task<(bool Met, TimeSpan Elapsed)> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, int pollMilliseconds = 100)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return (true, clock.Elapsed);
            if (clock.Elapsed >= timeout) return (false, clock.Elapsed);
            await Task.Delay(pollMilliseconds);
        }
    }

    /// <summary>The newest visible window of type <typeparamref name="T"/>, waiting for it to open.</summary>
    /// <exception cref="TimeoutException">No such window opened within <see cref="DialogTimeout"/>.</exception>
    public static async Task<T> WaitForWindowAsync<T>() where T : Window
    {
        T? found = null;
        var (met, _) = await WaitUntilAsync(() => (found = OpenWindows().OfType<T>().LastOrDefault(w => w.IsVisible)) != null, DialogTimeout);
        if (!met || found == null) throw new TimeoutException($"No {typeof(T).Name} opened within {DialogTimeout.TotalSeconds:0} s.");
        // One more turn so the dialog's Opened handlers (focus) and first layout have run.
        await Task.Delay(150);
        return found;
    }

    public static IReadOnlyList<Window> OpenWindows() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? [];

    /// <summary>Presses the button of <paramref name="window"/> bound to <paramref name="command"/>.</summary>
    /// <exception cref="InvalidOperationException">No enabled, visible button in the window is bound to it.</exception>
    public static void Click(Window window, ICommand command)
    {
        var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.Command, command))
            ?? throw new InvalidOperationException($"{window.GetType().Name} has no button bound to that command.");
        if (!button.IsEffectivelyVisible || !button.IsEffectivelyEnabled)
            throw new InvalidOperationException($"{window.GetType().Name}: the \"{button.Content}\" button is hidden or disabled.");
        if (ControlAutomationPeer.CreatePeerForElement(button) is not IInvokeProvider invoke)
            throw new InvalidOperationException($"{window.GetType().Name}: the \"{button.Content}\" button can't be invoked.");
        invoke.Invoke();
    }

    /// <summary>Types into a text box, replacing its text, as a user edit would.</summary>
    public static void Type(TextBox field, string text)
    {
        field.Focus();
        field.Text = text;
    }

    /// <summary>Waits for a command started by the smoke run to finish once its dialog has closed.</summary>
    /// <exception cref="TimeoutException">It is still running after <see cref="DialogTimeout"/>.</exception>
    public static async Task CompleteAsync(Task command, string what)
    {
        if (await Task.WhenAny(command, Task.Delay(DialogTimeout)) != command)
            throw new TimeoutException($"{what} did not finish within {DialogTimeout.TotalSeconds:0} s.");
        await command;
    }

    /// <summary>Runs a tray menu item's command the way a click does, and waits for it when it is asynchronous.</summary>
    public static async Task InvokeAsync(NativeMenuItem item)
    {
        switch (item.Command)
        {
            case AsyncRelayCommand command:
                await command.ExecuteAsync();
                break;
            case { } command:
                command.Execute(item.CommandParameter);
                break;
            default:
                throw new InvalidOperationException($"The tray item \"{item.Header}\" has no command.");
        }
    }

    /// <summary>
    /// Renders <paramref name="visual"/> at its screen scale to a PNG and returns its pixel size. Throws when the
    /// render is blank (a single color everywhere), which is what a window that never rendered produces.
    /// </summary>
    public static PixelSize Capture(TopLevel visual, string path)
    {
        var scale = visual.RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(visual.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(visual.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(visual);
        if (IsSingleColor(bitmap)) throw new InvalidOperationException($"The {size.Width}x{size.Height} render is blank.");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        return size;
    }

    private static bool IsSingleColor(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        using var copy = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        bitmap.CopyPixels(buffer);
        var row = new int[size.Width];
        int? first = null;
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, size.Width);
            first ??= row[0];
            foreach (var pixel in row)
                if (pixel != first) return false;
        }
        return true;
    }
}
