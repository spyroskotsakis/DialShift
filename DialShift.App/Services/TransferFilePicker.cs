using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DialShift.App.Services;

/// <summary>
/// <see cref="ITransferFilePicker"/> over Avalonia's <c>StorageProvider</c> (brief 5 §6, §7). The owner is the main
/// window, resolved the way <see cref="AvaloniaDialogService"/> resolves its owner: the running window when it is
/// visible, otherwise it is shown first so the native dialog is always parented (brief 5 §9). Tests and the smoke
/// runner bypass this boundary and call <see cref="ISettingsTransferService"/> with explicit paths.
/// </summary>
public sealed class TransferFilePicker(Func<Window?> mainWindow) : ITransferFilePicker
{
    /// <summary>The <c>*.json</c> filter (brief 5 §11 #4).</summary>
    private static readonly FilePickerFileType JsonFilter = new("JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"]
    };

    /// <summary>The "All files" filter (brief 5 §11 #4).</summary>
    private static readonly FilePickerFileType AllFilesFilter = new("All files") { Patterns = ["*"] };

    public async Task<string?> PickSavePathAsync(string defaultName)
    {
        if (ResolveOwner() is not { } window) return null;
        try
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = TransferText.ExportPickerTitle,
                SuggestedFileName = defaultName,
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [JsonFilter, AllFilesFilter]
            });
            return file?.TryGetLocalPath();
        }
        catch
        {
            // A picker failure is a cancel: no side effect, and never a crash in the UI lane.
            return null;
        }
    }

    public async Task<string?> PickOpenPathAsync()
    {
        if (ResolveOwner() is not { } window) return null;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = TransferText.ImportPickerTitle,
                AllowMultiple = false,
                FileTypeFilter = [JsonFilter]
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The main window as the picker's owner. A window that exists but is hidden (tray-only) is shown first, so the
    /// native dialog always has a live <c>TopLevel</c> (brief 5 §9); no window at all means no picker.
    /// </summary>
    private Window? ResolveOwner()
    {
        if (mainWindow() is not { } window) return null;
        try
        {
            if (!window.IsVisible) window.Show();
        }
        catch
        {
            return null;
        }
        return window;
    }
}
