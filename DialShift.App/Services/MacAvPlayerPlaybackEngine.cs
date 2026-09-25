// MacAvPlayerPlaybackEngine: IPlaybackEngine on AVFoundation's AVPlayer via raw Objective-C P/Invoke (brief 1 §4.3, §7.8;
// design and measurements in docs/spikes.md "Production guidance: MacAvPlayerPlaybackEngine").
//
// ─── Threading ────────────────────────────────────────────────────────────────────────────────────────────────────────
// • Every AVFoundation call runs on the process main thread, posted with dispatch_async_f to the main queue
//   (Interop/MacMainQueue), one FIFO stream of work items, each inside an autorelease pool. The host must service the
//   main run loop; Avalonia does ([NSApp run]). Nothing here references Avalonia.
// • Public members may be called from any thread and never block on the main thread. They update a small "request"
//   (guarded by `gate`) and post Reconcile(), which moves the native side to the latest request. Because Reconcile reads
//   the request when it runs, overlapping Start/Stop calls and out-of-date work items converge on the newest request:
//   a superseded session is simply never built.
// • State is observed by a 250 ms poll (System.Threading.Timer → one queued main-thread Poll at a time) plus three
//   NSNotificationCenter notifications (did-play-to-end, failed-to-play-to-end, playback-stalled).
// • Events are raised by SerialEventQueue on the thread pool, in order, outside `gate`, and only while their session is
//   still the current one (so nothing is raised for a session after a newer Start/Stop/Dispose).
//
// ─── Objective-C ownership (per object) ───────────────────────────────────────────────────────────────────────────────
// • NSString (URL text): +1 from alloc/initWithUTF8String:, released right after NSURL init.
// • NSURL: +1 from alloc/initWithString: (nil for unparseable text: init already freed the alloc, nothing to release);
//   released right after AVURLAsset retains it.
// • AVURLAsset: +1 from alloc/initWithURL:options:, released right after AVPlayerItem retains it.
// • AVPlayerItem: +1 held in `item` for exactly one session. The player retains it on replaceCurrentItemWithPlayerItem:;
//   our +1 is released when the item is replaced (new session), dropped (stop, failure, end of stream) or at dispose.
// • AVPlayer: +1 held in `player` for the engine lifetime (created with the first session; recreated only if AVPlayer
//   itself reports status Failed), released in TeardownAll after its item is cleared.
// • Observer: one NotificationObserver instance (+1) per engine, registered once when the player is first created,
//   removed from the notification center before it is released in TeardownAll.
// • Every other object (currentItem, error, userInfo, errorLog, notification name/object, strings) is +0: never
//   released and only read inside the work item's autorelease pool.
//
// ─── State mapping ────────────────────────────────────────────────────────────────────────────────────────────────────
// Opening   after replaceCurrentItemWithPlayerItem: + play, until the NEW item is current and ReadyToPlay.
// Buffering item ready but not audibly advancing: timeControlStatus Waiting, Playing without currentTime progress in
//           the last 2 s, or a playback-stalled notification.
// Playing   currentItem == our item && item ReadyToPlay && timeControlStatus Playing && currentTime advanced within
//           the last 2 s (spike: right after a replace the player still reads Playing for the OLD item).
// Ended     did-play-to-end notification, or an unrequested pause (2 consecutive polls) after having played; always
//           followed by Failed(EndOfStream).
// Failed    item/player status Failed or failed-to-play-to-end, NSError chain mapped by ClassifyNativeError.
// Stopped   raised after StopAsync for the stopped session (allowed by the contract).
// Hangs (server never answers, HTML over HTTP/1.0) produce no native error at all; the coordinator's watchdog owns them.
//
// ─── Capability differences vs LibVLC (documented in docs/spikes.md) ─────────────────────────────────────────────────
// No ITrackMetadataProvider: AVPlayerItemMetadataOutput delivered icy/StreamTitle for a Shoutcast v2 server only, and
// nothing for Icecast MP3/AAC or HLS streams, so titles are not offered on macOS. Formats are OS-version dependent;
// .pls/.m3u-named raw streams fail; http:// needs NSAllowsArbitraryLoadsForMedia in the .app; TLS errors are strict.
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DialShift.App.Services.Interop;
using DialShift.Core.Playback;
using Sel = DialShift.App.Services.Interop.AVFoundation.Sel;

namespace DialShift.App.Services;

/// <summary>macOS <see cref="IPlaybackEngine"/> on AVPlayer. See the source header for threading, ownership and state mapping.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacAvPlayerPlaybackEngine : IPlaybackEngine
{
    /// <summary>How often the main thread samples AVPlayer state while a session is live.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Playing requires currentTime to have advanced within this window.</summary>
    internal static readonly TimeSpan ProgressWindow = TimeSpan.FromSeconds(2);

    /// <summary>Bound on waiting for the main thread during disposal (a blocked main thread must not hang shutdown).</summary>
    internal static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);

    private const int PausedPollsForEndOfStream = 2;

    private readonly IAppLog log;
    private readonly SerialEventQueue events;
    private readonly Timer pollTimer;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ─── Request state: any thread, guarded by `gate` ───
    private readonly Lock gate = new();
    private long sessionCounter;
    private long currentSession;
    private Request? request;
    private float desiredVolume = 1f;
    private bool disposeRequested;
    private bool nativeUsed;
    private long lastFailedSession;
    private int pollQueued;

    // ─── Native state: main thread only ───
    private nint player;
    private NotificationObserver? observer;
    private nint item;
    private long itemSession;
    private string itemOrigin = "";
    private long builtSession;
    private ItemProgress progress = new();
    private float appliedVolume = float.NaN;
    private bool nativeClosed;

    /// <summary>Item + session the notification callback compares against; read on the posting thread.</summary>
    private volatile NoteTarget? noteTarget;

    private static readonly string DidPlayToEndName = ObjCRuntime.ToManagedString(AVFoundation.DidPlayToEndTimeNotification) ?? "";
    private static readonly string FailedToPlayToEndName = ObjCRuntime.ToManagedString(AVFoundation.FailedToPlayToEndTimeNotification) ?? "";
    private static readonly string StalledName = ObjCRuntime.ToManagedString(AVFoundation.PlaybackStalledNotification) ?? "";

    /// <summary>Creates the engine. No native object exists until the first <see cref="StartAsync"/>.</summary>
    /// <exception cref="DllNotFoundException">AVFoundation could not be loaded.</exception>
    public MacAvPlayerPlaybackEngine(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
        AVFoundation.EnsureLoaded();
        events = new SerialEventQueue(log);
        pollTimer = new Timer(static state => ((MacAvPlayerPlaybackEngine)state!).QueuePoll(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<PlaybackEngineFailedEventArgs>? Failed;

    public async Task StartAsync(StreamSource source, double volume, CancellationToken ct)
    {
        long session;
        bool playable;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            session = ++sessionCounter; // contract: synchronously on entry, before any await or event
            playable = StreamDiagnostics.IsPlayable(source?.Url);
            desiredVolume = StreamDiagnostics.ClampVolume(volume);
            var cancelled = ct.IsCancellationRequested;
            currentSession = cancelled ? 0 : session;
            request = playable && !cancelled ? new Request(session, source!.Url, StreamDiagnostics.Origin(source.Url)) : null;
            nativeUsed = true;
        }
        // Stops the previous session and (when playable) builds this one.
        var applied = MacMainQueue.InvokeAsync(Reconcile);
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        if (!playable)
        {
            ReportFailure(session, PlaybackFailureKind.InvalidUrl, $"avplayer: only absolute http(s) URLs are accepted; source={StreamDiagnostics.Origin(source.Url)}");
            return;
        }
        using (ct.Register(static state => ((CancelledStart)state!).Cancel(), new CancelledStart(this, session)))
            await applied.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        long stopped;
        lock (gate)
        {
            if (disposeRequested) return;
            stopped = currentSession;
            currentSession = 0;
            request = null;
            if (!nativeUsed) return;
        }
        await MacMainQueue.InvokeAsync(Reconcile).WaitAsync(ct).ConfigureAwait(false);
        if (stopped != 0) events.Post(() => RaiseStopped(stopped));
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        lock (gate)
        {
            if (disposeRequested) return Task.CompletedTask;
            desiredVolume = StreamDiagnostics.ClampVolume(volume);
            if (request is null) return Task.CompletedTask; // remembered for the next session
        }
        return MacMainQueue.InvokeAsync(Reconcile).WaitAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        bool first, used;
        lock (gate)
        {
            first = !disposeRequested;
            disposeRequested = true;
            currentSession = 0;
            request = null;
            used = nativeUsed;
        }
        if (first) _ = DisposeCoreAsync(used);
        return new ValueTask(disposal.Task);
    }

    private async Task DisposeCoreAsync(bool nativeWasUsed)
    {
        try
        {
            if (!nativeWasUsed) return;
            if (MacMainQueue.IsMainThread)
            {
                // Inline, so a host that blocks the main thread on disposal cannot deadlock. Work items still queued
                // behind this see nativeClosed and do nothing.
                MacMainQueue.RunInline(TeardownAll);
                return;
            }
            var teardown = MacMainQueue.InvokeAsync(TeardownAll);
            if (await Task.WhenAny(teardown, Task.Delay(DisposeTimeout)).ConfigureAwait(false) != teardown)
            {
                log.Warn("playback.engine_dispose_timeout", $"The main thread did not run the AVPlayer teardown within {DisposeTimeout.TotalSeconds:0} s; native objects are left to process exit.");
                return;
            }
            await teardown.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error("playback.engine_error", "AVPlayer teardown failed.", ex);
        }
        finally
        {
            pollTimer.Dispose();
            disposal.TrySetResult();
        }
    }

    // ─── Main thread ───

    /// <summary>Moves the native side to the latest request (idempotent; safe to run any number of times, in any order).</summary>
    private void Reconcile()
    {
        Request? want;
        float volume;
        lock (gate)
        {
            if (disposeRequested) return; // the teardown work item owns the native objects now
            want = request;
            volume = desiredVolume;
        }
        if (nativeClosed) return;
        if (want is null)
        {
            DropItem();
            return;
        }
        if (want.Session > builtSession)
        {
            builtSession = want.Session;
            try
            {
                Build(want, volume);
            }
            catch (Exception ex)
            {
                DropItem();
                log.Error("playback.engine_error", $"AVPlayer setup failed for session {want.Session}.", ex);
                ReportFailure(want.Session, PlaybackFailureKind.Unknown, $"avplayer: native setup failed ({ex.GetType().Name}); source={want.Origin}");
            }
            return;
        }
        ApplyVolume(volume, force: false);
    }

    private void Build(Request want, float volume)
    {
        EnsurePlayer();
        var text = ObjCRuntime.CreateNSString(want.Url.AbsoluteUri);                                               // +1
        var url = ObjCRuntime.SendId(ObjCRuntime.Alloc(AVFoundation.NSURLClass), Sel.InitWithString, text);          // +1 or nil
        ObjCRuntime.Release(text);
        if (url == 0)
        {
            DropItem();
            ReportFailure(want.Session, PlaybackFailureKind.InvalidUrl, $"avplayer: NSURL rejected the URL; source={want.Origin}");
            return;
        }
        var asset = ObjCRuntime.SendId(ObjCRuntime.Alloc(AVFoundation.AVURLAssetClass), Sel.InitWithURLOptions, url, 0); // +1, retains url
        ObjCRuntime.Release(url);
        if (asset == 0)
        {
            DropItem();
            ReportFailure(want.Session, PlaybackFailureKind.InvalidUrl, $"avplayer: AVURLAsset rejected the URL; source={want.Origin}");
            return;
        }
        var newItem = ObjCRuntime.SendId(ObjCRuntime.Alloc(AVFoundation.AVPlayerItemClass), Sel.InitWithAsset, asset); // +1, retains asset
        ObjCRuntime.Release(asset);
        if (newItem == 0)
        {
            DropItem();
            ReportFailure(want.Session, PlaybackFailureKind.Unknown, $"avplayer: AVPlayerItem init returned nil; source={want.Origin}");
            return;
        }
        ObjCRuntime.SendVoid(player, Sel.ReplaceCurrentItem, newItem); // the player retains newItem and releases the old one
        var previous = item;
        item = newItem;
        itemSession = want.Session;
        itemOrigin = want.Origin;
        progress = new ItemProgress();
        noteTarget = new NoteTarget(newItem, want.Session);
        ObjCRuntime.Release(previous); // our +1 on the replaced item (no-op for nil)
        ApplyVolume(volume, force: true); // re-applied after every replace (spike: one-off volume drift)
        ObjCRuntime.SendVoid(player, Sel.Play);
        Report(want.Session, PlaybackEngineState.Opening);
        SetPolling(true);
    }

    private void EnsurePlayer()
    {
        if (player != 0 && ObjCRuntime.SendNInt(player, Sel.Status) != AVStatus.Failed) return;
        if (player != 0)
        {
            // A failed AVPlayer cannot be reused. Our +1 on `item` stays valid; Build releases it after the replace.
            log.Warn("playback.engine_recreated", "AVPlayer reported status Failed; creating a new player.");
            ObjCRuntime.SendVoid(player, Sel.ReplaceCurrentItem, 0);
            ObjCRuntime.Release(player);
            player = 0;
        }
        player = ObjCRuntime.AllocInit(AVFoundation.AVPlayerClass); // +1, released in TeardownAll
        if (player == 0) throw new InvalidOperationException("AVPlayer init returned nil.");
        appliedVolume = float.NaN;
        if (observer is not null) return;
        observer = NotificationObserver.Create(OnNotification); // once per engine
        observer.Observe(AVFoundation.DidPlayToEndTimeNotification);
        observer.Observe(AVFoundation.FailedToPlayToEndTimeNotification);
        observer.Observe(AVFoundation.PlaybackStalledNotification);
    }

    /// <summary>Stops audio and closes the connection: pause, clear the player's item, release ours.</summary>
    private void DropItem()
    {
        if (item == 0) return;
        SetPolling(false);
        noteTarget = null;
        ObjCRuntime.SendVoid(player, Sel.Pause);
        ObjCRuntime.SendVoid(player, Sel.ReplaceCurrentItem, 0);
        ObjCRuntime.Release(item);
        item = 0;
        itemSession = 0;
    }

    private void TeardownAll()
    {
        if (nativeClosed) return;
        nativeClosed = true;
        if (player != 0) DropItem();
        observer?.Dispose(); // removeObserver: before release
        observer = null;
        ObjCRuntime.Release(player);
        player = 0;
    }

    private void ApplyVolume(float volume, bool force)
    {
        if (player == 0 || (!force && volume == appliedVolume)) return;
        ObjCRuntime.SendVoidFloat(player, Sel.SetVolume, volume);
        ObjCRuntime.SendVoidBool(player, Sel.SetMuted, volume <= 0f ? (byte)1 : (byte)0);
        appliedVolume = volume;
    }

    private void Poll()
    {
        Volatile.Write(ref pollQueued, 0);
        if (nativeClosed || item == 0 || player == 0 || progress.Finished) return;
        var session = itemSession;
        var p = progress;
        if (ObjCRuntime.SendNInt(player, Sel.Status) == AVStatus.Failed)
        {
            FailFromNative(session, ObjCRuntime.SendId(player, Sel.Error), "player");
            return;
        }
        if (ObjCRuntime.SendId(player, Sel.CurrentItem) != item) return; // the replace is not effective yet: still Opening
        var itemStatus = ObjCRuntime.SendNInt(item, Sel.Status);
        if (itemStatus == AVStatus.Failed)
        {
            FailFromNative(session, ObjCRuntime.SendId(item, Sel.Error), "item");
            return;
        }
        if (itemStatus != AVStatus.ReadyToPlay) return;

        var now = Stopwatch.GetTimestamp();
        var time = ObjCRuntime.SendCMTime(item, Sel.CurrentTime);
        if (time.IsValid)
        {
            var seconds = AVFoundation.CMTimeGetSeconds(time);
            if (!double.IsNaN(p.LastSeconds) && seconds > p.LastSeconds + 0.001) p.LastAdvanceAt = now;
            p.LastSeconds = seconds;
        }
        var control = ObjCRuntime.SendNInt(player, Sel.TimeControlStatus);
        var advancing = p.LastAdvanceAt is { } at && Stopwatch.GetElapsedTime(at, now) <= ProgressWindow;

        PlaybackEngineState state;
        if (control == AVStatus.Paused && p.EverPlayed)
        {
            // Nobody paused this item (Stop drops it instead): the server ended the stream.
            if (++p.PausedPolls >= PausedPollsForEndOfStream)
            {
                EndOfStream(session, "avplayer: playback paused itself after playing (stream closed)");
                return;
            }
            state = p.Reported;
        }
        else
        {
            p.PausedPolls = 0;
            state = control == AVStatus.Playing && advancing ? PlaybackEngineState.Playing : PlaybackEngineState.Buffering;
        }

        if (state == PlaybackEngineState.Playing && p.Reported != PlaybackEngineState.Playing)
        {
            p.EverPlayed = true;
            float volume;
            lock (gate) volume = desiredVolume;
            ApplyVolume(volume, force: true); // re-applied on every transition to Playing
        }
        CheckVolumeDrift();
        Transition(session, state);
    }

    private void CheckVolumeDrift()
    {
        if (float.IsNaN(appliedVolume)) return;
        var actual = ObjCRuntime.SendFloat(player, Sel.Volume);
        if (Math.Abs(actual - appliedVolume) <= 0.01f) return;
        log.Warn("playback.engine_volume_drift", $"AVPlayer volume read {actual:0.00}, expected {appliedVolume:0.00}; re-applied.");
        ApplyVolume(appliedVolume, force: true);
    }

    private void Transition(long session, PlaybackEngineState state)
    {
        if (progress.Reported == state) return;
        progress.Reported = state;
        Report(session, state);
    }

    private void FailFromNative(long session, nint error, string source)
    {
        var chain = ReadErrorChain(error);
        var httpStatus = ReadErrorLogStatus();
        var kind = ClassifyNativeError(chain.Select(e => (e.Domain, e.Code)).ToList(), httpStatus, out var atsBlocked);
        Fail(session, kind, Describe(source, chain, httpStatus, atsBlocked, itemOrigin));
    }

    private void Fail(long session, PlaybackFailureKind kind, string diagnostic)
    {
        if (progress.Finished) return;
        progress.Finished = true;
        DropItem(); // a failed session produces no audio
        ReportFailure(session, kind, diagnostic);
    }

    private void EndOfStream(long session, string diagnostic)
    {
        if (progress.Finished) return;
        progress.Finished = true;
        DropItem();
        Report(session, PlaybackEngineState.Ended);
        ReportFailure(session, PlaybackFailureKind.EndOfStream, $"{diagnostic}; source={itemOrigin}");
    }

    private void SetPolling(bool on)
    {
        try { pollTimer.Change(on ? PollInterval : Timeout.InfiniteTimeSpan, on ? PollInterval : Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void QueuePoll()
    {
        if (Interlocked.Exchange(ref pollQueued, 1) == 0) Observe(MacMainQueue.InvokeAsync(Poll), "poll");
    }

    // ─── Notifications (posting thread; AVFoundation posts these on the main thread) ───

    private void OnNotification(nint notification)
    {
        try
        {
            var target = noteTarget;
            if (target is null || ObjCRuntime.SendId(notification, Sel.Object) != target.Item) return;
            var name = ObjCRuntime.ToManagedString(ObjCRuntime.SendId(notification, Sel.Name));
            if (name == StalledName)
            {
                Observe(MacMainQueue.InvokeAsync(() => OnItemStalled(target)), "stalled");
            }
            else if (name == DidPlayToEndName)
            {
                Observe(MacMainQueue.InvokeAsync(() => OnItemEvent(target, null)), "end");
            }
            else if (name == FailedToPlayToEndName)
            {
                // Copy the error now: the notification and its userInfo are only borrowed for this call.
                var info = ObjCRuntime.SendId(notification, Sel.UserInfo);
                var chain = ReadErrorChain(info == 0 ? 0 : ObjCRuntime.SendId(info, Sel.ObjectForKey, AVFoundation.FailedToPlayToEndTimeErrorKey));
                Observe(MacMainQueue.InvokeAsync(() => OnItemEvent(target, chain)), "failed");
            }
        }
        catch (Exception ex)
        {
            log.Error("playback.engine_error", "AVPlayer notification handling failed.", ex);
        }
    }

    private bool IsCurrent(NoteTarget target) => !nativeClosed && item == target.Item && itemSession == target.Session && !progress.Finished;

    private void OnItemStalled(NoteTarget target)
    {
        if (!IsCurrent(target)) return;
        progress.LastAdvanceAt = null; // Playing again only after fresh progress
        Transition(target.Session, PlaybackEngineState.Buffering);
    }

    /// <summary>did-play-to-end (<paramref name="failure"/> null) or failed-to-play-to-end.</summary>
    private void OnItemEvent(NoteTarget target, List<NativeError>? failure)
    {
        if (!IsCurrent(target)) return;
        if (failure is null)
        {
            EndOfStream(target.Session, "avplayer: AVPlayerItemDidPlayToEndTime");
            return;
        }
        var httpStatus = ReadErrorLogStatus();
        var kind = ClassifyNativeError(failure.Select(e => (e.Domain, e.Code)).ToList(), httpStatus, out var atsBlocked);
        Fail(target.Session, kind, Describe("failed-to-play-to-end", failure, httpStatus, atsBlocked, itemOrigin));
    }

    // ─── Error reading and normalization ───

    private static List<NativeError> ReadErrorChain(nint error)
    {
        var chain = new List<NativeError>();
        for (var depth = 0; error != 0 && depth < 5; depth++)
        {
            chain.Add(new NativeError(
                ObjCRuntime.ToManagedString(ObjCRuntime.SendId(error, Sel.Domain)) ?? "?",
                ObjCRuntime.SendNInt(error, Sel.Code),
                ObjCRuntime.ToManagedString(ObjCRuntime.SendId(error, Sel.LocalizedDescription)) ?? ""));
            var info = ObjCRuntime.SendId(error, Sel.UserInfo);
            error = info == 0 ? 0 : ObjCRuntime.SendId(info, Sel.ObjectForKey, AVFoundation.UnderlyingErrorKey);
        }
        return chain;
    }

    /// <summary>HTTP status of the item's last error-log event (HLS and progressive HTTP failures), if any.</summary>
    private long? ReadErrorLogStatus()
    {
        if (item == 0) return null;
        var errorLog = ObjCRuntime.SendId(item, Sel.ErrorLog);
        var entries = errorLog == 0 ? 0 : ObjCRuntime.SendId(errorLog, Sel.Events);
        var last = entries == 0 ? 0 : ObjCRuntime.SendId(entries, Sel.LastObject);
        return last == 0 ? null : ObjCRuntime.SendNInt(last, Sel.ErrorStatusCode);
    }

    /// <summary>
    /// Maps an NSError chain (outer → NSUnderlyingErrorKey) and the error-log HTTP status onto
    /// <see cref="PlaybackFailureKind"/>, following the table measured in docs/spikes.md. ATS is checked first, then the
    /// HTTP status, then the chain outer → inner; the first match wins.
    /// </summary>
    internal static PlaybackFailureKind ClassifyNativeError(IReadOnlyList<(string Domain, long Code)> chain, long? httpStatus, out bool atsBlocked)
    {
        // ATS blocked cleartext: NSURLErrorDomain -1022, or NSOSStatusErrorDomain -1022 under AVFoundationErrorDomain -11800.
        atsBlocked = chain.Any(e => e.Code == -1022 && e.Domain is "NSURLErrorDomain" or "NSOSStatusErrorDomain" or "kCFErrorDomainCFNetwork");
        if (atsBlocked) return PlaybackFailureKind.TlsFailure;
        if (httpStatus >= 400) return PlaybackFailureKind.HttpError;
        foreach (var (domain, code) in chain)
        {
            switch (domain)
            {
                case "NSURLErrorDomain":
                    switch (code)
                    {
                        case -1000 or -1002: return PlaybackFailureKind.InvalidUrl;
                        case -1001 or -1003 or -1004 or -1005 or -1006 or -1009 or -1018 or -1020: return PlaybackFailureKind.NetworkUnavailable;
                        case >= -1206 and <= -1200: return PlaybackFailureKind.TlsFailure;
                        case -1007 or -1008 or -1010 or -1011 or -1012 or -1013 or -1017 or -1100 or -1102: return PlaybackFailureKind.HttpError;
                        case -1015 or -1016: return PlaybackFailureKind.UnsupportedFormat;
                    }
                    break;
                case "CoreMediaErrorDomain" or "NSOSStatusErrorDomain" when code is -12938 or -12660 or -16840 or -16847:
                    return PlaybackFailureKind.HttpError; // CoreMedia's carriers of HTTP 404 / 403 / 401 / 500
                case "NSOSStatusErrorDomain" when code == -12847:
                    return PlaybackFailureKind.UnsupportedFormat; // not a media resource (e.g. an HTML page over HTTPS)
                case "CoreMediaErrorDomain" when code == -12646:
                    return PlaybackFailureKind.UnsupportedFormat; // parsed as a playlist because of a .pls/.m3u path
                case "AVFoundationErrorDomain" when code is -11828 or -11829 or -11821 or -11833 or -11838 or -11848 or -11849:
                    return PlaybackFailureKind.UnsupportedFormat;
                case "AVFoundationErrorDomain" when code == -11850:
                    return PlaybackFailureKind.HttpError; // "server is not correctly configured"
            }
        }
        return PlaybackFailureKind.Unknown;
    }

    private static string Describe(string source, List<NativeError> chain, long? httpStatus, bool atsBlocked, string origin)
    {
        var text = new StringBuilder("avplayer ").Append(source).Append(": ");
        text.Append(chain.Count == 0 ? "no NSError" : string.Join(" <- ", chain.Select(e => $"{e.Domain} {e.Code} \"{e.Description}\"")));
        if (httpStatus is { } status) text.Append("; error_log_http_status=").Append(status);
        if (atsBlocked) text.Append("; ATS blocked cleartext http: the app bundle is missing NSAllowsArbitraryLoadsForMedia");
        text.Append("; source=").Append(origin);
        return StreamDiagnostics.Redact(text.ToString(), maxLength: 400);
    }

    // ─── Events (raised by SerialEventQueue: thread pool, in order, never under `gate`) ───

    private void Report(long session, PlaybackEngineState state) => events.Post(() =>
    {
        lock (gate)
            if (disposeRequested || session != currentSession) return;
        StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(session, state));
    });

    private void ReportFailure(long session, PlaybackFailureKind kind, string diagnostic) =>
        events.Post(() =>
        {
            lock (gate)
            {
                if (disposeRequested || session != currentSession || lastFailedSession >= session) return;
                lastFailedSession = session; // at most one failure per session
            }
            Failed?.Invoke(this, new PlaybackEngineFailedEventArgs(session, kind, diagnostic));
        });

    private void RaiseStopped(long session)
    {
        lock (gate)
            if (disposeRequested || currentSession != 0 || sessionCounter != session) return; // a newer Start supersedes it
        StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(session, PlaybackEngineState.Stopped));
    }

    private void CancelSession(long session)
    {
        lock (gate)
        {
            if (disposeRequested || currentSession != session) return;
            currentSession = 0;
            if (request?.Session == session) request = null;
        }
        Observe(MacMainQueue.InvokeAsync(Reconcile), "cancel");
    }

    private void Observe(Task task, string what)
    {
        if (task.IsCompleted && !task.IsFaulted) return;
        task.ContinueWith(
            t => log.Error("playback.engine_error", $"AVPlayer main-thread work '{what}' failed.", t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed record Request(long Session, Uri Url, string Origin);

    private sealed record NoteTarget(nint Item, long Session);

    private sealed record NativeError(string Domain, long Code, string Description);

    private sealed record CancelledStart(MacAvPlayerPlaybackEngine Engine, long Session)
    {
        public void Cancel() => Engine.CancelSession(Session);
    }

    /// <summary>Per-item observation state (main thread only).</summary>
    private sealed class ItemProgress
    {
        public double LastSeconds = double.NaN;
        public long? LastAdvanceAt;
        public bool EverPlayed;
        public int PausedPolls;
        public bool Finished;
        public PlaybackEngineState Reported = PlaybackEngineState.Opening;
    }
}
