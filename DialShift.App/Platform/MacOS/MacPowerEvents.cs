using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DialShift.Core.Playback;

namespace DialShift.App.Platform.MacOS;

/// <summary>
/// macOS resume notifications via <c>NSWorkspaceDidWakeNotification</c> (acceptance matrix §8.2.2, decision D3).
/// </summary>
/// <remarks>
/// <para><b>Mechanism:</b> a tiny Objective-C class (<c>DialShiftWakeObserver : NSObject</c>) is created at runtime
/// once per process with <c>objc_allocateClassPair</c>/<c>class_addMethod</c>/<c>objc_registerClassPair</c>. Its single
/// method <c>dialShiftDidWake:</c> (type encoding <c>v@:@</c>) points at a managed callback. Each
/// <see cref="Start"/> allocates one observer instance and registers it with
/// <c>[[NSWorkspace sharedWorkspace] notificationCenter]</c>
/// <c>addObserver:selector:name:object:</c>; <see cref="Dispose"/> calls <c>removeObserver:</c> and releases it, so no
/// observer outlives the object.</para>
/// <para><b>ABI (arm64):</b> <c>objc_msgSend</c> is bound once per exact prototype (every argument is a pointer-sized
/// <c>id</c>/<c>SEL</c>, no variadic call, no struct or floating-point return), and the callback is a plain C function
/// <c>void (id self, SEL _cmd, id notification)</c>. The callback delegate lives in a static field for the life of
/// the process because the registered class keeps its function pointer forever.</para>
/// <para><b>Threading:</b> AppKit posts the wake notification on the main thread (the Avalonia UI thread), so
/// <see cref="Resumed"/> is raised there; consumers must treat it as an arbitrary thread and not block.</para>
/// <para><b>Failure:</b> any problem while registering (AppKit missing, runtime refusal) is logged as
/// <c>power_events.unavailable</c> and <see cref="Start"/> returns normally: the coordinator's sleep-inclusive
/// monotonic tick-gap check (<see cref="MacMonotonicClock"/>) still detects wake. Exceptions never cross back into
/// Objective-C.</para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacPowerEvents : ISystemPowerEvents
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string ObserverClassName = "DialShiftWakeObserver";
    private const string WakeSelectorName = "dialShiftDidWake:";

    private static readonly WakeCallback Callback = OnWake;
    private static readonly Lazy<nint> ObserverClass = new(RegisterObserverClass, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<nint, MacPowerEvents> LiveObservers = new();

    private readonly IAppLog log;
    private readonly Lock gate = new();
    private nint observer;
    private nint notificationCenter;
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

            nint pendingObserver = 0;
            try
            {
                var observerClass = ObserverClass.Value;
                var poolToken = objc_autoreleasePoolPush();
                try
                {
                    pendingObserver = Send(Send(observerClass, Sel("alloc")), Sel("init"));
                    if (pendingObserver == 0) throw new InvalidOperationException("Couldn't create the wake observer.");

                    var workspace = Send(objc_getClass("NSWorkspace"), Sel("sharedWorkspace"));
                    var center = workspace == 0 ? 0 : Send(workspace, Sel("notificationCenter"));
                    if (center == 0) throw new InvalidOperationException("NSWorkspace has no notification center.");

                    LiveObservers[pendingObserver] = this;
                    SendAddObserver(center, Sel("addObserver:selector:name:object:"),
                        pendingObserver, Sel(WakeSelectorName), DidWakeNotificationName(), 0);

                    observer = pendingObserver;
                    notificationCenter = center;
                    pendingObserver = 0;
                }
                finally
                {
                    objc_autoreleasePoolPop(poolToken);
                }
                log.Info("power_events.started", "Listening for NSWorkspaceDidWakeNotification.");
            }
            catch (Exception ex)
            {
                if (pendingObserver != 0)
                {
                    LiveObservers.TryRemove(pendingObserver, out _);
                    TryRelease(pendingObserver);
                }
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
            if (observer == 0) return;

            try
            {
                var poolToken = objc_autoreleasePoolPush();
                try
                {
                    SendVoid(notificationCenter, Sel("removeObserver:"), observer);
                }
                finally
                {
                    objc_autoreleasePoolPop(poolToken);
                }
            }
            catch (Exception ex)
            {
                log.Warn("power_events.stop_failed", "Couldn't remove the wake observer.", ex);
            }
            LiveObservers.TryRemove(observer, out _);
            TryRelease(observer);
            observer = 0;
            notificationCenter = 0;
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

    private static void OnWake(nint self, nint selector, nint notification)
    {
        try
        {
            if (LiveObservers.TryGetValue(self, out var owner)) owner.RaiseResumed();
        }
        catch (Exception)
        {
            // Never let a managed exception unwind into the Objective-C runtime.
        }
    }

    private static nint RegisterObserverClass()
    {
        // AppKit hosts NSWorkspace and the notification name. Avalonia has already loaded it in the app; loading it
        // again only bumps a reference count and keeps it resident.
        NativeLibrary.Load(AppKitPath);

        var existing = objc_getClass(ObserverClassName);
        if (existing != 0) return existing;

        var cls = objc_allocateClassPair(objc_getClass("NSObject"), ObserverClassName, 0);
        if (cls == 0) throw new InvalidOperationException($"objc_allocateClassPair({ObserverClassName}) failed.");
        if (!class_addMethod(cls, Sel(WakeSelectorName), Marshal.GetFunctionPointerForDelegate(Callback), "v@:@"))
        {
            objc_disposeClassPair(cls);
            throw new InvalidOperationException($"class_addMethod({WakeSelectorName}) failed.");
        }
        objc_registerClassPair(cls);
        return cls;
    }

    private static nint DidWakeNotificationName()
    {
        // NSWorkspaceDidWakeNotification is an exported `NSString *const` global.
        var symbol = NativeLibrary.GetExport(NativeLibrary.Load(AppKitPath), "NSWorkspaceDidWakeNotification");
        var name = Marshal.ReadIntPtr(symbol);
        return name != 0 ? name : throw new InvalidOperationException("NSWorkspaceDidWakeNotification is null.");
    }

    private static void TryRelease(nint instance)
    {
        try { SendVoidNoArg(instance, Sel("release")); }
        catch (Exception) { /* nothing more to do */ }
    }

    private static nint Sel(string name) => sel_registerName(name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WakeCallback(nint self, nint selector, nint notification);

    [DllImport(ObjCLibrary)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjCLibrary)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjCLibrary)]
    private static extern nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [DllImport(ObjCLibrary)]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport(ObjCLibrary)]
    private static extern void objc_disposeClassPair(nint cls);

    [DllImport(ObjCLibrary)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool class_addMethod(nint cls, nint name, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(ObjCLibrary)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjCLibrary)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    /// <summary><c>id objc_msgSend(id, SEL)</c></summary>
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    /// <summary><c>void objc_msgSend(id, SEL)</c></summary>
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNoArg(nint receiver, nint selector);

    /// <summary><c>void objc_msgSend(id, SEL, id)</c></summary>
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);

    /// <summary><c>void objc_msgSend(id, SEL, id observer, SEL selector, NSString *name, id object)</c></summary>
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendAddObserver(nint receiver, nint selector, nint observer, nint observerSelector, nint name, nint obj);
}
