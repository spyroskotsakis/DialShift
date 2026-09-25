using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Rendering;
using DialShift.App.Interop;
using DialShift.Core.Playback;

namespace DialShift.App.Platform.MacOS;

/// <summary>
/// Keeps DialShift starting when macOS has no active display (displays asleep, a closed lid without an external
/// display, a headless Mac mini, a login while the screens are off). Avalonia.Native drives rendering from a
/// <c>CVDisplayLink</c>, and in Avalonia 12.1.2 it registers that link while the platform initializes. With no active
/// display the registration fails with <c>kCVReturnInvalidDisplay</c> (-6661) and <c>AppBuilder.Setup</c> throws, so
/// the tray, the schedule and playback would never start.
/// </summary>
/// <remarks>
/// Avalonia.Native has no option for another render timer and binds its render loop just before it creates the
/// compositor that starts it. That compositor resolves the loop through <see cref="AvaloniaLocator.Current"/>, which
/// is settable separately from <see cref="AvaloniaLocator.CurrentMutable"/> where the platform binds it. So when a
/// probe shows that no display link can be created, the platform is initialized behind a child locator scope that
/// answers <see cref="IRenderLoop"/> with a loop on Avalonia's <see cref="SleepLoopRenderTimer"/>, and that loop is then bound
/// for the rest of the process. With a display the platform initializes exactly as before. The fallback timer parks
/// its thread whenever the render loop has nothing to draw, so an idle tray app uses no CPU for it, and it renders
/// normally once a display wakes. It stays in use until DialShift next starts.
/// </remarks>
internal static class MacRenderTimerFallback
{
    /// <summary>Frames per second of the fallback timer, the rate a display link runs at on a 60 Hz display.</summary>
    internal const int FramesPerSecond = 60;

    /// <summary>Wraps <paramref name="builder"/>'s windowing subsystem (Avalonia.Native) with the fallback.</summary>
    [SupportedOSPlatform("macos")]
    public static AppBuilder UseRenderTimerFallback(this AppBuilder builder, IAppLog log)
        => builder.UseRenderTimerFallback(log, CoreVideo.ProbeDisplayLink);

    /// <summary>
    /// Wraps the windowing subsystem so that it probes <paramref name="probeDisplayLink"/> (a <c>CVReturn</c>) right
    /// before it initializes, and initializes on a fallback render loop, logging <c>app.render_timer_fallback</c>,
    /// when the probe fails.
    /// </summary>
    internal static AppBuilder UseRenderTimerFallback(this AppBuilder builder, IAppLog log, Func<int> probeDisplayLink)
    {
        var initialize = builder.WindowingSubsystemInitializer
            ?? throw new InvalidOperationException("Select the windowing subsystem before adding the render timer fallback.");
        return builder.UseWindowingSubsystem(() =>
        {
            var result = probeDisplayLink();
            if (result == CoreVideo.Success)
            {
                initialize();
                return;
            }
            log.Warn("app.render_timer_fallback", DescribeFallback(result));
            InitializeWithRenderLoop(initialize, RenderLoop.FromTimer(CreateFallbackTimer()));
        }, builder.WindowingSubsystemName ?? "");
    }

    /// <summary>The fallback timer: a thread that ticks at <see cref="FramesPerSecond"/> while the render loop has work and parks otherwise.</summary>
    internal static IRenderTimer CreateFallbackTimer() => new SleepLoopRenderTimer(FramesPerSecond);

    /// <summary>The <c>app.render_timer_fallback</c> message for a failed display-link probe.</summary>
    internal static string DescribeFallback(int cvReturn)
    {
        var cause = cvReturn == CoreVideo.InvalidDisplay ? "kCVReturnInvalidDisplay: no display is active" : "CoreVideo error";
        return $"CVDisplayLink can't be created (CVReturn {cvReturn}, {cause}). Rendering uses a {FramesPerSecond} fps " +
            "timer that idles when nothing changes, until DialShift restarts; the tray, schedule and playback are unaffected.";
    }

    /// <summary>
    /// Runs <paramref name="initialize"/> with <see cref="IRenderLoop"/> resolved as <paramref name="renderLoop"/>, then
    /// binds that loop over whatever the platform bound, so every later consumer uses the same loop.
    /// </summary>
    private static void InitializeWithRenderLoop(Action initialize, IRenderLoop renderLoop)
    {
        var resolver = AvaloniaLocator.Current;
        // A child scope answers IRenderLoop itself and defers everything else, including what the platform binds while
        // it initializes, to the real resolver.
        var scope = new AvaloniaLocator(resolver);
        scope.Bind<IRenderLoop>().ToConstant(renderLoop);
        AvaloniaLocator.Current = scope;
        try
        {
            initialize();
        }
        finally
        {
            AvaloniaLocator.Current = resolver;
        }
        AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(renderLoop);
    }
}
