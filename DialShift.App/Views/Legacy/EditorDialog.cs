using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using static DialShift.App.Views.Legacy.MainWindow;

namespace DialShift.App.Views.Legacy;

public class EditorDialog : Window
{
    protected readonly StackPanel Form = new();
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Spacing = 8 };
    protected readonly TextBlock Error = Text("", 13, false, "#FFB5A7", new Thickness(0, 10, 0, 0));

    protected EditorDialog(string title, string description)
    {
        Title = title + " · DialShift"; Width = 580; Height = 630; MinWidth = 510; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new DockPanel { Margin = new Thickness(26) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(Text(title, 26, true)); header.Children.Add(Text(description, 13, false, "#A3B4B6", new Thickness(0, 8, 0, 0)));
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        DockPanel.SetDock(Actions, Dock.Bottom); panel.Children.Add(Actions);
        DockPanel.SetDock(Error, Dock.Bottom); panel.Children.Add(Error);
        panel.Children.Add(new ScrollViewer { Content = Form }); Content = panel;
    }

    protected TextBox Field(string label, string value, int maxLength = 500)
    {
        Form.Children.Add(Text(label, 13, true));
        var field = new TextBox { Text = value, MaxLength = maxLength };
        Form.Children.Add(field); return field;
    }

    protected void FinishButtons(Action save)
    {
        var cancel = Button("Cancel", () => Close(false)); cancel.IsCancel = true; Actions.Children.Add(cancel);
        var submit = Button("Save", save, true); submit.IsDefault = true; Actions.Children.Add(submit);
    }
}
