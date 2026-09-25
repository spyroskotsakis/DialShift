using DialShift.Core;

namespace DialShift.App.ViewModels;

/// <summary>What a settings commit changed, so the service tells the coordinator only what it needs.</summary>
[Flags]
public enum SettingsChange
{
    /// <summary>Save only (preferences such as start in tray, volume, last station).</summary>
    None = 0,
    /// <summary>Stations, stream URLs or the fallback changed: <c>NotifySettingsChangedAsync</c>.</summary>
    Stations = 1,
    /// <summary>Slots or "Follow schedule" changed: <c>RefreshScheduleAsync</c> (replays the current slot, BHV-47).</summary>
    Schedule = 2
}

/// <summary>
/// The one path for settings mutations. Callers mutate <see cref="Settings"/> on the UI thread (the coordinator's host
/// threading contract), then call <see cref="CommitAsync"/> or <see cref="SaveAsync"/>.
/// </summary>
public interface ISettingsService
{
    /// <summary>The live settings shared with the coordinator. Mutate only on the UI thread.</summary>
    Settings Settings { get; }

    /// <summary>Raised on the UI thread after a <see cref="CommitAsync"/> that changed stations or the schedule, so pages and the tray re-render.</summary>
    event EventHandler? SettingsChanged;

    /// <summary>Persists now. A failure is logged and shown as "DialShift · Save failed" (BHV-16); it never throws.</summary>
    Task<bool> SaveAsync();

    /// <summary>Informs the coordinator per <paramref name="change"/>, saves, then raises <see cref="SettingsChanged"/>.</summary>
    Task CommitAsync(SettingsChange change);
}
