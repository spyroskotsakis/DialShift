namespace DialShift.App.Platform;

/// <summary>
/// OS resume notifications (acceptance matrix §8.2.2). Windows: <c>SystemEvents.PowerModeChanged</c>
/// with <c>PowerModes.Resume</c>. macOS: <c>NSWorkspace.DidWakeNotification</c> only if the spike passes
/// (decision D3), otherwise a no-op, with the coordinator's monotonic tick gap covering wake.
/// </summary>
public interface ISystemPowerEvents : IDisposable
{
    /// <summary>Raised on an arbitrary thread; the App forwards it to <c>IPlaybackCoordinator.NotifyWakeAsync()</c>.</summary>
    event EventHandler? Resumed;

    /// <summary>
    /// Idempotent. Called after the Avalonia desktop lifetime and message loop exist. Registration
    /// failure is logged as <c>power_events.unavailable</c> and is never fatal.
    /// </summary>
    void Start();
}
