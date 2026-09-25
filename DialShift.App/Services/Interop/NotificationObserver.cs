// A runtime-created Objective-C observer for NSNotificationCenter (no blocks, no ObjC toolchain).
//
// Ownership: each NotificationObserver holds exactly one +1 instance of the process-global class
// "DialShiftAVPlayerObserver". NSNotificationCenter does not retain observers, so the instance is removed from the
// center (removeObserver:) BEFORE it is released, and callbacks are routed through an instance-to-owner map rather than
// a GCHandle stored in an ivar, so a late callback for a removed instance finds no owner and is ignored.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DialShift.App.Services.Interop;

/// <summary>Registers one native observer on the default notification center and forwards notifications to a managed callback.</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class NotificationObserver : IDisposable
{
    private const string ClassName = "DialShiftAVPlayerObserver";
    private static readonly nint OnNoteSelector = ObjCRuntime.Selector("onNote:");
    private static readonly ConcurrentDictionary<nint, NotificationObserver> Owners = new();
    private static readonly Lock ClassLock = new();
    private static nint observerClass;

    private readonly Action<nint> callback;
    private readonly HashSet<nint> names = [];
    private nint instance;

    private NotificationObserver(nint instance, Action<nint> callback)
    {
        this.instance = instance;
        this.callback = callback;
    }

    /// <summary>
    /// Creates the observer (main thread). <paramref name="callback"/> receives the borrowed (+0) NSNotification on the
    /// posting thread (AVFoundation posts item notifications on the main thread, but NSNotificationCenter delivers on
    /// whichever thread posts). It must copy what it needs immediately, must not block, and must not throw.
    /// </summary>
    public static NotificationObserver Create(Action<nint> callback)
    {
        var instance = ObjCRuntime.AllocInit(EnsureClass());
        if (instance == 0) throw new InvalidOperationException($"{ClassName} init returned nil.");
        var observer = new NotificationObserver(instance, callback);
        Owners[instance] = observer;
        return observer;
    }

    /// <summary>Observes <paramref name="name"/> from any sender (object: nil). Each name is registered at most once.</summary>
    public void Observe(nint name)
    {
        ObjectDisposedException.ThrowIf(instance == 0, this);
        if (!names.Add(name)) return;
        var center = ObjCRuntime.SendId(AVFoundation.NSNotificationCenterClass, AVFoundation.Sel.DefaultCenter); // +0 singleton
        ObjCRuntime.SendVoid(center, AVFoundation.Sel.AddObserver, instance, OnNoteSelector, name, 0);
    }

    /// <summary>Removes every registration, then releases the instance (main thread). Idempotent.</summary>
    public void Dispose()
    {
        if (instance == 0) return;
        var center = ObjCRuntime.SendId(AVFoundation.NSNotificationCenterClass, AVFoundation.Sel.DefaultCenter);
        ObjCRuntime.SendVoid(center, AVFoundation.Sel.RemoveObserver, instance);
        Owners.TryRemove(instance, out _);
        ObjCRuntime.Release(instance);
        instance = 0;
        names.Clear();
    }

    /// <summary>The class is process-global: registered once, reused by every engine instance (objc_lookUpClass first).</summary>
    private static nint EnsureClass()
    {
        lock (ClassLock)
        {
            if (observerClass != 0) return observerClass;
            var existing = ObjCRuntime.LookUpClass(ClassName);
            if (existing != 0) return observerClass = existing;
            var cls = ObjCRuntime.AllocateClassPair(AVFoundation.NSObjectClass, ClassName, 0);
            if (cls == 0) throw new InvalidOperationException($"objc_allocateClassPair failed for {ClassName}.");
            delegate* unmanaged<nint, nint, nint, void> imp = &OnNote;
            if (ObjCRuntime.ClassAddMethod(cls, OnNoteSelector, (nint)imp, "v@:@") == 0)
                throw new InvalidOperationException($"class_addMethod failed for {ClassName}.");
            ObjCRuntime.RegisterClassPair(cls);
            return observerClass = cls;
        }
    }

    /// <summary><c>- (void)onNote:(NSNotification *)note</c>. Exceptions never cross into Objective-C.</summary>
    [UnmanagedCallersOnly]
    private static void OnNote(nint self, nint selector, nint notification)
    {
        try
        {
            if (!Owners.TryGetValue(self, out var owner)) return;
            var pool = ObjCRuntime.AutoreleasePoolPush();
            try { owner.callback(notification); }
            finally { ObjCRuntime.AutoreleasePoolPop(pool); }
        }
        catch (Exception ex)
        {
            // Last line of defence: an exception escaping here would abort the process. Callbacks catch and log their own.
            System.Diagnostics.Trace.TraceError($"{ClassName} callback failed: {ex.GetType().Name}");
        }
    }
}
