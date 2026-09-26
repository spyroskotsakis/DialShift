using System.Diagnostics;
using DialShift.App.Services;
using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;
using static DialShift.Tests.TestHarness;
using static DialShift.Tests.Ui.Headless;

namespace DialShift.Tests.Ui;

/// <summary>
/// HS-14 / MX-09: the production heartbeat (<see cref="PlaybackHost"/>, a real 1 s <see cref="PeriodicTimer"/> on the UI
/// thread, D18) driving the real <see cref="PlaybackCoordinator"/> over a <see cref="FakePlaybackEngine"/>. Wall time is
/// real time shifted so a slot boundary falls 1.3 s after the loop starts; the check is that the switch happens within 2 s
/// of that boundary. Quit cancels the loop and disposes the coordinator (BHV-43), bounded (CR-02).
/// </summary>
public static class PlaybackLoopTests
{
    public static async Task RunAsync()
    {
        await Headless.RunAsync(ScheduleFiresOnTheRealHeartbeat);
        await Headless.RunAsync(HostRules);
        await Headless.RunAsync(BoundedStop);
    }

    /// <summary>Real time moved by a fixed offset: it advances like the real clock but reads as the chosen "now".</summary>
    private sealed class OffsetClock(TimeSpan offset) : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + offset;
    }

    /// <summary>Counts heartbeat ticks reaching the coordinator, so a cancelled loop is observable.</summary>
    private sealed class CountingCoordinator(IPlaybackCoordinator inner) : IPlaybackCoordinator
    {
        private int ticks;
        public int Ticks => Volatile.Read(ref ticks);
        public PlaybackSnapshot Snapshot => inner.Snapshot;
        public event EventHandler<PlaybackSnapshot>? SnapshotChanged { add => inner.SnapshotChanged += value; remove => inner.SnapshotChanged -= value; }
        public Task PlayAsync(Guid stationId) => inner.PlayAsync(stationId);
        public Task ToggleAsync() => inner.ToggleAsync();
        public Task StopAsync() => inner.StopAsync();
        public Task NextStationAsync() => inner.NextStationAsync();
        public Task SetVolumeAsync(int volume) => inner.SetVolumeAsync(volume);
        public Task StartScheduleAsync() => inner.StartScheduleAsync();
        public Task RefreshScheduleAsync() => inner.RefreshScheduleAsync();
        public Task NotifyWakeAsync() => inner.NotifyWakeAsync();
        public Task NotifySettingsChangedAsync() => inner.NotifySettingsChangedAsync();
        public Task ForgetStationAsync(Guid stationId) => inner.ForgetStationAsync(stationId);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public Task OnTickAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ticks);
            return inner.OnTickAsync(cancellationToken);
        }
    }

    private static async Task ScheduleFiresOnTheRealHeartbeat()
    {
        var settings = new Settings
        {
            ScheduleEnabled = true,
            Stations =
            [
                new() { Name = "Alpha", Url = "https://a.example.org/live" },
                new() { Name = "Bravo", Url = "https://b.example.org/live" }
            ]
        };
        settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Monday] });
        settings.Schedule.Add(new ScheduleEntry { StationId = settings.Stations[1].Id, Time = "10:00", Days = [DayOfWeek.Monday] });

        // Monday 2026-09-14 10:00 UTC (the second slot) falls 1.3 s of real time from now.
        var boundary = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
        var realBoundary = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.3);
        var clock = new OffsetClock(boundary - realBoundary);
        var log = new RecordingAppLog();
        var engine = new FakePlaybackEngine();
        var coordinator = new CountingCoordinator(new PlaybackCoordinator(settings, engine, clock, StopwatchMonotonicClock.Instance, log, TimeZoneInfo.Utc));

        await coordinator.StartScheduleAsync();
        Check("HS-14 MX-09 BHV-10 startup catch-up opens the current slot (09:00 → Alpha)", engine.Starts.Count == 1 && engine.Starts[0].Source.Url.ToString() == settings.Stations[0].Url);
        engine.RaiseState(engine.LastSessionId, PlaybackEngineState.Playing);
        await coordinator.StopAsync();
        Check("HS-14 BHV-33 the user pauses within the slot", coordinator.Snapshot.Status == PlaybackStatus.ScheduledWaiting && !coordinator.Snapshot.IsActive);

        var host = new PlaybackHost(coordinator, log);
        host.Start();
        var fired = await WaitAsync(() => engine.Starts.Count >= 2);
        var lag = DateTimeOffset.UtcNow - realBoundary;
        Console.WriteLine($"  the 10:00 slot fired {lag.TotalMilliseconds:F0} ms after its boundary ({coordinator.Ticks} ticks)");
        Check("HS-14 MX-09 BHV-44 on the real 1 s heartbeat the next slot fires within 2 s of its boundary, even though paused",
            fired && lag >= TimeSpan.FromMilliseconds(-50) && lag <= TimeSpan.FromSeconds(2));
        Check("HS-14 BHV-44 it opens the slot's station (Bravo) as a schedule play",
            engine.Starts[1].Source.Url.ToString() == settings.Stations[1].Url && coordinator.Snapshot.IsActive && log.HasEvent("schedule.fired"));

        var clean = await host.StopAsync();
        var ticks = coordinator.Ticks;
        Check("HS-14 BHV-11 quit: the heartbeat stops cleanly and disposes the coordinator, which disposes the engine",
            clean && engine.IsDisposed && coordinator.Snapshot.Status == PlaybackStatus.Disposing);
        var starts = engine.Starts.Count;
        await coordinator.PlayAsync(settings.Stations[0].Id);
        var quiet = new TestDeadline(TimeSpan.FromSeconds(1.5));
        await WaitAsync(() => quiet.HasPassed);
        Check("HS-14 BHV-43 after quit no tick runs and no command starts playback", coordinator.Ticks == ticks && engine.Starts.Count == starts);
        Check("HS-14 no tick failed", !log.HasEvent("playback.tick_failed"));
    }

    private static async Task HostRules()
    {
        var journal = new Journal();
        var fake = new CountingCoordinator(new FakeCoordinator(journal));
        var log = new RecordingAppLog();
        var offThread = await Task.Run(() => Throws<InvalidOperationException>(() => new PlaybackHost(fake, log).Start()));
        Check("HS-14 D18 PlaybackHost.Start refuses to run without the UI synchronization context", offThread);

        var host = new PlaybackHost(fake, log);
        host.Start();
        host.Start();
        var ticking = new TestDeadline(TimeSpan.FromSeconds(2.4));
        await WaitAsync(() => ticking.HasPassed);
        Console.WriteLine($"  ticks in 2.4 s after two Start calls: {fake.Ticks}");
        Check("HS-14 D18 Start is idempotent: one 1 s loop (2 ticks in 2.4 s, not 4)", fake.Ticks is >= 1 and <= 3);
        Check("HS-14 D18 ticks run on the UI thread", Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
        await host.StopAsync();
        Check("HS-14 quit disposes the coordinator", journal.Entries.Contains("coordinator.DisposeAsync"));
    }

    private static async Task BoundedStop()
    {
        var journal = new Journal();
        var hanging = new FakeCoordinator(journal) { HoldDispose = true };
        var log = new RecordingAppLog();
        var host = new PlaybackHost(hanging, log);
        host.Start();
        var watch = Stopwatch.StartNew();
        var stop = host.StopAsync(TimeSpan.FromMilliseconds(200));
        var finished = await CompletesAsync(stop);
        watch.Stop();
        Check("HS-14 CR-02 HZ-02 StopAsync returns false after the dispose timeout instead of hanging",
            finished && !stop.Result && watch.Elapsed < TimeSpan.FromSeconds(3));
        Check("HS-14 CR-02 the timeout is logged as app.quit.dispose_timeout", log.Entries.Any(e => e.EventName == "app.quit.dispose_timeout" && e.Level == AppLogLevel.Warn));
    }
}
