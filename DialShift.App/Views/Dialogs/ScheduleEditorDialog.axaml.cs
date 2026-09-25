using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using DialShift.App.ViewModels;

namespace DialShift.App.Views.Dialogs;

/// <summary>Time-slot editor (BHV-56). Enter saves, Escape cancels, the label field has focus on open.</summary>
/// <remarks>
/// The time-zone picker is an <see cref="AutoCompleteBox"/>: typing filters the list (<see cref="ScheduleEditorViewModel.MatchesTimeZone"/>),
/// Down or F4 opens it, and Up/Down plus Enter choose while it is open (Enter there does not press Save). "Browse" opens
/// the whole list for pointer users.
/// </remarks>
public partial class ScheduleEditorDialog : Window
{
    private SelectingItemsControl? zoneList;

    public ScheduleEditorDialog() => InitializeComponent();

    public ScheduleEditorDialog(ScheduleEditorViewModel editor) : this()
    {
        DataContext = editor;
        TimeZoneField.ItemFilter = editor.MatchesTimeZone;
        TimeZoneField.TemplateApplied += (_, e) =>
        {
            // The inner text box is what takes focus, so it carries the field's spoken name and selects its text on focus,
            // so typing replaces the current zone.
            if (e.NameScope.Find<TextBox>("PART_TextBox") is { } box)
            {
                AutomationProperties.SetName(box, ScheduleEditorViewModel.TimeZoneLabel);
                box.GotFocus += (_, args) =>
                {
                    if (args.NavigationMethod == NavigationMethod.Tab) box.SelectAll();
                };
            }
            zoneList = e.NameScope.Find<SelectingItemsControl>("PART_SelectingItemsControl");
        };
        // The whole list opens at the current choice, highlighted, so Up/Down start from it.
        TimeZoneField.DropDownOpened += (_, _) =>
        {
            if (zoneList is not { } list || TimeZoneField.SelectedItem is not { } chosen) return;
            list.SelectedItem = chosen;
            Dispatcher.UIThread.Post(() => list.ScrollIntoView(chosen), DispatcherPriority.Loaded);
        };
        BrowseTimeZones.Click += (_, _) =>
        {
            TimeZoneField.Focus();
            TimeZoneField.IsDropDownOpen = true;
        };
        editor.CloseRequested += (_, _) => Close();
        editor.FocusRequested += (_, field) => Field(field)?.Focus();
        Opened += (_, _) => LabelField.Focus();
    }

    private Control? Field(string name) => name switch
    {
        nameof(ScheduleEditorViewModel.Time) => TimeField,
        nameof(ScheduleEditorViewModel.SelectedStation) => StationField,
        nameof(ScheduleEditorViewModel.SelectedTimeZone) => TimeZoneField,
        _ => null
    };
}
