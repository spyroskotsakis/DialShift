using System.Diagnostics;
using Avalonia;
using Avalonia.Rendering;
using DialShift.App.Interop;
using DialShift.App.Platform.MacOS;
using DialShift.Tests.Core;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Platform;

/// <summary>
/// The macOS no-display startup fallback (<see cref="MacRenderTimerFallback"/>): the decision, the render-loop swap
/// around a stand-in windowing initializer that binds and resolves its loop the way Avalonia.Native 12.1.2 does, and
/// the fallback timer's idle behaviour. Runs on every OS: only the probe itself is macOS-specific. Each case runs in its
/// own <see cref="AvaloniaLocator"/> scope so the headless suites' bindings are never changed.
/// </summary>
public static class RenderTimerFallbackTests
{
    public static async Task RunAsync()
    {
        DisplayAvailableKeepsTheNativeLoop();
        NoDisplayInitializesOnTheFallbackLoop();
        OtherCoreVideoErrorsAlsoFallBack();
        FailedInitializationRestoresTheResolver();
        RequiresAWindowingSubsystem();
        await FallbackTimerIdlesWhenStoppedAsync();
        if (OperatingSystem.IsMacOS())
        {
            var result = CoreVideo.ProbeDisplayLink();
            Check($"RT-06 the CoreVideo probe returns success or kCVReturnInvalidDisplay (got {result})",
                result is CoreVideo.Success or CoreVideo.InvalidDisplay);
        }
        else
        {
            Skip("RT-06 the CoreVideo probe", "macOS only");
        }
    }

    private static void DisplayAvailableKeepsTheNativeLoop()
    {
        using var scope = AvaloniaLocator.EnterScope();
        var log = new RecordingAppLog();
        var platform = new FakeNativePlatform();
        var builder = platform.Builder().UseRenderTimerFallback(log, () => CoreVideo.Success);
        builder.WindowingSubsystemInitializer!();

        Check("RT-01 with a display, the windowing subsystem keeps its name", builder.WindowingSubsystemName == FakeNativePlatform.Name);
        Check("RT-01 ... initializes once, and its compositor gets the native loop",
            platform.Initializations == 1 && ReferenceEquals(platform.CompositorLoop, platform.NativeLoop));
        Check("RT-01 ... the native timer is started, the resolver is untouched and nothing is logged",
            platform.NativeTimer.Started && ReferenceEquals(AvaloniaLocator.Current, AvaloniaLocator.CurrentMutable) && log.Entries.Count == 0);
        Check("RT-01 ... the native loop stays bound", ReferenceEquals(AvaloniaLocator.Current.GetService<IRenderLoop>(), platform.NativeLoop));
    }

    private static void NoDisplayInitializesOnTheFallbackLoop()
    {
        using var scope = AvaloniaLocator.EnterScope();
        var log = new RecordingAppLog();
        var platform = new FakeNativePlatform();
        var builder = platform.Builder().UseRenderTimerFallback(log, () => CoreVideo.InvalidDisplay);
        builder.WindowingSubsystemInitializer!();

        Check("RT-02 without a display, the platform initializes once and its compositor gets a loop other than the native one",
            platform.Initializations == 1 && platform.CompositorLoop != null && !ReferenceEquals(platform.CompositorLoop, platform.NativeLoop));
        Check("RT-02 ... the native timer is never started (no CVDisplayLink registration, no crash)", !platform.NativeTimer.Started);
        Check("RT-02 ... services the platform binds while it initializes still resolve through the swapped resolver",
            platform.MarkerSeenDuringInitialize);
        Check("RT-02 ... afterwards the resolver is restored and the fallback loop is bound for every later consumer",
            ReferenceEquals(AvaloniaLocator.Current, AvaloniaLocator.CurrentMutable) &&
            ReferenceEquals(AvaloniaLocator.Current.GetService<IRenderLoop>(), platform.CompositorLoop));
        var entry = log.Entries.SingleOrDefault();
        Check("RT-02 ... one warn app.render_timer_fallback naming CVReturn -6661 (kCVReturnInvalidDisplay)",
            log.Entries.Count == 1 && entry is { Level: AppLogLevel.Warn, EventName: "app.render_timer_fallback" } &&
            entry.Message.Contains("-6661", StringComparison.Ordinal) && entry.Message.Contains("kCVReturnInvalidDisplay", StringComparison.Ordinal));
    }

    private static void OtherCoreVideoErrorsAlsoFallBack()
    {
        using var scope = AvaloniaLocator.EnterScope();
        var log = new RecordingAppLog();
        var platform = new FakeNativePlatform();
        platform.Builder().UseRenderTimerFallback(log, () => -6660).WindowingSubsystemInitializer!();
        Check("RT-03 any other CVReturn error also falls back, logged with its code",
            !platform.NativeTimer.Started && log.Entries.Count == 1 && log.Entries[0].Message.Contains("CVReturn -6660, CoreVideo error", StringComparison.Ordinal));
    }

    private static void FailedInitializationRestoresTheResolver()
    {
        using var scope = AvaloniaLocator.EnterScope();
        var resolver = AvaloniaLocator.Current;
        var builder = AppBuilder.Configure<Application>()
            .UseWindowingSubsystem(() => throw new InvalidOperationException("platform failed"), FakeNativePlatform.Name)
            .UseRenderTimerFallback(new RecordingAppLog(), () => CoreVideo.InvalidDisplay);
        string? message = null;
        try { builder.WindowingSubsystemInitializer!(); }
        catch (InvalidOperationException ex) { message = ex.Message; }
        Check("RT-04 a platform failure on the fallback path still propagates, and the resolver is restored",
            message == "platform failed" && ReferenceEquals(AvaloniaLocator.Current, resolver));
    }

    private static void RequiresAWindowingSubsystem()
    {
        string? message = null;
        try { AppBuilder.Configure<Application>().UseRenderTimerFallback(new RecordingAppLog(), () => CoreVideo.Success); }
        catch (InvalidOperationException ex) { message = ex.Message; }
        Check("RT-05 adding the fallback before a windowing subsystem is selected is rejected", message?.Contains("windowing subsystem", StringComparison.Ordinal) == true);
    }

    // Robust to loaded CI VMs (like HZ-08): the running checks wait for ticks up to a generous deadline instead of
    // counting them in a fixed window, so a starved timer thread slows the check down but cannot fail it. The rate is
    // printed as information only. The idle check lets an in-flight tick drain before its window opens.
    private const int RunningTicks = 5;
    private static readonly TimeSpan TickDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopSettle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMilliseconds(500);

    private static async Task FallbackTimerIdlesWhenStoppedAsync()
    {
        var timer = MacRenderTimerFallback.CreateFallbackTimer();
        var ticks = 0;
        var clock = Stopwatch.StartNew();
        timer.Tick = _ => Interlocked.Increment(ref ticks);
        var ticked = await Wait.Until(() => Volatile.Read(ref ticks) >= RunningTicks, TickDeadline);
        var elapsed = clock.Elapsed;
        timer.Tick = null;
        var running = Volatile.Read(ref ticks);
        Console.WriteLine($"  RT-07: {running} ticks in {elapsed.TotalMilliseconds:F0} ms " +
            $"(~{running / Math.Max(elapsed.TotalSeconds, 0.001):F0}/s observed, {MacRenderTimerFallback.FramesPerSecond} fps nominal)");
        Check($"RT-07 the fallback timer ticks while started (>= {RunningTicks} ticks within {TickDeadline.TotalSeconds:0} s) and runs in the background",
            ticked && timer.RunsInBackground);

        // Settle: a tick already past the loop's stop check may still land, so wait until the count holds still for
        // StopSettle (bounded by the deadline) before opening the idle window.
        int stopped;
        var settle = Stopwatch.StartNew();
        do
        {
            stopped = Volatile.Read(ref ticks);
            await Task.Delay(StopSettle);
        }
        while (Volatile.Read(ref ticks) != stopped && settle.Elapsed < TickDeadline);
        await Task.Delay(IdleWindow);
        var idle = Volatile.Read(ref ticks) - stopped;
        Check($"RT-07 ... and does not tick once stopped ({idle} ticks in {IdleWindow.TotalMilliseconds:0} ms after a {StopSettle.TotalMilliseconds:0} ms settle)", idle == 0);

        timer.Tick = _ => Interlocked.Increment(ref ticks);
        var restarted = await Wait.Until(() => Volatile.Read(ref ticks) > stopped, TickDeadline);
        timer.Tick = null;
        Check($"RT-07 ... and ticks again after a restart (within {TickDeadline.TotalSeconds:0} s)", restarted);
    }

    /// <summary>
    /// Stands in for Avalonia.Native's windowing initializer: binds a loop on its own timer into
    /// <see cref="AvaloniaLocator.CurrentMutable"/>, then resolves <see cref="IRenderLoop"/> through
    /// <see cref="AvaloniaLocator.Current"/> and starts it, the way its compositor does.
    /// </summary>
    private sealed class FakeNativePlatform
    {
        public const string Name = "FakeNative";

        public RecordingTimer NativeTimer { get; } = new();

        public IRenderLoop NativeLoop { get; }

        public IRenderLoop? CompositorLoop { get; private set; }

        public int Initializations { get; private set; }

        public bool MarkerSeenDuringInitialize { get; private set; }

        public FakeNativePlatform() => NativeLoop = RenderLoop.FromTimer(NativeTimer);

        public AppBuilder Builder() => AppBuilder.Configure<Application>().UseWindowingSubsystem(Initialize, Name);

        private void Initialize()
        {
            Initializations++;
            var marker = new Marker();
            AvaloniaLocator.CurrentMutable.Bind<Marker>().ToConstant(marker).Bind<IRenderLoop>().ToConstant(NativeLoop);
            MarkerSeenDuringInitialize = ReferenceEquals(AvaloniaLocator.Current.GetService<Marker>(), marker);
            CompositorLoop = AvaloniaLocator.Current.GetService<IRenderLoop>();
            // The compositor's first Add starts the timer; here only the native timer records being started.
            if (ReferenceEquals(CompositorLoop, NativeLoop)) NativeTimer.Tick = _ => { };
        }

        private sealed class Marker;
    }

    private sealed class RecordingTimer : IRenderTimer
    {
        public bool Started { get; private set; }

        public Action<TimeSpan>? Tick
        {
            get => null;
            set => Started |= value != null;
        }

        public bool RunsInBackground => true;
    }
}
