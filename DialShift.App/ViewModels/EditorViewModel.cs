namespace DialShift.App.ViewModels;

/// <summary>How an editor dialog ended.</summary>
public enum EditorResult
{
    Cancelled,
    Saved,
    Deleted
}

/// <summary>Opens the editor dialogs. The Avalonia implementation owns each dialog by the window it was opened from.</summary>
public interface IEditorDialogService
{
    Task<EditorResult> ShowStationEditorAsync(StationEditorViewModel editor);

    Task<EditorResult> ShowScheduleEditorAsync(ScheduleEditorViewModel editor);
}

/// <summary>
/// Shared editor state: title, description, inline error, and Save/Cancel/Delete. Save validates and applies to
/// <c>Settings</c>; the caller then commits through <see cref="ISettingsService"/>. Delete only confirms and reports
/// <see cref="EditorResult.Deleted"/>; the caller performs the deletion so the coordinator is told first.
/// </summary>
public abstract class EditorViewModel : ObservableObject
{
    private string? error;

    protected EditorViewModel(string title, string description, bool canDelete, Action<Exception> onError)
    {
        Title = title;
        Description = description;
        CanDelete = canDelete;
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(EditorResult.Cancelled));
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, onError, () => CanDelete);
    }

    public string Title { get; }

    public string Description { get; }

    public bool CanDelete { get; }

    public abstract string DeleteLabel { get; }

    /// <summary>Inline validation message; null when valid.</summary>
    public string? Error
    {
        get => error;
        protected set
        {
            if (SetProperty(ref error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(error);

    public EditorResult Result { get; private set; } = EditorResult.Cancelled;

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    /// <summary>Asks the view to close. Raised once per editor.</summary>
    public event EventHandler<EditorResult>? CloseRequested;

    /// <summary>Raised when validation fails, with the name of the field that should take focus (for example "Name").</summary>
    public event EventHandler<string>? FocusRequested;

    /// <summary>Sets a field value; any edit clears a stale validation message.</summary>
    protected void Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName)) Error = null;
    }

    protected abstract void Save();

    protected abstract Task DeleteAsync();

    protected void Fail(string message, string? focusField = null)
    {
        Error = message;
        if (focusField != null) FocusRequested?.Invoke(this, focusField);
    }

    protected void Close(EditorResult result)
    {
        Result = result;
        CloseRequested?.Invoke(this, result);
    }
}
