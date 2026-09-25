// The App's only Objective-C runtime bindings, shared by the macOS AVPlayer adapter and the NSWorkspace wake observer
// (docs/spikes.md, "Production guidance"). Add new prototypes here; never declare objc_* P/Invokes anywhere else.
//
// arm64 ABI rule: objc_msgSend is a trampoline, not a variadic function. The callee's real prototype decides where
// arguments live (x0..x7 for pointers/integers, s0..s7 for float, x8 for the address of an indirect struct return), so
// every entry point below mirrors exactly one Objective-C method prototype. Never call objc_msgSend through a
// variadic or "object[] args" shape: on arm64 the arguments would land in the wrong registers.
//
// Ownership vocabulary used by every caller:
//   +1  returned by alloc/init/new/copy: the caller owns it and releases it exactly once (Release).
//   +0  returned by any other getter: borrowed. Never released, only read inside an autorelease pool scope
//       (MacMainQueue and NotificationObserver wrap every work item and callback in objc_autoreleasePoolPush/Pop).
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DialShift.App.Interop;

/// <summary>Typed <c>objc_msgSend</c> entry points plus the handful of runtime functions the macOS adapters need.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ObjCRuntime
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(LibObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClassCore(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_lookUpClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint LookUpClass(string name);

    [LibraryImport(LibObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Selector(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_autoreleasePoolPush")]
    internal static partial nint AutoreleasePoolPush();

    [LibraryImport(LibObjC, EntryPoint = "objc_autoreleasePoolPop")]
    internal static partial void AutoreleasePoolPop(nint pool);

    [LibraryImport(LibObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint AllocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    internal static partial void RegisterClassPair(nint cls);

    /// <summary>Destroys a class allocated by <see cref="AllocateClassPair"/> that was never registered.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_disposeClassPair")]
    internal static partial void DisposeClassPair(nint cls);

    /// <summary><c>BOOL class_addMethod(Class, SEL, IMP, const char *types)</c>; BOOL is a 1-byte C bool on arm64.</summary>
    [LibraryImport(LibObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial byte ClassAddMethod(nint cls, nint selector, nint implementation, string types);

    // ─── objc_msgSend, one entry point per prototype (receiver, _cmd, ...) ───

    /// <summary><c>id (id, SEL)</c>: alloc, init, getters returning objects.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendId(nint receiver, nint selector);

    /// <summary><c>id (id, SEL, id)</c>: initWithString:, initWithAsset:, objectForKey:, and <c>id (id, SEL, const char*)</c> (initWithUTF8String:).</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendId(nint receiver, nint selector, nint arg);

    /// <summary><c>id (id, SEL, id, id)</c>: initWithURL:options:.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendId(nint receiver, nint selector, nint arg1, nint arg2);

    /// <summary><c>void (id, SEL)</c>: play, pause, release.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector);

    /// <summary><c>void (id, SEL, id)</c>: replaceCurrentItemWithPlayerItem:, removeObserver:.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint arg);

    /// <summary><c>void (id, SEL, id, SEL, id, id)</c>: addObserver:selector:name:object:.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint arg1, nint arg2, nint arg3, nint arg4);

    /// <summary><c>void (id, SEL, float)</c>: setVolume:. The float travels in s0 on arm64 (never widen to double).</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidFloat(nint receiver, nint selector, float arg);

    /// <summary><c>void (id, SEL, BOOL)</c>: setMuted:. BOOL is a 1-byte C bool on arm64.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidBool(nint receiver, nint selector, byte arg);

    /// <summary><c>float (id, SEL)</c>: volume (returned in s0 on arm64).</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial float SendFloat(nint receiver, nint selector);

    /// <summary><c>NSInteger (id, SEL)</c>: status, timeControlStatus, code, count, errorStatusCode.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendNInt(nint receiver, nint selector);

    /// <summary><c>const char* (id, SEL)</c>: UTF8String (inner pointer, valid until the pool drains).</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial byte* SendUtf8(nint receiver, nint selector);

    /// <summary>
    /// <c>CMTime (id, SEL)</c>: currentTime. CMTime is a 24-byte struct, returned indirectly: on arm64 the caller passes the
    /// result address in x8, which the plain objc_msgSend trampoline preserves (verified in the spike against
    /// CMTimeGetSeconds). This prototype is arm64-only: x86_64 would need objc_msgSend_stret, and DialShift ships macOS for
    /// osx-arm64 only (decision D13).
    /// </summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial CMTime SendCMTimeArm64(nint receiver, nint selector);

    // ─── Helpers ───

    private static readonly nint SelAlloc = Selector("alloc");
    private static readonly nint SelInit = Selector("init");
    private static readonly nint SelRelease = Selector("release");
    private static readonly nint SelInitWithUtf8 = Selector("initWithUTF8String:");
    private static readonly nint SelUtf8String = Selector("UTF8String");

    /// <summary>Returns the class or throws when the framework defining it is not loaded.</summary>
    internal static nint GetClass(string name)
    {
        var cls = GetClassCore(name);
        return cls != 0 ? cls : throw new InvalidOperationException($"Objective-C class '{name}' is not available.");
    }

    /// <summary><c>[[cls alloc] init]</c>: +1, or 0 when init returned nil.</summary>
    internal static nint AllocInit(nint cls) => SendId(SendId(cls, SelAlloc), SelInit);

    /// <summary><c>[cls alloc]</c>: +1, must be paired with an init… call that consumes it.</summary>
    internal static nint Alloc(nint cls) => SendId(cls, SelAlloc);

    /// <summary>Releases a +1 reference. No-op for nil.</summary>
    internal static void Release(nint obj)
    {
        if (obj != 0) SendVoid(obj, SelRelease);
    }

    /// <summary>Creates an NSString (+1; the caller releases it).</summary>
    internal static nint CreateNSString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* text = bytes)
            return SendId(Alloc(GetClass("NSString")), SelInitWithUtf8, (nint)text);
    }

    /// <summary>Copies a borrowed (+0) NSString to a managed string; null for nil.</summary>
    internal static string? ToManagedString(nint nsString)
    {
        if (nsString == 0) return null;
        var utf8 = SendUtf8(nsString, SelUtf8String);
        return utf8 == null ? null : Marshal.PtrToStringUTF8((nint)utf8);
    }

    /// <summary>Reads a <c>CMTime</c>-returning getter (arm64 indirect struct return, see <see cref="SendCMTimeArm64"/>).</summary>
    /// <exception cref="PlatformNotSupportedException">The process is not arm64 (D13: no osx-x64 build).</exception>
    internal static CMTime SendCMTime(nint receiver, nint selector) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? SendCMTimeArm64(receiver, selector)
            : throw new PlatformNotSupportedException("The AVPlayer adapter supports arm64 only (decision D13).");

    /// <summary>Reads an exported <c>NSString *const</c> (or any pointer-sized constant) from a loaded framework.</summary>
    internal static nint ReadPointerConstant(nint library, string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
}
