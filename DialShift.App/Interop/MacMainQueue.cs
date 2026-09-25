// Main-thread marshalling for AppKit/AVFoundation work through libdispatch, so the adapter does not depend on Avalonia.
//
// Requirement on the host: the process main thread must service the main dispatch queue (a running main CFRunLoop /
// NSApplication loop). Avalonia's macOS backend runs [NSApp run] on the main thread, which does this. The spike showed
// that without it AVPlayer never leaves "Unknown" and reports no errors (docs/spikes.md, finding 3).
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DialShift.App.Interop;

/// <summary>Posts work to the main dispatch queue (<c>dispatch_async_f</c>), FIFO, each item inside an autorelease pool.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacMainQueue
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(LibSystem, EntryPoint = "dispatch_async_f")]
    private static partial void DispatchAsyncF(nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(LibSystem, EntryPoint = "pthread_main_np")]
    private static partial int PthreadMainNp();

    /// <summary><c>dispatch_get_main_queue()</c> is a macro for <c>&amp;_dispatch_main_q</c>; the export's address is the queue.</summary>
    private static readonly nint MainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");

    /// <summary>True on the process main thread (the AppKit/AVFoundation thread).</summary>
    public static bool IsMainThread => PthreadMainNp() != 0;

    /// <summary>
    /// Queues <paramref name="work"/> on the main queue and returns a task that completes (on the thread pool, never
    /// inline on the main thread) when it has run. Always asynchronous, even when called on the main thread, so all
    /// native work keeps one FIFO order. The task never completes if the main queue is not serviced.
    /// </summary>
    public static Task InvokeAsync(Action work)
    {
        var item = new WorkItem(work);
        var handle = GCHandle.Alloc(item);
        DispatchAsyncF(MainQueue, GCHandle.ToIntPtr(handle), &Run);
        return item.Completion.Task;
    }

    /// <summary>Runs <paramref name="work"/> synchronously inside an autorelease pool. Main thread only.</summary>
    public static void RunInline(Action work)
    {
        if (!IsMainThread) throw new InvalidOperationException("Main-thread work was invoked off the main thread.");
        var pool = ObjCRuntime.AutoreleasePoolPush();
        try { work(); }
        finally { ObjCRuntime.AutoreleasePoolPop(pool); }
    }

    [UnmanagedCallersOnly]
    private static void Run(nint context)
    {
        // Never let a managed exception cross into libdispatch: it would abort the process.
        WorkItem? item = null;
        try
        {
            var handle = GCHandle.FromIntPtr(context);
            item = (WorkItem)handle.Target!;
            handle.Free();
            var pool = ObjCRuntime.AutoreleasePoolPush();
            try { item.Work(); }
            finally { ObjCRuntime.AutoreleasePoolPop(pool); }
            item.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            item?.Completion.TrySetException(ex);
        }
    }

    private sealed class WorkItem(Action work)
    {
        public Action Work { get; } = work;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
