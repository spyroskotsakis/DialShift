namespace DialShift.App.ViewModels;

/// <summary>A message (OK) or confirmation (confirm/cancel) dialog (BHV-64). The confirm button is the default.</summary>
public sealed class MessageDialogViewModel
{
    public MessageDialogViewModel(string title, string message, string confirmText = "OK", string? cancelText = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText ?? "";
        IsConfirmation = cancelText != null;
        ConfirmCommand = new RelayCommand(() => Close(true));
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }

    /// <summary>True for a question with a cancel button; false for a plain message whose OK also answers Escape.</summary>
    public bool IsConfirmation { get; }

    public bool IsMessage => !IsConfirmation;

    public bool Result { get; private set; }

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler<bool>? CloseRequested;

    private void Close(bool result)
    {
        Result = result;
        CloseRequested?.Invoke(this, result);
    }
}
