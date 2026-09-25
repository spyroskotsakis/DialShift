using Avalonia.Controls;
using DialShift.App.ViewModels;

namespace DialShift.App.Views.Dialogs;

/// <summary>Station editor (BHV-52). Enter saves, Escape cancels, the name field has focus on open.</summary>
public partial class StationEditorDialog : Window
{
    public StationEditorDialog() => InitializeComponent();

    public StationEditorDialog(StationEditorViewModel editor) : this()
    {
        DataContext = editor;
        editor.CloseRequested += (_, _) => Close();
        editor.FocusRequested += (_, field) => Field(field)?.Focus();
        Opened += (_, _) => NameField.Focus();
    }

    private TextBox? Field(string name) => name switch
    {
        nameof(StationEditorViewModel.Name) => NameField,
        nameof(StationEditorViewModel.Url) => UrlField,
        _ => null
    };
}
