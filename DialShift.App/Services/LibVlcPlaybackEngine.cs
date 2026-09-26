// LibVlcPlaybackEngine: IPlaybackEngine on LibVLC 3 via LibVLCSharp 3.10.1 (brief 1 §7.8). Windows only at runtime
// (the VideoLAN.LibVLC.Windows native package ships only in the win-x64 artifact); it compiles in every build.
//
// Carried over from the pre-refactor playback controller: LibVLC is created off the UI thread at construction with
// "--no-video --no-osd --network-caching=1500 --http-reconnect" (plus "--aout=<module>" when LibVlcEngineOptions names an
// audio output: the engine tests, CI and smoke runs use "adummy" because hosted runners have no audio device); media gets
// ":no-video"; one MediaPlayer per session;
// Mute when the volume is 0; "now playing" from Media.Meta(NowPlaying), polled once a second while the input plays (as
// the legacy tick did); the previous player is stopped and disposed on a pool thread, and the next player is created
// only after that finished (never two audible players).
// Media.MetaChanged is not subscribed: the poll alone delivered ICY titles in the harness, and it keeps one fewer
// native callback path alive across rapid session churn. ICY titles come only through LibVLC 3's legacy HTTP access
// module, the only one that requests Icy-MetaData. LibVLC opens http:// and https:// with its newer module and falls back
// to the legacy one when a server answers with the Shoutcast v1 status line "ICY 200 OK". Measured with LibVLC 3.0.4
// against the engine tests' local server: an "HTTP/1.0 200" stream was requested without Icy-MetaData, while "ICY 200 OK"
// was retried with it and delivered the title. Stations without a title show the station tag instead.
//
// ─── Credentials ──────────────────────────────────────────────────────────────────────────────────────────────────────
// LibVLC sends a URL's user-info as HTTP Basic credentials, and keeps them for the life of the LibVLC instance, which is
// this engine's and so the app's lifetime. It then sends them with other requests to the same scheme://host:port and path,
// even when the station's URL carries none, and even before the server asks: in Windows CI (VideoLAN.LibVLC.Windows
// 3.0.23.1, HS-17 LV-08) a station without credentials on the user-info station's path played, and its first and only
// request carried the Authorization header. No request to any other path on that server carried them, including a
// second password-protected path with another realm. A password removed from a station URL therefore keeps working until the app quits. This matches how
// browsers treat a Basic-auth protection space, and it stays within one server and path, so it is accepted and
// documented in the README. LibVLC 3 has no option that disables or scopes this store (its only keystore option,
// --keystore, selects the persistent store), and a LibVLC instance per session would repeat the plugin load on every
// station change. LibVLC 3.0.4 (macOS x64, Rosetta) also sent user-info preemptively but did not reuse it. AVPlayer
// (macOS 26) reuses user-info credentials only after a 401 from the same server and realm, for the process lifetime.
//
// ─── TLS trust warm-up (D100) ─────────────────────────────────────────────────────────────────────────────────────────
// LibVLC's GnuTLS validates https certificates against the roots already in the Windows store. Windows ships with part
// of its trusted roots and downloads a missing one only while a CryptoAPI chain build (SChannel, .NET) asks for it, which
// GnuTLS never does. On a Windows 11 lacking such a root, every station whose chain ends at it failed with "TLS session
// handshake error" (SomaFM's USERTrust RSA root; ~8,960 of the catalog's ~13,250 URLs are https). So StartAsync first has
// the IStreamTrustWarmup (WindowsTrustWarmup: one .NET request, redirects followed) connect to an https source, which
// makes Windows add the root, and creates the Media only afterwards. The warm-up runs on the pool (never on the caller's
// thread, and bounded even while it blocks before returning its task), alongside the LibVLC init and retirement waits.
// TrustWarmupLimit (6 s) backs up the warm-up's own 5 s, and a warm-up never fails a start: whatever happens, LibVLC
// then connects and reports its own failure as before. Cancellation during it is the same as during those waits, and a
// session superseded or disposed during it never creates a player. The first entry of a .pls/.m3u playlist is warmed
// too when it is https (it may be on another host), before it is played. http:// URLs are never warmed. Not covered:
// hosts that only an HLS playlist names (its segment and variant URLs are fetched inside LibVLC).
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
//            audio on a playlist file (.pls/.m3u parsed into sub-items) instead plays its first entry in the same session
//            (an https entry after its warm-up).
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

    /// <summary>How long a start waits for the TLS trust warm-up at most: its own bound plus a margin, for a warm-up that overruns it.</summary>
    internal static readonly TimeSpan TrustWarmupLimit = WindowsTrustWarmup.Timeout + TimeSpan.FromSeconds(1);

    private readonly IAppLog log;
    private readonly IStreamTrustWarmup trustWarmup;
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
    /// <param name="log">Receives engine errors; diagnostics are redacted.</param>
    /// <param name="options">LibVLC instance settings: <see cref="LibVlcEngineOptions.Default"/> in the app.</param>
    /// <param name="trustWarmup">Runs before LibVLC opens an https source (D100): <see cref="WindowsTrustWarmup"/> in the app. Owned: disposed with the engine.</param>
    public LibVlcPlaybackEngine(IAppLog log, LibVlcEngineOptions options, IStreamTrustWarmup trustWarmup)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trustWarmup);
        this.log = log;
        this.trustWarmup = trustWarmup;
        events = new SerialEventQueue(log);
        string[] instanceOptions = options.AudioOutput is { } aout ? [.. LibVlcOptions, "--aout=" + aout] : LibVlcOptions;
        libVlc = Task.Run(() =>
        {
            LibVLCSharp.Shared.Core.Initialize();
            var instance = new LibVLC(instanceOptions);
            instance.Log += OnLibVlcLog;
            return instance;
        });
        watchdog = new Timer(static state => ((LibVlcPlaybackEngine)state!).events.Post(((LibVlcPlaybackEngine)state).OnWatchdogTick), this, WatchdogInterval, WatchdogInterval);
    }

    /// <summary>The warm-up this engine owns. Internal as a test seam only: HS-17 LV-01 checks the factory's wiring with it.</summary>
    internal IStreamTrustWarmup TrustWarmup => trustWarmup;

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
            desiredVolume = EngineInput.ClampVolume(volume);
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
        var origin = StreamUrlRedactor.RedactUrl(source.Url);
        if (!EngineInput.IsPlayable(source.Url))
        {
            ReportFailure(id, PlaybackFailureKind.InvalidUrl, $"libvlc: only absolute http(s) URLs are accepted; source={origin}");
            return;
        }

        // D100: Windows fetches any missing root of an https stream's chain before LibVLC connects; alongside the waits below.
        var trustWarmed = IsHttps(source.Url) ? WarmTrustAsync(source.Url, origin, ct) : Task.CompletedTask;
        LibVLC vlc;
        try
        {
            vlc = await libVlc.WaitAsync(ct).ConfigureAwait(false);
            // Never two audible players: the previous one must be stopped before this one starts.
            await Task.WhenAll(pendingRetirements).WaitAsync(ct).ConfigureAwait(false);
            await trustWarmed.ConfigureAwait(false); // throws only OperationCanceledException for ct
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
            value = desiredVolume = EngineInput.ClampVolume(volume);
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
            trustWarmup.Dispose(); // cancels a warm-up in flight; its start then sees disposeRequested and creates no player
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

    /// <summary>
    /// The D100 warm-up of an https URL, bounded by <see cref="TrustWarmupLimit"/> and by <paramref name="ct"/>. It runs
    /// on the pool, so the bound also covers a warm-up that blocks before it returns its task (starting a request looks
    /// up the system proxy synchronously), and the caller, possibly the UI thread, never runs any of it. Throws only
    /// <see cref="OperationCanceledException"/> for <paramref name="ct"/>; anything else is logged and playback goes on.
    /// </summary>
    private async Task WarmTrustAsync(Uri url, string origin, CancellationToken ct)
    {
        var warming = Task.Run(() => trustWarmup.WarmAsync(url, ct), CancellationToken.None);
        // A warm-up left behind by the bound or the token may still fault later: observed here, so it never surfaces as unobserved.
        _ = warming.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            await warming.WaitAsync(TrustWarmupLimit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The warm-up logs its own failures and never throws; this is the backstop for one that throws or overruns.
            log.Warn(WindowsTrustWarmup.LogEvent, $"TLS trust warm-up did not finish ({ex.GetType().Name}); LibVLC connects anyway; source={origin}");
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
    /// into them. Plays the first entry within the same session (once), which is what AVPlayer does natively. An https
    /// entry, whatever the playlist's own scheme, is warmed first (D100): off the event queue, then played from it if the
    /// session is still current; true is returned at once, and a refused Play then fails the session as it would here.
    /// </summary>
    private bool TryPlayPlaylistEntry(Session session)
    {
        if (session.PlaylistEntry is not null) return false;
        Media entry;
        Uri? httpsEntry;
        lock (session.Sync)
        {
            if (session.Retired) return false;
            using var entries = session.Media!.SubItems;
            if (entries.Count == 0 || entries[0] is not { } first) return false;
            entry = session.PlaylistEntry = first; // disposed with the session
            session.InputPlaying = false;
            httpsEntry = Uri.TryCreate(first.Mrl, UriKind.Absolute, out var url) && IsHttps(url) ? url : null;
            if (httpsEntry is null) return session.Player!.Play(entry);
        }
        _ = PlayEntryAfterTrustWarmupAsync(session, entry, httpsEntry);
        return true;
    }

    private async Task PlayEntryAfterTrustWarmupAsync(Session session, Media entry, Uri url)
    {
        await WarmTrustAsync(url, StreamUrlRedactor.RedactUrl(url), CancellationToken.None).ConfigureAwait(false);
        events.Post(() =>
        {
            if (!IsCurrent(session)) return; // stopped, superseded or disposed meanwhile
            bool played;
            lock (session.Sync)
            {
                if (session.Retired) return;
                played = session.Player!.Play(entry);
            }
            if (played) return;
            EndSession(session);
            ReportAfterLogSettles(session, PlaybackFailureKind.UnsupportedFormat, "EndReached before any audio");
        });
    }

    private static bool IsHttps(Uri url) => url.Scheme == Uri.UriSchemeHttps;

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
        Failed?.Invoke(this, new PlaybackEngineFailedEventArgs(id, kind, StreamUrlRedactor.RedactDiagnostic(diagnostic, maxLength: 400)));
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
        var line = new LogLine(Stopwatch.GetTimestamp(), ClassifyLogMessage(e.Message), e.Module ?? "?", StreamUrlRedactor.RedactDiagnostic(e.Message, maxLength: 160));
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

/// <summary>LibVLC instance settings that differ between the app and test or smoke runs.</summary>
/// <remarks>
/// <see cref="AudioOutput"/> names a LibVLC audio output module, passed as <c>--aout=&lt;module&gt;</c>; null keeps LibVLC's
/// default (the system output device). <see cref="Dummy"/> selects <c>adummy</c>, which discards decoded audio while
/// playback still advances, so the engine tests, CI and smoke runs work on machines without an audio device. The app
/// selects it only through <see cref="PlaybackEngineOptions"/> (<c>DIALSHIFT_AUDIO_OUTPUT=dummy</c>).
/// </remarks>
public sealed partial record LibVlcEngineOptions
{
    /// <summary>LibVLC's default audio output: what the app uses.</summary>
    public static LibVlcEngineOptions Default { get; } = new();

    /// <summary>LibVLC's <c>adummy</c> audio output: no audio device needed, nothing is audible.</summary>
    public static LibVlcEngineOptions Dummy { get; } = new() { AudioOutput = "adummy" };

    /// <summary>LibVLC audio output module name (letters, digits, '_' or '-'), or null for LibVLC's default.</summary>
    /// <exception cref="ArgumentException">Anything else: the name becomes a LibVLC command-line option.</exception>
    public string? AudioOutput
    {
        get;
        init => field = value is null || ModuleNamePattern().IsMatch(value)
            ? value
            : throw new ArgumentException($"'{value}' is not a LibVLC audio output module name.", nameof(AudioOutput));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ModuleNamePattern();
}
