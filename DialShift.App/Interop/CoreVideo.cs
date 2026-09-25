// CoreVideo display-link probe used by MacRenderTimerFallback before Avalonia.Native starts.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DialShift.App.Interop;

/// <summary>
/// Makes the same <c>CVDisplayLinkCreateWithActiveCGDisplays</c> call that Avalonia.Native's render timer makes when it
/// registers, so the app can tell in advance whether that registration will fail.
/// </summary>
/// <remarks>Only the calls are macOS-only; the <c>CVReturn</c> constants are plain values any platform may compare.</remarks>
internal static partial class CoreVideo
{
    private const string CoreVideoPath = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    /// <summary><c>kCVReturnSuccess</c>.</summary>
    public const int Success = 0;

    /// <summary><c>kCVReturnInvalidDisplay</c>: no display is active (all asleep, lid closed without an external display).</summary>
    public const int InvalidDisplay = -6661;

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreVideoPath, EntryPoint = "CVDisplayLinkCreateWithActiveCGDisplays")]
    private static partial int CVDisplayLinkCreateWithActiveCGDisplays(out nint displayLink);

    [SupportedOSPlatform("macos")]
    [LibraryImport(CoreVideoPath, EntryPoint = "CVDisplayLinkRelease")]
    private static partial void CVDisplayLinkRelease(nint displayLink);

    /// <summary>Creates and releases a display link for the active displays; returns its <c>CVReturn</c>.</summary>
    [SupportedOSPlatform("macos")]
    public static int ProbeDisplayLink()
    {
        var result = CVDisplayLinkCreateWithActiveCGDisplays(out var displayLink);
        if (displayLink != 0) CVDisplayLinkRelease(displayLink);
        return result;
    }
}
