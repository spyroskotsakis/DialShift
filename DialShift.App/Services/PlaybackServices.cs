using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DialShift.Core.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DialShift.App.Services;

/// <summary>Composition of the per-OS playback engine (brief 1 §4.2, §7.8).</summary>
public static class PlaybackServiceCollectionExtensions
{
    /// <summary>
    /// Registers the singleton <see cref="PlaybackEngineFactory"/> for this OS. It deliberately does not register an
    /// <see cref="IPlaybackEngine"/>: see the factory for why. Any OS other than Windows or macOS throws
    /// <see cref="PlatformNotSupportedException"/>. The engine receives the registered <see cref="IAppLog"/> when there is
    /// one. <see cref="PlaybackEngineOptions.AudioOutputVariable"/> is read once, when the factory is first resolved.
    /// </summary>
    public static IServiceCollection AddDialShiftPlayback(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("DialShift plays audio on Windows (LibVLC) and macOS (AVPlayer) only.");
        services.TryAddSingleton(sp => new PlaybackEngineFactory(
            sp.GetService<IAppLog>() ?? NullAppLog.Instance,
            PlaybackEngineOptions.FromEnvironment(Environment.GetEnvironmentVariable)));
        return services;
    }
}

/// <summary>Engine settings the composition root reads once from the environment (developer, CI and smoke runs only).</summary>
/// <param name="AudioOutput">The trimmed value of <see cref="AudioOutputVariable"/>, or null when it is unset or blank.</param>
/// <remarks>
/// Only <c>DIALSHIFT_AUDIO_OUTPUT=dummy</c> (any case) has an effect: on Windows, LibVLC then uses its <c>adummy</c>
/// output (<see cref="LibVlcEngineOptions.Dummy"/>), so playback advances on machines without an audio device, such as
/// hosted CI runners. macOS ignores it: AVPlayer always uses the system output. Any other value is ignored with a warning.
/// </remarks>
public sealed record PlaybackEngineOptions(string? AudioOutput)
{
    public const string AudioOutputVariable = "DIALSHIFT_AUDIO_OUTPUT";

    /// <summary>The only value of <see cref="AudioOutputVariable"/> that has an effect.</summary>
    public const string DummyAudioOutputValue = "dummy";

    /// <summary>No override: the system audio output.</summary>
    public static PlaybackEngineOptions Default { get; } = new((string?)null);

    /// <summary>True when <see cref="AudioOutput"/> asks for the dummy output.</summary>
    public bool DummyAudioOutput => string.Equals(AudioOutput, DummyAudioOutputValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads <see cref="AudioOutputVariable"/> through <paramref name="getVariable"/> (<see cref="Environment.GetEnvironmentVariable(string)"/> in the app).</summary>
    public static PlaybackEngineOptions FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var value = getVariable(AudioOutputVariable)?.Trim();
        return string.IsNullOrEmpty(value) ? Default : new PlaybackEngineOptions(value);
    }
}

/// <summary>
/// Creates the one <see cref="IPlaybackEngine"/> for this OS: Windows → <see cref="LibVlcPlaybackEngine"/>, macOS →
/// <see cref="MacAvPlayerPlaybackEngine"/>.
/// </summary>
/// <remarks>
/// <para><b>Why a factory and not an <see cref="IPlaybackEngine"/> service (D17).</b> <see cref="PlaybackCoordinator"/> owns
/// the engine it is given. It maps the engine's session ids to its own queued starts (the N-th start is session N), so it
/// needs a fresh engine that nobody else has started, and it disposes that engine exactly once. The container therefore
/// never holds an engine: nothing can resolve one, and disposing the provider never disposes one. The composition root
/// calls <see cref="Create"/> once, inside its <see cref="IPlaybackCoordinator"/> registration; a second call throws.</para>
/// </remarks>
public sealed class PlaybackEngineFactory
{
    private readonly IAppLog log;
    private readonly PlaybackEngineOptions options;
    private int created;

    public PlaybackEngineFactory(IAppLog log, PlaybackEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("DialShift plays audio on Windows (LibVLC) and macOS (AVPlayer) only.");
        this.log = log;
        this.options = options;
        EngineName = OperatingSystem.IsWindows() ? nameof(LibVlcPlaybackEngine) : nameof(MacAvPlayerPlaybackEngine);
    }

    /// <summary>The engine type this OS uses, for the <c>app.start</c> log line.</summary>
    public string EngineName { get; }

    /// <summary>
    /// Creates the engine. The caller (the coordinator registration) takes ownership. When
    /// <see cref="PlaybackEngineOptions.AudioOutput"/> is set, logs <c>playback.audio_output</c> saying whether it was applied.
    /// </summary>
    /// <exception cref="InvalidOperationException">An engine was already created: the coordinator's engine is the only one (D17).</exception>
    public IPlaybackEngine Create()
    {
        if (Interlocked.Exchange(ref created, 1) != 0)
            throw new InvalidOperationException("The playback engine is created once, for the playback coordinator, which owns it (D17).");
        LogAudioOutputOverride();
        if (OperatingSystem.IsWindows())
            return new LibVlcPlaybackEngine(log, options.DummyAudioOutput ? LibVlcEngineOptions.Dummy : LibVlcEngineOptions.Default);
        if (OperatingSystem.IsMacOS()) return new MacAvPlayerPlaybackEngine(log);
        throw new PlatformNotSupportedException("DialShift plays audio on Windows (LibVLC) and macOS (AVPlayer) only.");
    }

    private void LogAudioOutputOverride()
    {
        const string variable = PlaybackEngineOptions.AudioOutputVariable;
        if (options.AudioOutput is null) return;
        if (!options.DummyAudioOutput)
            log.Warn("playback.audio_output", $"{variable} is ignored: the only supported value is '{PlaybackEngineOptions.DummyAudioOutputValue}'.");
        else if (OperatingSystem.IsWindows())
            log.Info("playback.audio_output", $"{variable}={PlaybackEngineOptions.DummyAudioOutputValue}: LibVLC discards audio (--aout={LibVlcEngineOptions.Dummy.AudioOutput}).");
        else
            log.Info("playback.audio_output", $"{variable}={PlaybackEngineOptions.DummyAudioOutputValue} is ignored on macOS: AVPlayer always uses the system audio output.");
    }
}

/// <summary>
/// Serial, off-thread event raising shared by both engines: work runs one item at a time, in post order, on the thread
/// pool — never inside a caller's StartAsync/StopAsync stack and never while an engine lock is held (IPlaybackEngine
/// threading rules). A throwing handler is logged and does not stop later events.
/// </summary>
internal sealed class SerialEventQueue(IAppLog log)
{
    private readonly ConcurrentQueue<Action> work = new();
    private int draining;

    public void Post(Action action)
    {
        work.Enqueue(action);
        if (Interlocked.CompareExchange(ref draining, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static queue => queue.Drain(), this, preferLocal: false);
    }

    private void Drain()
    {
        while (true)
        {
            while (work.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { log.Error("playback.engine_event_failed", "A playback engine event handler threw.", ex); }
            }
            Volatile.Write(ref draining, 0);
            // A post that raced with the reset above either sees draining == 0 and schedules its own drain, or is picked up here.
            if (work.IsEmpty || Interlocked.CompareExchange(ref draining, 1, 0) != 0) return;
        }
    }
}

/// <summary>Redaction helpers for engine diagnostics (IAppLog redaction rule: at most scheme + host + port).</summary>
internal static partial class StreamDiagnostics
{
    /// <summary>"scheme://host[:port]" of <paramref name="url"/>; never user-info, path, query or fragment.</summary>
    public static string Origin(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri) return "(not an absolute URL)";
        if (string.IsNullOrEmpty(url.Host)) return url.Scheme + ":";
        return url.IsDefaultPort ? $"{url.Scheme}://{url.Host}" : $"{url.Scheme}://{url.Host}:{url.Port}";
    }

    /// <summary>True for absolute http/https URLs with a host: the only sources either engine accepts.</summary>
    public static bool IsPlayable(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(url.Host);

    /// <summary>
    /// Reduces every URL inside native text to scheme://host[:port], drops query strings from anything else, and caps
    /// the length. Native messages (NSError descriptions, LibVLC log lines) can echo request URLs, tokens or credentials.
    /// </summary>
    public static string Redact(string? text, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var redacted = UrlPattern().Replace(text, m => Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) ? Origin(uri) : "(url)");
        redacted = QueryPattern().Replace(redacted, "?…");
        redacted = redacted.ReplaceLineEndings(" ").Trim();
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }

    /// <summary>Linear 0.0–1.0 volume, clamped; NaN is treated as muted.</summary>
    public static float ClampVolume(double volume) => double.IsNaN(volume) ? 0f : (float)Math.Clamp(volume, 0.0, 1.0);

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s'""`<>)\]]+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\?[^\s'""`<>)\]]+")]
    private static partial Regex QueryPattern();
}
