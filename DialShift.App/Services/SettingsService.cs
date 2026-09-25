using DialShift.App.ViewModels;
using DialShift.Core;
using DialShift.Core.Playback;

namespace DialShift.App.Services;

/// <summary>
/// <see cref="ISettingsService"/> over <see cref="SettingsStore"/>. Saves are atomic (the store writes a temp file and
/// moves it into place). A failure is logged as <c>settings.save_failed</c> and shown with today's wording (BHV-16).
/// </summary>
public sealed class SettingsService(Settings settings, SettingsStore store, IPlaybackCoordinator coordinator, IDialogService dialogs, IAppLog log) : ISettingsService
{
    public Settings Settings { get; } = settings;

    public event EventHandler? SettingsChanged;

    public async Task<bool> SaveAsync()
    {
        try
        {
            store.Save(Settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Error("settings.save_failed", "Couldn't save settings.", ex);
            await dialogs.ShowMessageAsync(UiText.SaveFailedTitle, UiText.SaveFailed(ex.Message));
            return false;
        }
    }

    public async Task CommitAsync(SettingsChange change)
    {
        if (change.HasFlag(SettingsChange.Stations)) await coordinator.NotifySettingsChangedAsync();
        if (change.HasFlag(SettingsChange.Schedule)) await coordinator.RefreshScheduleAsync();
        await SaveAsync();
        if (change != SettingsChange.None) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
