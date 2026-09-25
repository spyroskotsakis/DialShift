using DialShift.Core;
using DialShift.Core.Playback;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Core;

/// <summary>
/// Pure <see cref="RetryPolicy"/> rules and the coordinator's timing constants (acceptance-matrix §5.1, BHV-35..39).
/// Values are characterized from the legacy <c>RadioController</c>; every check pins current behavior.
/// </summary>
public static class RetryPolicyTests
{
    public static void Run()
    {
        Constants();
        Backoff();
        Countdown();
        RetryTarget();
    }

    private static void Constants()
    {
        Check("§5.1 fallback after 3 consecutive failures", RetryPolicy.FallbackAfterFailures == 3);
        Check("§5.1 primary re-check 120 s after the fallback plays", RetryPolicy.PrimaryRecheckInterval == TimeSpan.FromSeconds(120));
        Check("§5.1 60 s of stable primary playback resets failures", RetryPolicy.StablePlaybackReset == TimeSpan.FromSeconds(60));
        Check("§5.1 stall watchdog 25 s (strict >)", RetryPolicy.StallTimeout == TimeSpan.FromSeconds(25));
        Check("§5.1 wake tick gap 15 s (monotonic, >=)", PlaybackCoordinator.WakeGapThreshold == TimeSpan.FromSeconds(15));
        Check("§5.1 wake network settle 2 s (OQ-7)", PlaybackCoordinator.WakeSettleDelay == TimeSpan.FromSeconds(2));
        Check("§4.4 wake debounce 10 s", PlaybackCoordinator.WakeDebounce == TimeSpan.FromSeconds(10));
    }

    private static void Backoff()
    {
        var delays = Enumerable.Range(1, 8).Select(n => (int)RetryPolicy.DelayAfterFailure(n).TotalSeconds).ToArray();
        Check("BHV-35 backoff is 3 s, 6 s, then 30 s forever", delays.SequenceEqual([3, 6, 30, 30, 30, 30, 30, 30]));
        Check("BHV-35 never gives up: failure 10 000 still waits 30 s", RetryPolicy.DelayAfterFailure(10_000) == TimeSpan.FromSeconds(30));
        Check("RetryPolicy: failure count below 1 is rejected", Throws<ArgumentOutOfRangeException>(() => RetryPolicy.DelayAfterFailure(0)));
    }

    private static void Countdown()
    {
        var three = TimeSpan.FromSeconds(3);
        Check("BHV-35 countdown: ceil(3 - 0) = 3", RetryPolicy.SecondsRemaining(three, TimeSpan.Zero) == 3);
        Check("BHV-35 countdown: ceil(3 - 1) = 2", RetryPolicy.SecondsRemaining(three, TimeSpan.FromSeconds(1)) == 2);
        Check("BHV-35 countdown: ceil(3 - 1.2) = 2 (rounds up)", RetryPolicy.SecondsRemaining(three, TimeSpan.FromSeconds(1.2)) == 2);
        Check("BHV-35 countdown: ceil(30 - 0.001) = 30", RetryPolicy.SecondsRemaining(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1)) == 30);
        Check("BHV-35 countdown: 0 when due", RetryPolicy.SecondsRemaining(three, three) == 0);
        Check("BHV-35 countdown: never negative when overdue", RetryPolicy.SecondsRemaining(three, TimeSpan.FromSeconds(9)) == 0);
    }

    private static void RetryTarget()
    {
        var a = new Station { Name = "A", Url = "https://a.example.org/" };
        var b = new Station { Name = "B", Url = "https://b.example.org/" };
        var settings = new Settings { Stations = [a, b], FallbackStationId = b.Id };

        Check("BHV-36 below 3 failures the desired station is retried", RetryPolicy.SelectRetryTarget(settings, a, 2, onFallback: false) == (a, false));
        Check("BHV-36 at 3 failures the configured fallback is chosen", RetryPolicy.SelectRetryTarget(settings, a, 3, onFallback: false) == (b, true));
        Check("BHV-36 above 3 failures the fallback is still chosen", RetryPolicy.SelectRetryTarget(settings, a, 7, onFallback: false) == (b, true));
        Check("BHV-37 [quirk] on the fallback the next retry returns to the primary (alternation)", RetryPolicy.SelectRetryTarget(settings, a, 4, onFallback: true) == (a, false));

        settings.FallbackStationId = a.Id;
        Check("CT-PB-09 fallback == desired behaves as no fallback", RetryPolicy.SelectRetryTarget(settings, a, 5, onFallback: false) == (a, false));
        settings.FallbackStationId = Guid.NewGuid();
        Check("RetryPolicy: a fallback id that is not a known station is ignored", RetryPolicy.SelectRetryTarget(settings, a, 5, onFallback: false) == (a, false));
        settings.FallbackStationId = null;
        Check("CT-PB-08 no fallback configured: always the desired station", RetryPolicy.SelectRetryTarget(settings, a, 5, onFallback: false) == (a, false));
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
