using Avalonia.Controls;
using DialShift.App.ViewModels;

namespace DialShift.App.Views.Dialogs;

/// <summary>Message / confirmation dialog (BHV-64). Enter confirms; Escape cancels (or dismisses a plain message).</summary>
public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    public MessageDialog(MessageDialogViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += (_, _) => Close();
        Opened += (_, _) => ConfirmButton.Focus();
    }
}
