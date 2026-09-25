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
    private const int Samples = 5;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

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

        // Unit conversion and rate against Stopwatch, robust to loaded CI VMs (HZ-08): several paired samples over a
        // 300 ms sleep, judged on the median, so one oversleep or one preemption between paired reads cannot fail the check.
        // The bounds still catch any unit error: a wrong tick unit is off by 10x or more, far outside [0.5x, 3x].
        var samples = new List<(double Clock, double Stopwatch)>();
        for (var i = 0; i < Samples; i++)
        {
            var stopwatchStart = Stopwatch.GetTimestamp();
            var start = clock.GetTimestamp();
            Thread.Sleep(Delay);
            var end = clock.GetTimestamp();
            var stopwatchElapsed = Stopwatch.GetElapsedTime(stopwatchStart);
            samples.Add((clock.GetElapsedTime(start, end).TotalMilliseconds, stopwatchElapsed.TotalMilliseconds));
        }
        var medianElapsed = Median(samples.Select(s => s.Clock));
        var medianRate = Median(samples.Select(s => s.Clock / s.Stopwatch));
        Console.WriteLine($"  {name}: {Samples} x {Delay.TotalMilliseconds:0} ms sleeps measured "
            + string.Join(", ", samples.Select(s => $"{s.Clock:F1}/{s.Stopwatch:F1}")) + $" ms (clock/Stopwatch); median {medianElapsed:F1} ms, rate {medianRate:F3}");
        Check($"§8.2.8 HZ-08 {name}: the median GetElapsedTime over a {Delay.TotalMilliseconds:0} ms sleep is within [0.5x, 3x] of it (unit conversion)",
            medianElapsed >= Delay.TotalMilliseconds * 0.5 && medianElapsed <= Delay.TotalMilliseconds * 3);
        // Elapsed times, not absolute values, are comparable across the two clocks; with no sleep in the interval the
        // sleep-inclusive clock and Stopwatch advance at the same rate. ±10% covers GetTickCount64's ~16 ms resolution.
        Check($"§8.2.8 HZ-08 {name}: over the same intervals it advances at Stopwatch's rate (median ratio within ±10%)",
            Math.Abs(medianRate - 1.0) <= 0.10);

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

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
