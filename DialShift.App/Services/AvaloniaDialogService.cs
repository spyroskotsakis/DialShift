using Avalonia.Controls;
using DialShift.App.ViewModels;
using DialShift.App.Views.Dialogs;

namespace DialShift.App.Services;

/// <summary>
/// Avalonia dialogs (acceptance matrix §8.2.5). Owner rule: the most recent dialog this service opened that is still
/// visible (so a confirmation raised by an editor sits on that editor), else the main window when it is visible. With
/// no visible owner (start in tray, startup failure) the dialog is ownerless, centered on screen and activated; it is
/// never its own owner (BHV-64).
/// </summary>
public sealed class AvaloniaDialogService(Func<Window?> mainWindow) : IDialogService, IEditorDialogService
{
    private readonly List<Window> open = [];

    public Task ShowMessageAsync(string title, string message) =>
        ShowAsync(new MessageDialog(new MessageDialogViewModel(title, message)));

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No")
    {
        var model = new MessageDialogViewModel(title, message, confirmText, cancelText);
        await ShowAsync(new MessageDialog(model));
        return model.Result;
    }

    /// <summary>The startup-failure dialog (BHV-04, HS-08). The caller logs <c>app.startup_failed</c> first and exits non-zero after.</summary>
    public Task ShowStartupFailureAsync(string reason, string logFile) =>
        ShowMessageAsync(UiText.AppName, UiText.StartupFailed(reason, logFile));

    public async Task<EditorResult> ShowStationEditorAsync(StationEditorViewModel editor)
    {
        await ShowAsync(new StationEditorDialog(editor));
        return editor.Result;
    }

    public async Task<EditorResult> ShowScheduleEditorAsync(ScheduleEditorViewModel editor)
    {
        await ShowAsync(new ScheduleEditorDialog(editor));
        return editor.Result;
    }

    private Window? ResolveOwner()
    {
        for (var i = open.Count - 1; i >= 0; i--)
            if (open[i].IsVisible) return open[i];
        return mainWindow() is { IsVisible: true } main ? main : null;
    }

    private async Task ShowAsync(Window dialog)
    {
        var owner = ResolveOwner();
        open.Add(dialog);
        try
        {
            if (owner != null)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                await dialog.ShowDialog(owner);
                return;
            }
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
            dialog.Show();
            dialog.Activate();
            await closed.Task;
        }
        finally
        {
            open.Remove(dialog);
        }
    }
}
