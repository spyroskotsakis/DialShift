using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DialShift.Core.Playback;

namespace DialShift.App.Platform.MacOS;

/// <summary>
/// Sleep-inclusive monotonic clock for macOS (brief 1 §4.1a/§4.4): <c>clock_gettime_nsec_np(CLOCK_MONOTONIC)</c>,
/// in nanoseconds.
/// </summary>
/// <remarks>
/// .NET's <see cref="System.Diagnostics.Stopwatch"/> on macOS reads <c>CLOCK_UPTIME_RAW</c>, which stops while the
/// Mac sleeps, so a tick gap measured with it never shows the sleep and the timer-gap wake fallback would never fire.
/// Darwin's <c>CLOCK_MONOTONIC</c> keeps counting during sleep and is not affected by wall-clock changes
/// (<c>settimeofday</c>, NTP steps, time-zone changes). <c>_CLOCK_MONOTONIC = 6</c> is taken from the SDK's
/// <c>usr/include/_time.h</c>.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class MacMonotonicClock : IMonotonicClock
{
    private const int ClockMonotonic = 6;

    public static MacMonotonicClock Instance { get; } = new();

    private MacMonotonicClock() { }

    public long GetTimestamp() => (long)clock_gettime_nsec_np(ClockMonotonic);

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromTicks((endingTimestamp - startingTimestamp) / TimeSpan.NanosecondsPerTick);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clock_gettime_nsec_np")]
    private static partial ulong clock_gettime_nsec_np(int clockId);
}
