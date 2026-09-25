using DialShift.App.Services;
using DialShift.Core.Playback;

namespace DialShift.App.ViewModels;

/// <summary>Services every page view model needs, plus the shared error sink for commands.</summary>
public sealed class ViewModelServices(
    IPlaybackCoordinator coordinator,
    ISettingsService settings,
    IDialogService dialogs,
    IEditorDialogService editors,
    IAppLog log)
{
    public IPlaybackCoordinator Coordinator { get; } = coordinator;
    public ISettingsService Settings { get; } = settings;
    public IDialogService Dialogs { get; } = dialogs;
    public IEditorDialogService Editors { get; } = editors;
    public IAppLog Log { get; } = log;

    /// <summary>An unexpected command failure: logged, then shown, so no action fails silently.</summary>
    public void ReportError(Exception ex)
    {
        Log.Error("ui.command_failed", "A user command failed.", ex);
        _ = Dialogs.ShowMessageAsync(UiText.UnexpectedErrorTitle, ex.Message);
    }

    /// <summary>Runs fire-and-forget work started by a property setter (a two-way binding) with the same error handling as a command.</summary>
    public async void Run(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { ReportError(ex); }
    }
}

/// <summary>A tab of the main window: its heading plus page content.</summary>
public abstract class PageViewModel(ViewModelServices services) : ObservableObject
{
    protected ViewModelServices Services { get; } = services;

    public abstract string Title { get; }

    public abstract string Description { get; }

    /// <summary>Re-reads settings after a committed change.</summary>
    public abstract void Refresh();

    /// <summary>Applies a playback snapshot (UI thread). Most pages ignore it.</summary>
    public virtual void ApplySnapshot(PlaybackSnapshot snapshot) { }
}
