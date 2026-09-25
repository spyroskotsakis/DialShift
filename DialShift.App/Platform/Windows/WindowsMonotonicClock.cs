using System.Runtime.Versioning;
using DialShift.Core.Playback;

namespace DialShift.App.Platform.Windows;

/// <summary>
/// Sleep-inclusive monotonic clock for Windows (brief 1 §4.1a/§4.4), in milliseconds from
/// <see cref="Environment.TickCount64"/> (<c>GetTickCount64</c>).
/// </summary>
/// <remarks>
/// The tick-gap wake fallback needs a clock that keeps counting while the machine sleeps or hibernates and ignores
/// wall-clock changes. <c>GetTickCount64</c> is documented to include time spent in sleep and hibernation, and it is
/// not affected by system-time changes. <see cref="System.Diagnostics.Stopwatch"/> (QPC) is not used because whether
/// QPC advances across S3/S4 depends on hardware and Windows version; <c>QueryUnbiasedInterruptTime</c> explicitly
/// excludes sleep. The 10–16 ms resolution is ample for a 15 s gap check and the coordinator's 1 s timers.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMonotonicClock : IMonotonicClock
{
    public static WindowsMonotonicClock Instance { get; } = new();

    private WindowsMonotonicClock() { }

    public long GetTimestamp() => Environment.TickCount64;

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromMilliseconds(endingTimestamp - startingTimestamp);
}
