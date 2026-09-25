using System.Diagnostics;
using DialShift.App.Platform.MacOS;
using DialShift.App.Platform.Windows;
using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Platform;

/// <summary>
/// The per-OS sleep-inclusive <see cref="IMonotonicClock"/> (acceptance matrix §8.2.8, D14). Sleep itself is native-only
/// (NC-02, NC-08); these checks cover monotonicity, unit conversion and agreement with <see cref="Stopwatch"/>.
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

        // Retry once: a loaded CI runner can stall a thread between the paired reads.
        var agrees = false;
        for (var attempt = 0; attempt < 2 && !agrees; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            var start = clock.GetTimestamp();
            Thread.Sleep(200);
            var elapsed = clock.GetElapsedTime(start, clock.GetTimestamp());
            stopwatch.Stop();
            var difference = (elapsed - stopwatch.Elapsed).Duration();
            Console.WriteLine($"  {name}: 200 ms sleep measured {elapsed.TotalMilliseconds:F1} ms (Stopwatch {stopwatch.Elapsed.TotalMilliseconds:F1} ms)");
            agrees = (elapsed - TimeSpan.FromMilliseconds(200)).Duration() <= TimeSpan.FromMilliseconds(50) && difference <= TimeSpan.FromMilliseconds(50);
        }
        Check($"§8.2.8 {name}: GetElapsedTime over a 200 ms sleep is 200 ms ±50 ms and within 50 ms of Stopwatch", agrees);

        if (OperatingSystem.IsMacOS())
        {
            Check("§8.2.8 mac: timestamps are nanoseconds: GetElapsedTime(0, 1e9) = 1 s", clock.GetElapsedTime(0, 1_000_000_000) == TimeSpan.FromSeconds(1));
            Check("§8.2.8 mac: 100 ns = one tick, sub-tick remainders truncate", clock.GetElapsedTime(0, 150) == TimeSpan.FromTicks(1));

            // Stopwatch on macOS reads CLOCK_UPTIME_RAW, which stops during sleep; CLOCK_MONOTONIC must never be behind it.
            if (Stopwatch.Frequency == 1_000_000_000)
            {
                var uptime = Stopwatch.GetTimestamp();
                var monotonic = clock.GetTimestamp();
                Console.WriteLine($"  mac: CLOCK_MONOTONIC - uptime = {TimeSpan.FromTicks((monotonic - uptime) / 100)} (time this Mac has slept since boot)");
                Check("§8.2.8 mac: the clock is >= Stopwatch uptime (sleep-inclusive)", monotonic >= uptime);
            }
            else
            {
                Skip("§8.2.8 mac: the clock is >= Stopwatch uptime", $"Stopwatch.Frequency is {Stopwatch.Frequency}, not nanoseconds, so the values are not comparable");
            }
        }
        else
        {
            Check("§8.2.8 win: timestamps are milliseconds: GetElapsedTime(0, 1000) = 1 s", clock.GetElapsedTime(0, 1000) == TimeSpan.FromSeconds(1));
            Check("§8.2.8 win: the clock is Environment.TickCount64", Math.Abs(clock.GetTimestamp() - Environment.TickCount64) <= 100);
        }
        Check($"§8.2.8 {name}: a reversed pair gives a negative span", clock.GetElapsedTime(1_000_000, 0) < TimeSpan.Zero);
    }
}
