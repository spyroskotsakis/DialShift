namespace DialShift.App.Services;

/// <summary>
/// The native Save/Open file picker for the settings transfer (brief 5 §6, §7). A thin boundary over Avalonia's
/// <c>StorageProvider</c>, owned by the main window (the <c>AvaloniaDialogService</c> owner pattern); tests and the
/// smoke runner bypass it and call <c>ISettingsTransferService</c> with explicit paths, so no OS dialog blocks
/// automation (the <c>IFileRevealService</c>/<c>FakeFileReveal</c> precedent).
/// </summary>
public interface ITransferFilePicker
{
    /// <summary>
    /// Shows the Save dialog with <paramref name="defaultName"/> suggested and <c>*.json</c> plus "All files" filters
    /// (brief 5 §6, §11 #4). Returns the chosen path, or null when the user cancelled; a null result has no side effect.
    /// </summary>
    Task<string?> PickSavePathAsync(string defaultName);

    /// <summary>
    /// Shows the Open dialog. Returns the chosen path, or null when the user cancelled; a null result has no side effect.
    /// </summary>
    Task<string?> PickOpenPathAsync();
}
