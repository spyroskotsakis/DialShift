using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DialShift.App.Platform;
using DialShift.App.Platform.MacOS;
using DialShift.App.Platform.Windows;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Platform;

/// <summary>
/// <see cref="ISystemPowerEvents"/> contract (acceptance matrix HS-16, §8.2.2). macOS delivery is exercised by posting a
/// synthetic <c>NSWorkspaceDidWakeNotification</c> to the workspace notification center: <c>NSNotificationCenter</c>
/// delivers synchronously on the posting thread, so no run loop is needed. Windows <c>SystemEvents</c> delivery needs
/// a message loop and a real resume (NC-02).
/// </summary>
public static class PowerEventsTests
{
    public static void Run()
    {
        if (OperatingSystem.IsMacOS()) Mac();
        else Skip("HS-16 macOS NSWorkspace wake observer", "macOS only");

        if (OperatingSystem.IsWindows()) Windows();
        else Skip("HS-16 Windows SystemEvents.PowerModeChanged", "Windows only (runs on windows-latest in CI)");
    }

    [SupportedOSPlatform("macos")]
    private static void Mac()
    {
        var cycles = true;
        for (var i = 0; i < 3; i++)
        {
            var log = new RecordingAppLog();
            cycles &= NoThrow(() => { using var events = new MacPowerEvents(log); events.Start(); });
            cycles &= log.HasEvent("power_events.started") && !log.HasEvent("power_events.unavailable");
        }
        Check("HS-16 mac: Start/Dispose x3 never throws and registers each time", cycles);

        var log1 = new RecordingAppLog();
        var events1 = new MacPowerEvents(log1);
        var resumed1 = 0;
        events1.Resumed += (_, _) => resumed1++;
        events1.Start();
        events1.Start();
        Check("HS-16 mac: Start is idempotent (one registration)", log1.Entries.Count(e => e.EventName == "power_events.started") == 1);

        Wake.Post(Wake.DidWake);
        Check("HS-16 mac: a synthetic NSWorkspaceDidWakeNotification raises Resumed exactly once", resumed1 == 1);
        Check("HS-16 mac: ... and logs power_events.resumed", log1.HasEvent("power_events.resumed"));

        Wake.Post(Wake.WillSleep);
        Check("HS-16 mac: NSWorkspaceWillSleepNotification does not raise Resumed", resumed1 == 1);

        var log2 = new RecordingAppLog();
        var events2 = new MacPowerEvents(log2);
        var resumed2 = 0;
        events2.Resumed += (_, _) => resumed2++;
        events2.Resumed += (_, _) => throw new InvalidOperationException("handler boom");
        events2.Start();
        var posted = NoThrow(() => Wake.Post(Wake.DidWake));
        Check("HS-16 mac: two instances each receive the wake", resumed1 == 2 && resumed2 == 1);
        Check("HS-16 mac: a throwing handler never unwinds into Objective-C; it is logged", posted && log2.HasEvent("power_events.handler_failed"));

        events1.Dispose();
        events1.Dispose();
        Wake.Post(Wake.DidWake);
        Check("HS-16 mac: after Dispose (twice, idempotent) nothing is raised", resumed1 == 2 && resumed2 == 2);
        events1.Start();
        Wake.Post(Wake.DidWake);
        Check("HS-16 mac: Start after Dispose is a no-op", resumed1 == 2 && resumed2 == 3 && log1.Entries.Count(e => e.EventName == "power_events.started") == 1);
        events2.Dispose();
        Wake.Post(Wake.DidWake);
        Check("HS-16 mac: with every instance disposed no observer is left", resumed1 == 2 && resumed2 == 3);

        Check("HS-16 mac: Dispose without Start is harmless", NoThrow(() => new MacPowerEvents(new RecordingAppLog()).Dispose()));
    }

    [SupportedOSPlatform("windows")]
    private static void Windows()
    {
        var noThrow = true;
        var logs = new List<RecordingAppLog>();
        for (var i = 0; i < 3; i++)
        {
            var log = new RecordingAppLog();
            logs.Add(log);
            noThrow &= NoThrow(() => { using var events = new WindowsPowerEvents(log); events.Start(); });
        }
        Check("HS-16 win: subscribe/unsubscribe (Start/Dispose x3) never throws", noThrow);
        var unavailable = logs.SelectMany(l => l.Entries).FirstOrDefault(e => e.EventName == "power_events.unavailable");
        if (unavailable == null)
            Check("HS-16 win: each Start subscribed (power_events.started, no power_events.unavailable)", logs.All(l => l.HasEvent("power_events.started")));
        else
            Skip("HS-16 win: each Start subscribed", "SystemEvents refused in this session (logged as power_events.unavailable, non-fatal by contract): " + unavailable.Exception?.Message);

        var log1 = new RecordingAppLog();
        var events1 = new WindowsPowerEvents(log1);
        events1.Start();
        events1.Start();
        Check("HS-16 win: Start is idempotent (at most one subscription attempt)",
            log1.Entries.Count(e => e.EventName is "power_events.started" or "power_events.unavailable") == 1);
        Check("HS-16 win: Dispose twice is harmless", NoThrow(() => { events1.Dispose(); events1.Dispose(); }));
        events1.Start();
        Check("HS-16 win: Start after Dispose is a no-op", log1.Entries.Count(e => e.EventName is "power_events.started" or "power_events.unavailable") == 1);
        Skip("HS-16 win: Resumed on PowerModes.Resume", "SystemEvents delivery needs a message loop and a real resume: native check NC-02");
    }

    /// <summary>Posts workspace notifications the way AppKit does, via the Objective-C runtime.</summary>
    [SupportedOSPlatform("macos")]
    private static class Wake
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

        public const string DidWake = "NSWorkspaceDidWakeNotification";
        public const string WillSleep = "NSWorkspaceWillSleepNotification";

        public static void Post(string exportedName)
        {
            var name = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(AppKit), exportedName));
            var pool = objc_autoreleasePoolPush();
            try
            {
                var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                var center = Send(workspace, sel_registerName("notificationCenter"));
                SendPost(center, sel_registerName("postNotificationName:object:"), name, 0);
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        [DllImport(ObjC)] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC)] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
        [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPost(nint receiver, nint selector, nint name, nint obj);
    }
}
