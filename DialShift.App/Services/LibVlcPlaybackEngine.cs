// LibVlcPlaybackEngine: IPlaybackEngine on LibVLC 3 via LibVLCSharp 3.10.1 (brief 1 §7.8). Windows only at runtime
// (the VideoLAN.LibVLC.Windows native package ships only in the win-x64 artifact); it compiles in every build.
//
// Carried over from the pre-refactor playback controller: LibVLC is created off the UI thread at construction with
// "--no-video --no-osd --network-caching=1500 --http-reconnect"; media gets ":no-video"; one MediaPlayer per session;
// Mute when the volume is 0; "now playing" from Media.Meta(NowPlaying), polled once a second while the input plays (as
// the legacy tick did); the previous player is stopped and disposed on a pool thread, and the next player is created
// only after that finished (never two audible players).
// Media.MetaChanged is not subscribed: the poll alone delivered ICY titles in the harness, and it keeps one fewer
// native callback path alive across rapid session churn. ICY titles need http:// (LibVLC 3's https:// access module
// does not request Icy-MetaData), so https stations show the station tag instead.
//
// ─── Threading ────────────────────────────────────────────────────────────────────────────────────────────────────────
// • Public members may be called from any thread; `gate` guards the session bookkeeping and is never held across an
//   await or while raising an event.
// • LibVLC raises its events on its own threads, where calling back into LibVLC can deadlock. Handlers therefore only
//   post to SerialEventQueue; state mapping, Meta reads, volume re-application and public events all run there.
// • Each session owns its MediaPlayer + Media. Native calls on them (Play, Volume/Mute, Meta) happen under the
//   session's own lock and only while it is not retired; retirement flips `Retired` under that lock, then stops and
//   disposes on a pool thread, so no native call can race the disposal.
//
// ─── State mapping ────────────────────────────────────────────────────────────────────────────────────────────────────
// Opening    MediaPlayer.Opening.
// Buffering  MediaPlayer.Buffering with cache < 100 % before the first audible progress; afterwards only when no
//            TimeChanged arrived for StallThreshold (LibVLC keeps sending buffering updates while audio advances).
// Playing    the first TimeChanged after MediaPlayer.Playing (LibVLC's Playing only means the input started; an HTML
//            page reaches it too), and again on the next TimeChanged after a stall.
// Ended      MediaPlayer.EndReached after audio advanced, followed by Failed(EndOfStream). An EndReached before any
//            audio on a playlist file (.pls/.m3u parsed into sub-items) instead plays its first entry in the same session.
// Failed     MediaPlayer.EncounteredError, or EndReached before any audio advanced. EncounteredError carries no detail,
//            so the kind comes from the latest LibVLC warning/error log line of the session that matches
//            ClassifyLogMessage, read 300 ms after the event because log lines arrive on other threads (else Unknown;
//            for an early EndReached, else UnsupportedFormat). The session is retired at once either way. Also
//            MediaPlayer.Play returning false or LibVLC failing to initialize (Unknown).
// Stopped    raised after StopAsync for the stopped session (allowed by the contract).
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DialShift.Core.Playback;
using LibVLCSharp.Shared;

namespace DialShift.App.Services;

/// <summary>Windows <see cref="IPlaybackEngine"/> on LibVLC, with LibVLC's "now playing" title as <see cref="ITrackMetadataProvider"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class LibVlcPlaybackEngine : IPlaybackEngine, ITrackMetadataProvider
{
    /// <summary>LibVLC instance options (unchanged from the legacy app).</summary>
    internal static readonly string[] LibVlcOptions = ["--no-video", "--no-osd", "--network-caching=1500", "--http-reconnect"];

    /// <summary>While Playing, this long without a TimeChanged event is reported as Buffering (the coordinator's 25 s watchdog decides).</summary>
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LogLineLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogSettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly IAppLog log;
    private readonly SerialEventQueue events;
    private readonly Task<LibVLC> libVlc;
    private readonly Timer watchdog;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock logLock = new();
    private readonly Queue<LogLine> recentLogLines = new();

    // ─── Guarded by `gate` ───
    private readonly Lock gate = new();
    private long sessionCounter;
    private long currentSession;
    private Session? active;
    private readonly List<Task> retiring = [];
    private float desiredVolume = 1f;
    private bool disposeRequested;
    private long lastFailedSession;
    private string? title;

    /// <summary>Creates the engine and starts LibVLC initialization on a pool thread (it can take a moment on first run).</summary>
    public LibVlcPlaybackEngine(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
        events = new SerialEventQueue(log);
        libVlc = Task.Run(() =>
        {
            LibVLCSharp.Shared.Core.Initialize();
            var instance = new LibVLC(LibVlcOptions);
            instance.Log += OnLibVlcLog;
            return instance;
        });
        watchdog = new Timer(static state => ((LibVlcPlaybackEngine)state!).events.Post(((LibVlcPlaybackEngine)state).OnWatchdogTick), this, WatchdogInterval, WatchdogInterval);
    }

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<PlaybackEngineFailedEventArgs>? Failed;

    public event EventHandler? MetadataChanged;

    public string? CurrentTitle
    {
        get { lock (gate) return title; }
    }

    public async Task StartAsync(StreamSource source, double volume, CancellationToken ct)
    {
        long id;
        Session? previous;
        bool titleCleared;
        Task[] pendingRetirements;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            id = ++sessionCounter; // contract: synchronously on entry, before any await or event
            currentSession = ct.IsCancellationRequested ? 0 : id;
            desiredVolume = StreamDiagnostics.ClampVolume(volume);
            previous = active;
            active = null;
            titleCleared = title is not null;
            title = null;
            RetireLocked(previous);
            pendingRetirements = [.. retiring];
        }
        if (titleCleared) events.Post(RaiseMetadataChanged);
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        var origin = StreamDiagnostics.Origin(source.Url);
        if (!StreamDiagnostics.IsPlayable(source.Url))
        {
            ReportFailure(id, PlaybackFailureKind.InvalidUrl, $"libvlc: only absolute http(s) URLs are accepted; source={origin}");
            return;
        }

        LibVLC vlc;
        try
        {
            vlc = await libVlc.WaitAsync(ct).ConfigureAwait(false);
            // Never two audible players: the previous one must be stopped before this one starts.
            await Task.WhenAll(pendingRetirements).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelSession(id);
            throw;
        }
        catch (Exception ex)
        {
            log.Error("playback.engine_error", "LibVLC failed to initialize.", ex);
            ReportFailure(id, PlaybackFailureKind.Unknown, $"libvlc: initialization failed ({ex.GetType().Name}); source={origin}");
            return;
        }

        // Created under `gate`, so disposal (which flags itself under `gate`) can never release LibVLC while a
        // MediaPlayer/Media constructor runs on it. The constructors raise no events.
        Session? session = new(id, origin, Stopwatch.GetTimestamp());
        lock (gate)
        {
            if (disposeRequested || currentSession != id)
            {
                session = null; // superseded while waiting
            }
            else
            {
                try
                {
                    session.Player = new MediaPlayer(vlc);
                    session.Media = new Media(vlc, source.Url, ":no-video");
                    Attach(session);
                    active = session;
                }
                catch (Exception ex)
                {
                    DisposeNative(session);
                    log.Error("playback.engine_error", $"LibVLC player setup failed for session {id}.", ex);
                    ReportFailure(id, PlaybackFailureKind.Unknown, $"libvlc: player setup failed ({ex.GetType().Name}); source={origin}");
                    return;
                }
            }
        }
        if (session is null)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        bool accepted;
        lock (session.Sync)
        {
            // Retirement cannot have disposed it yet: it flips Retired under this same lock first.
            if (session.Retired) return;
            var player = session.Player!;
            player.Volume = ToLibVlcVolume(desiredVolume);
            player.Mute = desiredVolume <= 0f;
            accepted = player.Play(session.Media!);
        }
        if (!accepted)
        {
            events.Post(() =>
            {
                if (!IsCurrent(session)) return;
                EndSession(session);
                ReportFailure(id, PlaybackFailureKind.Unknown, $"libvlc: MediaPlayer.Play refused the media; source={origin}");
            });
        }
        if (ct.IsCancellationRequested)
        {
            CancelSession(id);
            ct.ThrowIfCancellationRequested();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        long stopped;
        bool titleCleared;
        Task[] pending;
        lock (gate)
        {
            if (disposeRequested) return;
            stopped = currentSession;
            currentSession = 0;
            RetireLocked(active);
            active = null;
            titleCleared = title is not null;
            title = null;
            pending = [.. retiring];
        }
        if (titleCleared) events.Post(RaiseMetadataChanged);
        await Task.WhenAll(pending).WaitAsync(ct).ConfigureAwait(false);
        if (stopped != 0) events.Post(() => RaiseStopped(stopped));
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        Session? session;
        float value;
        lock (gate)
        {
            if (disposeRequested) return Task.CompletedTask;
            value = desiredVolume = StreamDiagnostics.ClampVolume(volume);
            session = active;
        }
        if (session is not null) ApplyVolume(session, value);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        bool first;
        lock (gate)
        {
            first = !disposeRequested;
            disposeRequested = true;
            currentSession = 0;
            if (first)
            {
                RetireLocked(active);
                active = null;
                title = null;
            }
        }
        if (first) _ = DisposeCoreAsync();
        return new ValueTask(disposal.Task);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await watchdog.DisposeAsync().ConfigureAwait(false);
            Task[] pending;
            lock (gate) pending = [.. retiring];
            await Task.WhenAll(pending).ConfigureAwait(false);
            if (await libVlc.ContinueWith(static t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default).ConfigureAwait(false) is { } vlc)
            {
                // No player can still be under construction: StartAsync creates players under `gate` only while
                // disposeRequested is false, and DisposeAsync set it under `gate` and retired the active one (awaited above).
                vlc.Log -= OnLibVlcLog;
                vlc.Dispose();
            }
        }
        catch (Exception ex)
        {
            log.Error("playback.engine_error", "LibVLC teardown failed.", ex);
        }
        finally
        {
            disposal.TrySetResult();
        }
    }

    // ─── Sessions ───

    /// <summary>Marks <paramref name="session"/> retired and stops + disposes it on a pool thread. Caller holds `gate`.</summary>
    private void RetireLocked(Session? session)
    {
        retiring.RemoveAll(static t => t.IsCompleted);
        if (session is null) return;
        lock (session.Sync) session.Retired = true;
        retiring.Add(Task.Run(() =>
        {
            Detach(session);
            try { session.Player?.Stop(); } // can block for seconds on network streams: never on the caller's thread
            catch (Exception ex) { log.Warn("playback.engine_error", "LibVLC MediaPlayer.Stop failed.", ex); }
            DisposeNative(session);
        }));
    }

    private void DisposeNative(Session session)
    {
        try
        {
            session.Player?.Dispose();
            session.PlaylistEntry?.Dispose();
            session.Media?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warn("playback.engine_error", "LibVLC player disposal failed.", ex);
        }
    }

    /// <summary>The start's token was cancelled: stop that session silently (no events) if it is still current.</summary>
    private void CancelSession(long id)
    {
        lock (gate)
        {
            if (disposeRequested || currentSession != id) return;
            currentSession = 0;
            if (active?.Id != id) return;
            RetireLocked(active);
            active = null;
        }
    }

    private void Attach(Session session)
    {
        var player = session.Player!;
        session.Handlers = new Handlers(
            (_, _) => events.Post(() => OnOpening(session)),
            (_, e) => { var cache = e.Cache; events.Post(() => OnBuffering(session, cache)); },
            (_, _) => events.Post(() => OnPlaying(session)),
            (_, _) => events.Post(() => OnTimeChanged(session)),
            (_, _) => events.Post(() => OnError(session)),
            (_, _) => events.Post(() => OnEndReached(session)));
        player.Opening += session.Handlers.Opening;
        player.Buffering += session.Handlers.Buffering;
        player.Playing += session.Handlers.Playing;
        player.TimeChanged += session.Handlers.TimeChanged;
        player.EncounteredError += session.Handlers.Error;
        player.EndReached += session.Handlers.EndReached;
    }

    private void Detach(Session session)
    {
        if (session.Handlers is not { } h) return;
        try
        {
            var player = session.Player!;
            player.Opening -= h.Opening;
            player.Buffering -= h.Buffering;
            player.Playing -= h.Playing;
            player.TimeChanged -= h.TimeChanged;
            player.EncounteredError -= h.Error;
            player.EndReached -= h.EndReached;
        }
        catch (Exception ex)
        {
            log.Warn("playback.engine_error", "LibVLC event detach failed.", ex);
        }
    }

    // ─── LibVLC events, mapped on the SerialEventQueue ───

    private bool IsCurrent(Session session)
    {
        lock (gate) return !disposeRequested && currentSession == session.Id && ReferenceEquals(active, session) && !session.Finished;
    }

    private void OnOpening(Session session) => Transition(session, PlaybackEngineState.Opening);

    private void OnBuffering(Session session, float cache)
    {
        // Before the first audible frame this is the initial fill. Afterwards LibVLC keeps emitting buffering updates
        // while audio still advances, so from then on only the missing-progress check reports Buffering.
        if (cache < 100f && !session.EverPlayed) Transition(session, PlaybackEngineState.Buffering);
    }

    private void OnPlaying(Session session)
    {
        // LibVLC's "Playing" means the input started, not that audio advances: Playing is reported on time progress.
        session.InputPlaying = true;
        RefreshTitle(session);
    }

    private void OnTimeChanged(Session session)
    {
        session.LastProgressAt = Stopwatch.GetTimestamp();
        if (!session.InputPlaying || session.Reported == PlaybackEngineState.Playing) return;
        if (!session.EverPlayed)
        {
            session.EverPlayed = true;
            float volume;
            lock (gate) volume = desiredVolume;
            ApplyVolume(session, volume); // re-applied when a session first becomes audible, like the legacy app
        }
        Transition(session, PlaybackEngineState.Playing);
    }

    /// <summary>Once a second: missing progress while Playing → Buffering, and the now-playing title poll.</summary>
    private void OnWatchdogTick()
    {
        Session? session;
        lock (gate) session = active;
        if (session is null || session.Finished) return;
        if (session.Reported == PlaybackEngineState.Playing && session.LastProgressAt is { } last && Stopwatch.GetElapsedTime(last) >= StallThreshold)
            Transition(session, PlaybackEngineState.Buffering);
        if (session.InputPlaying) RefreshTitle(session);
    }

    private void OnError(Session session)
    {
        if (!IsCurrent(session)) return;
        EndSession(session);
        ReportAfterLogSettles(session, PlaybackFailureKind.Unknown, "EncounteredError");
    }

    private void OnEndReached(Session session)
    {
        if (!IsCurrent(session)) return;
        if (!session.EverPlayed)
        {
            if (TryPlayPlaylistEntry(session)) return;
            // Ended before any audio advanced (an HTML page, a body LibVLC could not demux): not an end of stream.
            EndSession(session);
            ReportAfterLogSettles(session, PlaybackFailureKind.UnsupportedFormat, "EndReached before any audio");
            return;
        }
        Transition(session, PlaybackEngineState.Ended);
        EndSession(session);
        ReportFailure(session.Id, PlaybackFailureKind.EndOfStream, $"libvlc: EndReached; source={session.Origin}");
    }

    /// <summary>
    /// A .pls/.m3u playlist file "ends" at once with its entries parsed as sub-items; a bare MediaPlayer does not advance
    /// into them. Plays the first entry within the same session (once), which is what AVPlayer does natively.
    /// </summary>
    private bool TryPlayPlaylistEntry(Session session)
    {
        if (session.PlaylistEntry is not null) return false;
        lock (session.Sync)
        {
            if (session.Retired) return false;
            using var entries = session.Media!.SubItems;
            if (entries.Count == 0 || entries[0] is not { } entry) return false;
            session.PlaylistEntry = entry; // disposed with the session
            session.InputPlaying = false;
            return session.Player!.Play(entry);
        }
    }

    /// <summary>A failed or ended session produces no audio: it is retired now instead of waiting for the coordinator's Stop.</summary>
    private void EndSession(Session session)
    {
        session.Finished = true;
        lock (gate)
        {
            if (!ReferenceEquals(active, session)) return;
            active = null;
            RetireLocked(session);
        }
    }

    /// <summary>
    /// LibVLC delivers log lines on its own threads, sometimes after the event that reports the error, so the failure
    /// is classified after a short settle delay (the session is already retired and silent meanwhile).
    /// </summary>
    private void ReportAfterLogSettles(Session session, PlaybackFailureKind fallback, string what) =>
        _ = Task.Delay(LogSettleDelay).ContinueWith(_ =>
        {
            var (kind, detail) = ClassifyRecentLog(session.StartedAt);
            ReportFailure(session.Id, kind ?? fallback, $"libvlc: {what}; {detail}; source={session.Origin}");
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private void RefreshTitle(Session session)
    {
        if (!IsCurrent(session)) return;
        string? value;
        lock (session.Sync)
        {
            if (session.Retired) return;
            value = (session.PlaylistEntry ?? session.Media!).Meta(MetadataType.NowPlaying);
        }
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        lock (gate)
        {
            if (currentSession != session.Id || title == value) return;
            title = value;
        }
        RaiseMetadataChanged();
    }

    private void ApplyVolume(Session session, float volume)
    {
        lock (session.Sync)
        {
            if (session.Retired || session.Player is not { } player) return;
            player.Volume = ToLibVlcVolume(volume);
            player.Mute = volume <= 0f;
        }
    }

    private static int ToLibVlcVolume(float volume) => (int)Math.Round(volume * 100f);

    // ─── Events (SerialEventQueue: thread pool, in order, never under `gate`) ───

    private void Transition(Session session, PlaybackEngineState state)
    {
        if (session.Reported == state || session.Finished && state != PlaybackEngineState.Ended) return;
        session.Reported = state;
        lock (gate)
            if (disposeRequested || currentSession != session.Id) return;
        StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(session.Id, state));
    }

    private void ReportFailure(long id, PlaybackFailureKind kind, string diagnostic) => events.Post(() =>
    {
        lock (gate)
        {
            if (disposeRequested || currentSession != id || lastFailedSession >= id) return;
            lastFailedSession = id; // at most one failure per session
        }
        Failed?.Invoke(this, new PlaybackEngineFailedEventArgs(id, kind, StreamDiagnostics.Redact(diagnostic, maxLength: 400)));
    });

    private void RaiseStopped(long id)
    {
        lock (gate)
            if (disposeRequested || currentSession != 0 || sessionCounter != id) return; // a newer Start supersedes it
        StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(id, PlaybackEngineState.Stopped));
    }

    private void RaiseMetadataChanged()
    {
        lock (gate)
            if (disposeRequested) return;
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─── Failure classification from LibVLC's log (EncounteredError carries no detail) ───

    /// <summary>Keeps the last warning/error lines (redacted, with their classification); cheap on LibVLC's threads.</summary>
    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        if (e.Level < LogLevel.Warning) return;
        var line = new LogLine(Stopwatch.GetTimestamp(), ClassifyLogMessage(e.Message), e.Module ?? "?", StreamDiagnostics.Redact(e.Message, maxLength: 160));
        lock (logLock)
        {
            recentLogLines.Enqueue(line);
            while (recentLogLines.Count > 32) recentLogLines.Dequeue();
        }
    }

    /// <summary>The latest classified line logged since the session started, else the latest line (kind null).</summary>
    private (PlaybackFailureKind? Kind, string Detail) ClassifyRecentLog(long sessionStartedAt)
    {
        lock (logLock)
        {
            var lines = recentLogLines.Where(l => l.At >= sessionStartedAt && Stopwatch.GetElapsedTime(l.At) <= LogLineLifetime).ToList();
            var line = lines.LastOrDefault(l => l.Kind is not null) ?? lines.LastOrDefault();
            return line is null ? (null, "no LibVLC warning or error logged") : (line.Kind, $"{line.Module}: {line.Message}");
        }
    }

    /// <summary>Keyword mapping of LibVLC 3 access/demux/TLS messages onto <see cref="PlaybackFailureKind"/>; null when unrecognized.</summary>
    internal static PlaybackFailureKind? ClassifyLogMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        if (HttpStatusPattern().IsMatch(message)) return PlaybackFailureKind.HttpError;
        if (Contains(message, "TLS") || Contains(message, "certificate") || Contains(message, "gnutls") || Contains(message, "handshake"))
            return PlaybackFailureKind.TlsFailure;
        if (Contains(message, "cannot resolve") || Contains(message, "unknown host") || Contains(message, "No such host") || Contains(message, "Name or service not known")
            || Contains(message, "connection refused") || Contains(message, "actively refused") || Contains(message, "unreachable") || Contains(message, "No route to host")
            || Contains(message, "timed out") || Contains(message, "cannot connect") || Contains(message, "connection failed") || Contains(message, "getaddrinfo"))
            return PlaybackFailureKind.NetworkUnavailable;
        if (Contains(message, "no suitable access module") || Contains(message, "unknown protocol"))
            return PlaybackFailureKind.InvalidUrl;
        if (Contains(message, "no suitable demux") || Contains(message, "could not identify") || Contains(message, "no suitable decoder")
            || Contains(message, "is not supported") || Contains(message, "unsupported"))
            return PlaybackFailureKind.UnsupportedFormat;
        return null;

        static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\bHTTP\b\D{0,16}\b[45]\d\d\b", RegexOptions.IgnoreCase)]
    private static partial Regex HttpStatusPattern();

    private sealed record LogLine(long At, PlaybackFailureKind? Kind, string Module, string Message);

    private sealed record Handlers(
        EventHandler<EventArgs> Opening,
        EventHandler<MediaPlayerBufferingEventArgs> Buffering,
        EventHandler<EventArgs> Playing,
        EventHandler<MediaPlayerTimeChangedEventArgs> TimeChanged,
        EventHandler<EventArgs> Error,
        EventHandler<EventArgs> EndReached);

    /// <summary>One LibVLC player session. Fields other than Retired are touched only on the SerialEventQueue.</summary>
    private sealed class Session(long id, string origin, long startedAt)
    {
        public long Id { get; } = id;
        public string Origin { get; } = origin;
        public long StartedAt { get; } = startedAt;
        public Lock Sync { get; } = new();
        public MediaPlayer? Player { get; set; }
        public Media? Media { get; set; }
        public Media? PlaylistEntry { get; set; }
        public Handlers? Handlers { get; set; }
        public bool Retired { get; set; }
        public bool InputPlaying { get; set; }
        public bool EverPlayed { get; set; }
        public bool Finished { get; set; }
        public long? LastProgressAt { get; set; }
        public PlaybackEngineState Reported { get; set; } = PlaybackEngineState.Idle;
    }
}
