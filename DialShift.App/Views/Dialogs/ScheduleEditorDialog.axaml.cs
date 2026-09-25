using Avalonia.Controls;
using DialShift.App.ViewModels;

namespace DialShift.App.Views.Dialogs;

/// <summary>Time-slot editor (BHV-56). Enter saves, Escape cancels, the label field has focus on open.</summary>
public partial class ScheduleEditorDialog : Window
{
    public ScheduleEditorDialog() => InitializeComponent();

    public ScheduleEditorDialog(ScheduleEditorViewModel editor) : this()
    {
        DataContext = editor;
        editor.CloseRequested += (_, _) => Close();
        editor.FocusRequested += (_, field) => Field(field)?.Focus();
        Opened += (_, _) => LabelField.Focus();
    }

    private Control? Field(string name) => name switch
    {
        nameof(ScheduleEditorViewModel.Time) => TimeField,
        nameof(ScheduleEditorViewModel.SelectedStation) => StationField,
        _ => null
    };
}
