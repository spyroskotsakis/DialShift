// A runtime-created Objective-C observer for an NSNotificationCenter (no blocks, no ObjC toolchain). Used by the AVPlayer
// adapter (default center) and by MacPowerEvents (the NSWorkspace notification center).
//
// Ownership: each NotificationObserver holds exactly one +1 instance of the process-global class
// "DialShiftNotificationObserver". NSNotificationCenter does not retain observers, so the instance is removed from its
// center (removeObserver:) BEFORE it is released, and callbacks are routed through an instance-to-owner map rather than
// a GCHandle stored in an ivar, so a late callback for a removed instance finds no owner and is ignored.
//
// Threading: an instance is not thread-safe; its owner serializes Create/Observe/Dispose (the AVPlayer adapter on the
// main queue, MacPowerEvents under its lock). NSNotificationCenter itself accepts add/remove from any thread.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DialShift.App.Interop;

/// <summary>Registers one native observer on a notification center and forwards notifications to a managed callback.</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class NotificationObserver : IDisposable
{
    private const string ClassName = "DialShiftNotificationObserver";
    private static readonly nint OnNoteSelector = ObjCRuntime.Selector("onNote:");
    private static readonly nint SelDefaultCenter = ObjCRuntime.Selector("defaultCenter");                        // NSNotificationCenter* (+0 singleton)
    private static readonly nint SelAddObserver = ObjCRuntime.Selector("addObserver:selector:name:object:");      // void (id, SEL, NSString*, id)
    private static readonly nint SelRemoveObserver = ObjCRuntime.Selector("removeObserver:");                     // void (id)
    private static readonly ConcurrentDictionary<nint, NotificationObserver> Owners = new();
    private static readonly Lock ClassLock = new();
    private static nint observerClass;

    private readonly nint center;
    private readonly Action<nint> callback;
    private readonly HashSet<nint> names = [];
    private nint instance;

    private NotificationObserver(nint center, nint instance, Action<nint> callback)
    {
        this.center = center;
        this.instance = instance;
        this.callback = callback;
    }

    /// <summary>Creates an observer on <c>[NSNotificationCenter defaultCenter]</c> (main thread). See <see cref="Create(nint, Action{nint})"/>.</summary>
    public static NotificationObserver Create(Action<nint> callback) =>
        Create(ObjCRuntime.SendId(ObjCRuntime.GetClass("NSNotificationCenter"), SelDefaultCenter), callback);

    /// <summary>
    /// Creates an observer on <paramref name="notificationCenter"/>, a center that lives for the process (the default
    /// center or <c>[[NSWorkspace sharedWorkspace] notificationCenter]</c>; it is not retained). <paramref name="callback"/>
    /// receives the borrowed (+0) NSNotification synchronously on the posting thread, inside an autorelease pool
    /// (AppKit and AVFoundation post on the main thread, but NSNotificationCenter delivers on whichever thread posts).
    /// It must copy what it needs immediately, must not block, and must not throw.
    /// </summary>
    public static NotificationObserver Create(nint notificationCenter, Action<nint> callback)
    {
        if (notificationCenter == 0) throw new InvalidOperationException("The notification center is nil.");
        var instance = ObjCRuntime.AllocInit(EnsureClass());
        if (instance == 0) throw new InvalidOperationException($"{ClassName} init returned nil.");
        var observer = new NotificationObserver(notificationCenter, instance, callback);
        Owners[instance] = observer;
        return observer;
    }

    /// <summary>Observes <paramref name="name"/> from any sender (object: nil). Each name is registered at most once.</summary>
    public void Observe(nint name)
    {
        ObjectDisposedException.ThrowIf(instance == 0, this);
        if (!names.Add(name)) return;
        ObjCRuntime.SendVoid(center, SelAddObserver, instance, OnNoteSelector, name, 0);
    }

    /// <summary>Removes every registration from the center, then releases the instance. Idempotent.</summary>
    public void Dispose()
    {
        if (instance == 0) return;
        ObjCRuntime.SendVoid(center, SelRemoveObserver, instance);
        Owners.TryRemove(instance, out _);
        ObjCRuntime.Release(instance);
        instance = 0;
        names.Clear();
    }

    /// <summary>The class is process-global: registered once, reused by every observer (objc_lookUpClass first).</summary>
    private static nint EnsureClass()
    {
        lock (ClassLock)
        {
            if (observerClass != 0) return observerClass;
            var existing = ObjCRuntime.LookUpClass(ClassName);
            if (existing != 0) return observerClass = existing;
            var cls = ObjCRuntime.AllocateClassPair(ObjCRuntime.GetClass("NSObject"), ClassName, 0);
            if (cls == 0) throw new InvalidOperationException($"objc_allocateClassPair failed for {ClassName}.");
            delegate* unmanaged<nint, nint, nint, void> imp = &OnNote;
            if (ObjCRuntime.ClassAddMethod(cls, OnNoteSelector, (nint)imp, "v@:@") == 0)
            {
                ObjCRuntime.DisposeClassPair(cls);
                throw new InvalidOperationException($"class_addMethod failed for {ClassName}.");
            }
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
