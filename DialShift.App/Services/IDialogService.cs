namespace DialShift.App.Services;

/// <summary>
/// Modal messages and confirmations (acceptance matrix §8.2.5). The owner is the main window when it
/// is visible; otherwise the dialog is ownerless and centered on screen, and never its own owner.
/// The confirm button is the default, the cancel button is the cancel, and all text is selectable and wraps.
/// </summary>
public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No");
}
