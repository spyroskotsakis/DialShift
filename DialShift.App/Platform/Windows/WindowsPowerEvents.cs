using System.Runtime.Versioning;
using DialShift.Core.Playback;
using Microsoft.Win32;

namespace DialShift.App.Platform.Windows;

/// <summary>
/// Windows resume notifications via <c>SystemEvents.PowerModeChanged</c> with <c>PowerModes.Resume</c>
/// (acceptance matrix §8.2.2, spike SP-03).
/// </summary>
/// <remarks>
/// <para><see cref="Start"/> must be called after the Avalonia desktop lifetime and its message loop exist (from
/// the UI thread, e.g. in <c>OnFrameworkInitializationCompleted</c>). <c>SystemEvents</c> creates its hidden
/// notification window on first subscription, and subscribing earlier would tie it to a thread without a pump.</para>
/// <para><see cref="Dispose"/> unsubscribes deterministically; nothing is raised after it. <see cref="Resumed"/> is
/// raised on the <c>SystemEvents</c> thread. A subscription failure is logged as <c>power_events.unavailable</c> and
/// is never fatal: the coordinator's monotonic tick-gap check (<see cref="WindowsMonotonicClock"/>) still detects wake.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerEvents : ISystemPowerEvents
{
    private readonly IAppLog log;
    private readonly Lock gate = new();
    private bool subscribed;
    private bool started;
    private volatile bool disposed;

    public WindowsPowerEvents(IAppLog log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event EventHandler? Resumed;

    public void Start()
    {
        lock (gate)
        {
            if (started || disposed) return;
            started = true;
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                subscribed = true;
                log.Info("power_events.started", "Listening for SystemEvents.PowerModeChanged.");
            }
            catch (Exception ex)
            {
                log.Warn("power_events.unavailable",
                    "Couldn't subscribe to SystemEvents.PowerModeChanged; the monotonic tick-gap check covers wake.", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (!subscribed) return;
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                log.Warn("power_events.stop_failed", "Couldn't unsubscribe from SystemEvents.PowerModeChanged.", ex);
            }
            subscribed = false;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (disposed || e.Mode != PowerModes.Resume) return;
        log.Info("power_events.resumed", "Windows reported resume from sleep.");
        try
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            log.Error("power_events.handler_failed", "The wake handler threw.", ex);
        }
    }
}
