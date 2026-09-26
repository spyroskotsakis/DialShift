using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DialShift.App.ViewModels;

namespace DialShift.App.Views.Dialogs;

/// <summary>
/// Station editor (BHV-52). Enter saves, Escape cancels. Edit mode focuses the name field on open; Add mode focuses the
/// catalog search (D68), or the name field when the catalog is unavailable and the search box is disabled.
/// </summary>
/// <remarks>
/// The search box's keys (docs/catalog-contracts.md §5.5, D85): Down opens the closed results on their first row, or
/// moves the highlight down in the open ones; Up moves it back; Enter picks the highlighted result while the results are
/// open, and while a search the user asked for is still pending it runs that search at once and picks its first row, or
/// nothing on no match, without saving (D87 item 6); otherwise (the results closed, or a pending search that would leave
/// them closed, D89) it falls through to Save; Page Down and Page Up scroll the detail pane (its notes can be long).
/// Escape closes the open results wherever the focus is in the dialog, unless a filter drop-down is open and takes it
/// first; with the results closed it is not handled, so the Cancel button's <c>IsCancel</c> closes the dialog. The
/// results list never takes focus: pointing at a row highlights it, pressing it picks it.
/// </remarks>
public partial class StationEditorDialog : Window
{
    private readonly StationEditorViewModel? editor;

    public StationEditorDialog() => InitializeComponent();

    public StationEditorDialog(StationEditorViewModel editor) : this()
    {
        this.editor = editor;
        DataContext = editor;
        var closeRequested = false;
        var open = true;
        editor.CloseRequested += (_, _) =>
        {
            closeRequested = true;
            if (open) Close();
        };
        // Closed from the title bar: cancel, so the editor stops its catalog work like any other cancel.
        Closed += (_, _) =>
        {
            open = false;
            if (!closeRequested) editor.CancelCommand.Execute(null);
        };
        editor.FocusRequested += (_, field) => Field(field)?.Focus();
        Opened += (_, _) => (editor.IsAddMode && SearchBox.IsEffectivelyEnabled ? SearchBox : NameField).Focus();
        if (!editor.IsAddMode) return;

        editor.PropertyChanged += OnEditorPropertyChanged;
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        SearchBox.AddHandler(TappedEvent, (_, _) => ReopenResults(), handledEventsToo: true);
        ResultsList.PointerMoved += (_, e) =>
        {
            if (RowAt(e.Source) is { } row) editor.HighlightedResult = row;
        };
        ResultsList.AddHandler(TappedEvent, (_, e) =>
        {
            if (RowAt(e.Source) is not { } row) return;
            editor.HighlightedResult = row;
            editor.SelectEntryCommand.Execute(null);
        }, handledEventsToo: true);
        foreach (var field in new[] { NameField, TagField, UrlField })
            field.GotFocus += (_, _) => editor.IsResultsOpen = false;
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private TextBox? Field(string name) => name switch
    {
        nameof(StationEditorViewModel.Name) => NameField,
        nameof(StationEditorViewModel.Url) => UrlField,
        _ => null
    };

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (editor == null || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Down:
                // Opening highlights the first row; only an open list moves the highlight.
                if (editor.IsResultsOpen) editor.MoveHighlight(1);
                else if (editor.Results.Count > 0) editor.IsResultsOpen = true;
                e.Handled = true;
                break;
            case Key.Up:
                editor.MoveHighlight(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                // Picks the top match of the text as typed (D87 item 6, D89); when not handled, Enter goes on to Save.
                e.Handled = editor.PickOnEnter();
                break;
            case Key.PageDown:
                DetailScroll.PageDown();
                e.Handled = true;
                break;
            case Key.PageUp:
                DetailScroll.PageUp();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Escape closes the open results wherever the focus is, before the Cancel button sees it. A filter's open drop-down
    /// is left to close itself first.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || editor is not { IsResultsOpen: true }) return;
        if (FilterRow.GetLogicalDescendants().OfType<ComboBox>().Any(c => c.IsDropDownOpen)) return;
        editor.IsResultsOpen = false;
        e.Handled = true;
    }

    /// <summary>A click in the search box brings back the last results the user closed.</summary>
    private void ReopenResults()
    {
        if (editor is { Results.Count: > 0 }) editor.IsResultsOpen = true;
    }

    /// <summary>A press anywhere but the overlay, the search box or the filters closes the results.</summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (editor is not { IsResultsOpen: true } || e.Source is not Visual source) return;
        if (source == ResultsOverlay || source == SearchBox || source == FilterRow
            || ResultsOverlay.IsVisualAncestorOf(source) || SearchBox.IsVisualAncestorOf(source) || FilterRow.IsVisualAncestorOf(source))
            return;
        editor.IsResultsOpen = false;
    }

    /// <summary>The search box is disabled when the catalog turns out unavailable; typing then starts in the name field.</summary>
    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StationEditorViewModel.IsCatalogLoading) || editor is not { IsCatalogLoading: false, IsCatalogAvailable: false })
            return;
        var focused = FocusManager?.GetFocusedElement();
        if (focused == null || ReferenceEquals(focused, SearchBox)) NameField.Focus();
    }

    private static CatalogResultRow? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as CatalogResultRow;
}
