using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DialShift.App.Interop;
using DialShift.Core.Playback;

namespace DialShift.App.Platform.MacOS;

/// <summary>
/// macOS resume notifications via <c>NSWorkspaceDidWakeNotification</c> (acceptance matrix §8.2.2, decision D3).
/// </summary>
/// <remarks>
/// <para><b>Mechanism:</b> each <see cref="Start"/> creates one shared-interop <see cref="NotificationObserver"/> on
/// <c>[[NSWorkspace sharedWorkspace] notificationCenter]</c> and observes <c>NSWorkspaceDidWakeNotification</c>. The
/// runtime Objective-C class behind it (<c>DialShiftNotificationObserver : NSObject</c>, method <c>onNote:</c>,
/// <c>v@:@</c>, an <c>[UnmanagedCallersOnly]</c> function pointer) is registered once per process and shared with the
/// AVPlayer adapter. <see cref="Dispose"/> calls <c>removeObserver:</c> before releasing the instance, so no observer
/// outlives this object.</para>
/// <para><b>Threading:</b> <see cref="Resumed"/> is raised synchronously on the thread that posted the notification.
/// AppKit posts the real wake notification on the main thread (the Avalonia UI thread), but consumers must treat it as
/// an arbitrary thread, return quickly and not block.</para>
/// <para><b>Failure:</b> any problem while registering (AppKit missing, runtime refusal) is logged as
/// <c>power_events.unavailable</c> and <see cref="Start"/> returns normally: the coordinator's sleep-inclusive
/// monotonic tick-gap check (<see cref="MacMonotonicClock"/>) still detects wake. Handler exceptions are logged and
/// never cross back into Objective-C.</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacPowerEvents : ISystemPowerEvents
{
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private readonly IAppLog log;
    private readonly Lock gate = new();
    private NotificationObserver? observer;
    private bool started;
    private volatile bool disposed;

    public MacPowerEvents(IAppLog log)
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

            NotificationObserver? pending = null;
            try
            {
                // AppKit hosts NSWorkspace and the notification name. Avalonia has already loaded it in the app; loading
                // it again only bumps a reference count and keeps it resident.
                var appKit = NativeLibrary.Load(AppKitPath);
                var didWake = ObjCRuntime.ReadPointerConstant(appKit, "NSWorkspaceDidWakeNotification");
                if (didWake == 0) throw new InvalidOperationException("NSWorkspaceDidWakeNotification is null.");

                var pool = ObjCRuntime.AutoreleasePoolPush();
                try
                {
                    var workspace = ObjCRuntime.SendId(ObjCRuntime.GetClass("NSWorkspace"), ObjCRuntime.Selector("sharedWorkspace"));
                    var center = workspace == 0 ? 0 : ObjCRuntime.SendId(workspace, ObjCRuntime.Selector("notificationCenter"));
                    pending = NotificationObserver.Create(center, _ => RaiseResumed());
                    pending.Observe(didWake);
                }
                finally
                {
                    ObjCRuntime.AutoreleasePoolPop(pool);
                }

                observer = pending;
                pending = null;
                log.Info("power_events.started", "Listening for NSWorkspaceDidWakeNotification.");
            }
            catch (Exception ex)
            {
                if (pending != null) TryDispose(pending);
                log.Warn("power_events.unavailable",
                    "Couldn't register for NSWorkspaceDidWakeNotification; the monotonic tick-gap check covers wake.", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (observer == null) return;
            TryDispose(observer);
            observer = null;
        }
    }

    private void TryDispose(NotificationObserver instance)
    {
        try
        {
            instance.Dispose();
        }
        catch (Exception ex)
        {
            log.Warn("power_events.stop_failed", "Couldn't remove the wake observer.", ex);
        }
    }

    private void RaiseResumed()
    {
        if (disposed) return;
        log.Info("power_events.resumed", "macOS reported wake from sleep.");
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
