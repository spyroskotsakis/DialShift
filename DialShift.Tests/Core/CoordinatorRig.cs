using DialShift.Core;
using DialShift.Core.Playback;
using DialShift.Tests.Fakes;

namespace DialShift.Tests.Core;

/// <summary>Fixed zones for coordinator tests. Always injected explicitly, so no check depends on the host's zone.</summary>
public static class Zones
{
    public static TimeZoneInfo Utc => TimeZoneInfo.Utc;

    /// <summary>Europe/Athens (EET/EEST): +03:00 in September; DST 2026-03-29 03:00→04:00 and 2026-10-25 04:00→03:00.</summary>
    public static TimeZoneInfo Athens { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
}

/// <summary>
/// The acceptance-matrix §7.1 harness around one <see cref="PlaybackCoordinator"/>: stations A/B/C, optional slots
/// (A Mon 09:00, B Mon 11:00), fake wall + monotonic clocks, a scriptable engine, a recording log, and every published
/// <see cref="PlaybackSnapshot"/>. The default "now" is Monday 2026-09-14 10:00 in the injected zone (UTC by default).
/// </summary>
public sealed class CoordinatorRig : IAsyncDisposable
{
    private readonly Lock publishedGate = new();
    private readonly List<PlaybackSnapshot> published = [];

    private CoordinatorRig(Settings settings, FakePlaybackEngine engine, IPlaybackEngine port, FakeClock clock, TimeZoneInfo zone)
    {
        Settings = settings;
        Engine = engine;
        Clock = clock;
        Zone = zone;
        Coordinator = new PlaybackCoordinator(settings, port, clock, Mono, Log, zone);
        Coordinator.SnapshotChanged += (_, s) => { lock (publishedGate) published.Add(s); };
    }

    public Settings Settings { get; }
    public FakePlaybackEngine Engine { get; }
    public FakeClock Clock { get; }
    public FakeMonotonicClock Mono { get; } = new();
    public RecordingAppLog Log { get; } = new();
    public TimeZoneInfo Zone { get; }
    public PlaybackCoordinator Coordinator { get; }

    public Station A => Settings.Stations[0];
    public Station B => Settings.Stations[1];
    public Station C => Settings.Stations[2];
    public ScheduleEntry? SlotA { get; private set; }
    public ScheduleEntry? SlotB { get; private set; }

    public PlaybackSnapshot S => Coordinator.Snapshot;
    public PlaybackStatus Status => Coordinator.Snapshot.Status;

    /// <summary>Id of the newest engine session (the coordinator's current attempt when one is open).</summary>
    public long Session => Engine.LastSessionId;
    public int StartCount => Engine.Starts.Count;
    public EngineStartCall LastStart => Engine.Starts[^1];

    public IReadOnlyList<PlaybackSnapshot> Published { get { lock (publishedGate) return [.. published]; } }

    /// <param name="schedule">Settings.ScheduleEnabled.</param>
    /// <param name="slots">Adds slot A (Mon 09:00 → A) and slot B (Mon 11:00 → B).</param>
    /// <param name="zone">Injected local zone; UTC when null.</param>
    /// <param name="localNow">Wall-clock "now" in <paramref name="zone"/>; Monday 2026-09-14 10:00 when null.</param>
    /// <param name="plainEngine">Hide the metadata capability from the coordinator (CT-PB-37).</param>
    /// <param name="urls">Optional stream URLs for A/B/C (e.g. secret-bearing URLs for CT-LOG).</param>
    public static CoordinatorRig Create(bool schedule = false, bool slots = true, TimeZoneInfo? zone = null, DateTime? localNow = null,
        bool plainEngine = false, string[]? urls = null)
    {
        zone ??= Zones.Utc;
        var local = DateTime.SpecifyKind(localNow ?? new DateTime(2026, 9, 14, 10, 0, 0), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        var settings = new Settings
        {
            ScheduleEnabled = schedule,
            Stations =
            [
                new() { Name = "Alpha", Tag = "Tag A", Url = urls?[0] ?? "https://a.example.org/live" },
                new() { Name = "Bravo", Tag = "Tag B", Url = urls?[1] ?? "https://b.example.org/live" },
                new() { Name = "Charlie", Tag = "Tag C", Url = urls?[2] ?? "https://c.example.org/live" }
            ]
        };
        var engine = new FakePlaybackEngine();
        IPlaybackEngine port = plainEngine ? new PlainPlaybackEngine(engine) : engine;
        var rig = new CoordinatorRig(settings, engine, port, new FakeClock(new DateTimeOffset(utc, TimeSpan.Zero)), zone);
        if (slots)
        {
            rig.SlotA = new ScheduleEntry { StationId = settings.Stations[0].Id, Time = "09:00", Days = [DayOfWeek.Monday] };
            rig.SlotB = new ScheduleEntry { StationId = settings.Stations[1].Id, Time = "11:00", Days = [DayOfWeek.Monday] };
            settings.Schedule.AddRange([rig.SlotA, rig.SlotB]);
        }
        return rig;
    }

    /// <summary>§7.1 <c>Step(n)</c>: advances both clocks by 1 s and awaits one tick, <paramref name="n"/> times.</summary>
    public async Task Step(int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Mono.Advance(TimeSpan.FromSeconds(1));
            await Coordinator.OnTickAsync(CancellationToken.None);
        }
    }

    /// <summary>One tick after advancing the monotonic clock by <paramref name="mono"/> and the wall clock by <paramref name="wall"/> (default: same).</summary>
    public Task Jump(TimeSpan mono, TimeSpan? wall = null)
    {
        Mono.Advance(mono);
        Clock.Advance(wall ?? mono);
        return Coordinator.OnTickAsync(CancellationToken.None);
    }

    /// <summary>Sets the wall clock to <paramref name="local"/> in the rig's zone (monotonic time does not move).</summary>
    public void SetLocal(DateTime local) =>
        Clock.UtcNow = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone), TimeSpan.Zero);

    /// <summary>The engine reports Playing for the newest session.</summary>
    public void Playing() => Engine.RaiseState(Session, PlaybackEngineState.Playing);

    /// <summary>The engine reports a failure for the newest session.</summary>
    public void Fail(PlaybackFailureKind kind = PlaybackFailureKind.NetworkUnavailable) => Engine.RaiseFailed(Session, kind);

    public int LogCount(string eventName) => Log.Entries.Count(e => e.EventName == eventName);

    public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
}

/// <summary>Small async helpers shared by the coordinator suites.</summary>
public static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until true or <paramref name="timeout"/> (a <see cref="TestDeadline"/>) passes. Bounds a test's wait only.</summary>
    public static async Task<bool> Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = new TestDeadline(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (deadline.HasPassed) return false;
            await Task.Delay(1);
        }
        return true;
    }

    /// <summary>True when <paramref name="task"/> finishes within <paramref name="timeout"/> (a deadlock detector).</summary>
    public static async Task<bool> Finishes(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
