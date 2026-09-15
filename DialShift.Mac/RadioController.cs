using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DialShift.Core;
using LibVLCSharp.Shared;

namespace DialShift;

public sealed class RadioController : IDisposable
{
    private readonly Settings settings;
    private readonly Task<LibVLC> engine;
    private int generation;
    private MediaPlayer? player;
    private readonly List<Task> retiring = new();
    private readonly DispatcherTimer timer;
    private readonly Stopwatch activity = new();
    private readonly Stopwatch retryClock = new();
    private readonly ScheduleSession scheduleSession = new();
    private readonly Stopwatch stablePlayback = new();
    private DateTime lastTick = DateTime.UtcNow;
    private int failures;
    private double retrySeconds;
    private bool disposed;
    private bool fallback;
    private bool failurePending;
    public Station? Desired { get; private set; }
    public Station? Current { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsPlaying { get; private set; }
    public string Status { get; private set; } = "Ready when you are";
    public string Track { get; private set; } = "Choose a station and make yourself at home.";
    public Occurrence? Next => Scheduler.Evaluate(settings, DateTime.Now).Next;
    public event Action? Changed;

    public RadioController(Settings settings)
    {
        this.settings = settings;
        engine = Task.Run(() =>
        {
            LibVLCSharp.Shared.Core.Initialize();
            return new LibVLC("--no-video", "--no-osd", "--network-caching=1500", "--http-reconnect");
        });
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void StartSchedule() => CheckSchedule(true);
    public void RefreshSchedule()
    {
        CheckSchedule(true);
        Changed?.Invoke();
    }

    private void CheckSchedule(bool force = false)
    {
        if (!settings.ScheduleEnabled) return;
        var slot = scheduleSession.TakeChange(settings, DateTime.Now, force);
        if (slot == null) return;
        var station = settings.Stations.FirstOrDefault(s => s.Id == slot.Entry.StationId);
        if (station != null) Play(station, false);
    }

    public void Play(Station station, bool manual = true)
    {
        if (manual) scheduleSession.HoldCurrent(settings, DateTime.Now);
        Desired = station;
        settings.LastStationId = station.Id;
        failures = 0;
        fallback = false;
        IsActive = true;
        Open(station);
    }

    public void Toggle()
    {
        if (IsActive) Pause();
        else if ((Desired ?? settings.Stations.FirstOrDefault(s => s.Id == settings.LastStationId) ?? settings.Stations.FirstOrDefault()) is { } station)
            Play(station);
    }

    public void Pause()
    {
        scheduleSession.HoldCurrent(settings, DateTime.Now);
        IsActive = false;
        IsPlaying = false;
        retryClock.Reset();
        Retire();
        Status = settings.ScheduleEnabled ? "Paused · resumes at the next scheduled change" : "Paused";
        Track = "Press play to return to the live broadcast.";
        Changed?.Invoke();
    }

    public void NextStation()
    {
        if (settings.Stations.Count == 0) return;
        var index = settings.Stations.FindIndex(s => s.Id == (Desired?.Id ?? Current?.Id));
        Play(settings.Stations[(index + 1) % settings.Stations.Count]);
    }

    public void SetVolume(int volume)
    {
        settings.Volume = Math.Clamp(volume, 0, 100);
        if (player != null) { player.Volume = settings.Volume; player.Mute = settings.Volume == 0; }
    }

    public void ResumeFromSleep()
    {
        CheckSchedule();
        if (IsActive && Desired != null) { failures = 0; fallback = false; Open(Desired); }
    }

    public void ForgetStation(Guid id)
    {
        if (Desired?.Id == id) Desired = null;
        if (Current?.Id == id) Current = null;
        Changed?.Invoke();
    }

    private async void Open(Station station)
    {
        Retire();
        var attempt = ++generation;
        var previousStopped = Task.WhenAll(retiring);
        retryClock.Reset();
        failurePending = false;
        stablePlayback.Reset();
        Current = station;
        IsPlaying = false;
        Status = fallback ? "Connecting to fallback…" : "Connecting…";
        Track = "Opening the live stream";
        activity.Restart();
        Changed?.Invoke();
        LibVLC vlc;
        try { vlc = await engine; await previousStopped; }
        catch (Exception ex) { if (!disposed && attempt == generation && IsActive) { App.Log(ex); Fail(); } return; }
        if (disposed || attempt != generation || !IsActive) return;
        activity.Restart();
        var session = new MediaPlayer(vlc);
        player = session;
        void OnUi(Action action) => Dispatcher.UIThread.Post(() => { if (!disposed && player == session && IsActive) action(); });
        session.Playing += (_, _) => OnUi(() =>
        {
            session.Volume = settings.Volume;
            session.Mute = settings.Volume == 0;
            IsPlaying = true;
            Status = fallback ? "Live · fallback station" : "Live broadcast";
            activity.Restart();
            stablePlayback.Restart();
            Changed?.Invoke();
        });
        session.TimeChanged += (_, _) => OnUi(() => activity.Restart());
        session.EncounteredError += (_, _) => OnUi(Fail);
        session.EndReached += (_, _) => OnUi(Fail);
        try
        {
            using var media = new Media(vlc, new Uri(station.Url));
            media.AddOption(":no-video");
            session.Volume = settings.Volume;
            session.Mute = settings.Volume == 0;
            if (!session.Play(media)) Fail();
        }
        catch (Exception ex) { App.Log(ex); Fail(); }
        Changed?.Invoke();
    }

    private void Fail()
    {
        if (!IsActive || failurePending) return;
        failurePending = true;
        IsPlaying = false;
        failures++;
        Retire();
        retrySeconds = failures < 3 ? failures * 3 : 30;
        Status = $"Stream unavailable · retry in {retrySeconds:0}s";
        Track = "Your next scheduled change will still run.";
        retryClock.Restart();
        Changed?.Invoke();
    }

    private void Tick()
    {
        if (disposed) return;
        var now = DateTime.UtcNow;
        var gap = now - lastTick;
        lastTick = now;
        // The timer pauses while the Mac sleeps; a large gap means we just woke up.
        if (gap.TotalSeconds >= 15) ResumeFromSleep();
        CheckSchedule();
        if (IsPlaying && !fallback && stablePlayback.Elapsed.TotalSeconds >= 60) failures = 0;
        if (retryClock.IsRunning && !IsPlaying) Status = $"Stream unavailable · retry in {Math.Max(0, Math.Ceiling(retrySeconds - retryClock.Elapsed.TotalSeconds)):0}s";
        if (IsActive && retryClock.IsRunning && retryClock.Elapsed.TotalSeconds >= retrySeconds && Desired != null)
        {
            var backup = settings.Stations.FirstOrDefault(s => s.Id == settings.FallbackStationId && s.Id != Desired.Id);
            fallback = failures >= 3 && backup != null && !fallback;
            Open(fallback ? backup! : Desired);
        }
        else if (IsActive && player != null && activity.Elapsed.TotalSeconds > 25) Fail();
        if (fallback && IsPlaying && retryClock.IsRunning == false) retryClock.Restart();
        if (fallback && IsPlaying) retrySeconds = 120;
        if (IsPlaying && player?.Media is { } media)
        {
            using (media)
            {
                var title = media.Meta(MetadataType.NowPlaying);
                Track = string.IsNullOrWhiteSpace(title) ? Current?.Tag ?? "Live radio" : title;
            }
        }
        Changed?.Invoke();
    }

    private void Retire()
    {
        var old = player;
        generation++;
        player = null;
        if (old == null) return;
        retiring.RemoveAll(t => t.IsCompleted);
        retiring.Add(Task.Run(() => { try { old.Stop(); old.Dispose(); } catch (Exception ex) { App.Log(ex); } }));
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        IsActive = false;
        Retire();
        _ = ReleaseEngine();
    }

    private async Task ReleaseEngine()
    {
        try { await Task.WhenAll(retiring); (await engine).Dispose(); }
        catch (Exception ex) { App.Log(ex); }
    }
}
