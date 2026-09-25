using System.Diagnostics;
using DialShift.App.Platform.MacOS;
using DialShift.App.Platform.Windows;
using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Platform;

/// <summary>
/// The per-OS sleep-inclusive <see cref="IMonotonicClock"/> (acceptance matrix §8.2.8, D14). Sleep itself is native-only
/// (NC-02, NC-08); these checks cover monotonicity, unit conversion and elapsed-time agreement with <see cref="Stopwatch"/>.
/// </summary>
public static class MonotonicClockTests
{
    private const int Reads = 100_000;

    public static void Run()
    {
        IMonotonicClock clock;
        string name;
        if (OperatingSystem.IsMacOS()) { clock = MacMonotonicClock.Instance; name = "mac CLOCK_MONOTONIC"; }
        else if (OperatingSystem.IsWindows()) { clock = WindowsMonotonicClock.Instance; name = "win GetTickCount64"; }
        else
        {
            Skip("§8.2.8 platform monotonic clock", "the App supports only macOS and Windows");
            return;
        }

        var previous = clock.GetTimestamp();
        var decreases = 0;
        for (var i = 0; i < Reads; i++)
        {
            var next = clock.GetTimestamp();
            if (next < previous) decreases++;
            previous = next;
        }
        Check($"§8.2.8 {name}: {Reads:N0} consecutive reads never decrease", decreases == 0);
        Check($"§8.2.8 {name}: timestamps are positive (time since boot)", clock.GetTimestamp() > 0);

        // Paired reads over one 200 ms sleep. Retried: a loaded CI runner (macos-latest is a VM) can oversleep or stall a
        // thread between the paired reads; every attempt is printed.
        var sleepAgrees = false;
        var rateAgrees = false;
        for (var attempt = 0; attempt < 3 && !(sleepAgrees && rateAgrees); attempt++)
        {
            var stopwatchStart = Stopwatch.GetTimestamp();
            var start = clock.GetTimestamp();
            Thread.Sleep(200);
            var end = clock.GetTimestamp();
            var stopwatchElapsed = Stopwatch.GetElapsedTime(stopwatchStart);
            var elapsed = clock.GetElapsedTime(start, end);
            Console.WriteLine($"  {name}: 200 ms sleep measured {elapsed.TotalMilliseconds:F1} ms (Stopwatch {stopwatchElapsed.TotalMilliseconds:F1} ms)");
            sleepAgrees = (elapsed - TimeSpan.FromMilliseconds(200)).Duration() <= TimeSpan.FromMilliseconds(50);
            rateAgrees = (elapsed - stopwatchElapsed).Duration() <= TimeSpan.FromMilliseconds(50);
        }
        Check($"§8.2.8 {name}: GetElapsedTime over a 200 ms sleep is 200 ms ±50 ms", sleepAgrees);
        // Elapsed times, not absolute values, are comparable across the two clocks; with no sleep in the interval the
        // sleep-inclusive clock and Stopwatch advance at the same rate.
        Check($"§8.2.8 {name}: over the same interval its elapsed time is within ±50 ms of Stopwatch's (no sleep occurred)", rateAgrees);

        if (OperatingSystem.IsMacOS())
        {
            Check("§8.2.8 mac: timestamps are nanoseconds: GetElapsedTime(0, 1e9) = 1 s", clock.GetElapsedTime(0, 1_000_000_000) == TimeSpan.FromSeconds(1));
            Check("§8.2.8 mac: 100 ns = one tick, sub-tick remainders truncate", clock.GetElapsedTime(0, 150) == TimeSpan.FromTicks(1));

            // Informational only: CLOCK_MONOTONIC (wall time minus boot time, counts during sleep) and Stopwatch
            // (CLOCK_UPTIME_RAW, mach_absolute_time) have unrelated epochs on some hosts, notably VMs, so their absolute
            // values are not a contract. On a physical Mac the difference is roughly the time it has slept since boot.
            var uptime = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            var monotonic = clock.GetElapsedTime(0, clock.GetTimestamp());
            Console.WriteLine($"  mac (info, not checked): CLOCK_MONOTONIC {monotonic} - Stopwatch {uptime} = {monotonic - uptime}");
        }
        else
        {
            Check("§8.2.8 win: timestamps are milliseconds: GetElapsedTime(0, 1000) = 1 s", clock.GetElapsedTime(0, 1000) == TimeSpan.FromSeconds(1));
            Check("§8.2.8 win: the clock is Environment.TickCount64", Math.Abs(clock.GetTimestamp() - Environment.TickCount64) <= 100);
        }
        Check($"§8.2.8 {name}: a reversed pair gives a negative span", clock.GetElapsedTime(1_000_000, 0) < TimeSpan.Zero);
    }
}
