using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static DialShift.App.Views.Legacy.MainWindow;

namespace DialShift.App.Views.Legacy;

public static class Message
{
    public static Task Show(Window? owner, string title, string message) => ShowInternal(owner, title, message, confirm: false);
    public static Task<bool> Confirm(Window? owner, string title, string message) => ShowInternal(owner, title, message, confirm: true);

    private static async Task<bool> ShowInternal(Window? owner, string title, string message, bool confirm)
    {
        var result = false;
        var window = new Window
        {
            Title = title,
            Width = 460, Height = 250,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brush("#EFF6F0"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, FontSize = 14, Foreground = Brush("#A3B4B6"), TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Spacing = 8 };
        if (confirm)
        {
            var no = Button("No", () => { result = false; window.Close(); }); no.IsCancel = true;
            var yes = Button("Yes", () => { result = true; window.Close(); }, true); yes.IsDefault = true;
            actions.Children.Add(no); actions.Children.Add(yes);
        }
        else
        {
            var ok = Button("OK", () => window.Close(), true); ok.IsDefault = true;
            actions.Children.Add(ok);
        }
        panel.Children.Add(actions);
        window.Content = panel;
        if (owner != null) await window.ShowDialog(owner);
        else await window.ShowDialog(window);
        return result;
    }
}
