namespace DialShift.Core.Playback;

/// <summary>
/// What an engine should play. Deliberately engine-agnostic: no codec, container, header,
/// credential, or native option ever travels through this type.
/// </summary>
/// <param name="Url">Absolute http/https stream URL (already validated by <c>SettingsStore.ValidUrl</c>).</param>
/// <param name="DisplayName">Human-readable station name; used for diagnostics only, never for playback.</param>
public sealed record StreamSource(Uri Url, string DisplayName);

/// <summary>Normalized engine lifecycle state. Both LibVLC and AVPlayer map onto these values.</summary>
public enum PlaybackEngineState
{
    /// <summary>No session has been started, or the engine was just created.</summary>
    Idle,
    /// <summary><see cref="IPlaybackEngine.StartAsync"/> accepted the source; the engine is resolving/connecting.</summary>
    Opening,
    /// <summary>Connected but not producing audio (initial fill, or playback stopped advancing / waiting for data).</summary>
    Buffering,
    /// <summary>Audible playback is advancing. This is the only signal that a start actually succeeded.</summary>
    Playing,
    /// <summary>The source reported end-of-stream. Always followed by <see cref="IPlaybackEngine.Failed"/> with <see cref="PlaybackFailureKind.EndOfStream"/> for the same session.</summary>
    Ended,
    /// <summary>The session was stopped by <see cref="IPlaybackEngine.StopAsync"/>, a newer <see cref="IPlaybackEngine.StartAsync"/>, cancellation, or disposal.</summary>
    Stopped
}

/// <summary>Normalized failure categories. Adapters map native errors (LibVLC events, AVPlayerItem errors) onto these.</summary>
public enum PlaybackFailureKind
{
    /// <summary>No network route, DNS failure, connection refused/reset, host unreachable.</summary>
    NetworkUnavailable,
    /// <summary>The server answered with a non-success HTTP status (4xx/5xx), or an unusable redirect.</summary>
    HttpError,
    /// <summary>TLS handshake or certificate validation failed.</summary>
    TlsFailure,
    /// <summary>The engine cannot decode the container/codec/playlist.</summary>
    UnsupportedFormat,
    /// <summary>The engine rejected the URL itself.</summary>
    InvalidUrl,
    /// <summary>The engine natively detected a stall. (The 25 s stall watchdog is coordinator-owned and does not depend on this.)</summary>
    Stalled,
    /// <summary>A live stream ended. For a radio app this is a failure and triggers the retry policy.</summary>
    EndOfStream,
    /// <summary>Anything that could not be classified.</summary>
    Unknown
}

/// <summary>Raised when the engine session changes state.</summary>
/// <param name="SessionId">Session this event belongs to (see <see cref="IPlaybackEngine"/> session rules).</param>
/// <param name="State">The new state.</param>
public sealed record PlaybackEngineStateChangedEventArgs(long SessionId, PlaybackEngineState State);

/// <summary>Raised when the engine session fails. After this event the session produces no audio.</summary>
/// <param name="SessionId">Session this failure belongs to.</param>
/// <param name="Kind">Normalized failure category.</param>
/// <param name="Diagnostic">
/// Optional, already-redacted diagnostic text for the log. MUST NOT contain credentials, query strings,
/// or full private stream URLs (use scheme + host at most). Never shown verbatim as the only UI message.
/// </param>
public sealed record PlaybackEngineFailedEventArgs(long SessionId, PlaybackFailureKind Kind, string? Diagnostic = null);

/// <summary>
/// Lowest-common-denominator playback port (brief 1 §5.6). Describes only behavior guaranteed by BOTH
/// the Windows LibVLC adapter and the macOS AVPlayer adapter. Retry timing, fallback selection, schedule
/// intent, stall watchdog, wake recovery and cancellation generations are owned by the playback
/// coordinator and MUST NOT be implemented inside an adapter. No Objective-C/AppKit/CoreFoundation or
/// LibVLC type may cross this interface.
/// </summary>
/// <remarks>
/// <para><b>Sessions.</b> Every call to <see cref="StartAsync"/> begins a new session. The engine assigns
/// <c>SessionId</c> values from a per-instance counter that starts at 1 and is incremented by exactly one,
/// synchronously, on entry to each <see cref="StartAsync"/> call (before its first await and before any
/// event for that session), even if the call later fails or is cancelled. A caller that serializes its
/// engine commands therefore knows the id of the session it just requested (the N-th start is session N).
/// Starting a new session implicitly stops the previous one. Once <see cref="StartAsync"/>,
/// <see cref="StopAsync"/> or <see cref="IAsyncDisposable.DisposeAsync"/> has been invoked, the engine
/// MUST NOT raise events for any earlier session; the id on every event lets the coordinator discard any
/// stale callback that races past that rule anyway.</para>
/// <para><b>Successful start.</b> <see cref="StartAsync"/> completing successfully means only that the
/// request was accepted and the session exists. Audible playback is signalled solely by
/// <see cref="StateChanged"/> with <see cref="PlaybackEngineState.Playing"/>. Stream-level problems
/// (network, HTTP, TLS, format, URL rejected by the native engine) are reported through
/// <see cref="Failed"/> — possibly before <see cref="StartAsync"/> returns — and are never thrown.
/// <see cref="StartAsync"/> throws only <see cref="OperationCanceledException"/> (the session is then
/// stopped silently), <see cref="ObjectDisposedException"/>, or <see cref="ArgumentNullException"/>.
/// Callers must still treat any other exception as <see cref="PlaybackFailureKind.Unknown"/>.</para>
/// <para><b>Progress.</b> Adapters report <see cref="PlaybackEngineState.Buffering"/> whenever playback
/// stops advancing (LibVLC: buffering / no time progress; AVPlayer: <c>timeControlStatus</c> waiting) and
/// <see cref="PlaybackEngineState.Playing"/> again when it resumes. The coordinator's stall watchdog
/// measures time spent outside <see cref="PlaybackEngineState.Playing"/>.</para>
/// <para><b>Volume.</b> Linear 0.0–1.0 (values outside are clamped). The coordinator maps
/// <c>Settings.Volume</c> (int 0–100) as <c>volume / 100.0</c>. 0.0 means muted: adapters must produce no
/// audible output at 0.0 (LibVLC: <c>Mute = true</c>; AVPlayer: volume 0 / muted).</para>
/// <para><b>Threading.</b> All members may be called from any thread; adapters marshal to their native
/// thread affinity internally (AVPlayer: main thread). Events are raised on arbitrary threads, must not be
/// raised while an adapter holds an internal lock, and handlers must return quickly without blocking.
/// Adapters should not raise events synchronously inside <see cref="StartAsync"/>/<see cref="StopAsync"/>
/// call stacks; callers must tolerate it if they do.</para>
/// </remarks>
public interface IPlaybackEngine : IAsyncDisposable
{
    /// <summary>Session state changes. See the session and threading rules on <see cref="IPlaybackEngine"/>.</summary>
    event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    /// <summary>Session failures. At most one per session; the session is dead afterwards.</summary>
    event EventHandler<PlaybackEngineFailedEventArgs>? Failed;

    /// <summary>Stops any current session and begins a new one for <paramref name="source"/> at <paramref name="volume"/> (0.0–1.0).</summary>
    Task StartAsync(StreamSource source, double volume, CancellationToken ct);

    /// <summary>
    /// Stops the current session, if any. Idempotent. On completion no audio is produced, no
    /// <see cref="Failed"/> event is raised for the stopped session, and <see cref="PlaybackEngineState.Stopped"/>
    /// may be raised for it.
    /// </summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Changes the volume (0.0–1.0) of the current session and remembers it for later sessions. No-op when idle.</summary>
    Task SetVolumeAsync(double volume, CancellationToken ct);
}
