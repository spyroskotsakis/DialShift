using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DialShift.Core.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DialShift.App.Services;

/// <summary>Composition of the per-OS playback engine (brief 1 §4.2, §7.8).</summary>
public static class PlaybackServiceCollectionExtensions
{
    /// <summary>
    /// Registers the one <see cref="IPlaybackEngine"/> for this OS as a singleton: Windows → <see cref="LibVlcPlaybackEngine"/>,
    /// macOS → <see cref="MacAvPlayerPlaybackEngine"/>. Any other OS throws <see cref="PlatformNotSupportedException"/>.
    /// The engine receives the registered <see cref="IAppLog"/> when there is one.
    /// </summary>
    /// <remarks>
    /// Ownership: <see cref="PlaybackCoordinator"/> disposes the engine it is given. The container also tracks the
    /// singleton; engine disposal is idempotent, but both engines implement only <see cref="IAsyncDisposable"/>, so the
    /// host must dispose the service provider with <c>DisposeAsync</c> (a synchronous <c>Dispose</c> of the provider
    /// throws for async-only singletons).
    /// </remarks>
    public static IServiceCollection AddDialShiftPlayback(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (OperatingSystem.IsWindows())
            services.TryAddSingleton<IPlaybackEngine>(CreateWindowsEngine);
        else if (OperatingSystem.IsMacOS())
            services.TryAddSingleton<IPlaybackEngine>(CreateMacEngine);
        else
            throw new PlatformNotSupportedException("DialShift plays audio on Windows (LibVLC) and macOS (AVPlayer) only.");
        return services;
    }

    [SupportedOSPlatform("windows")]
    private static IPlaybackEngine CreateWindowsEngine(IServiceProvider services) => new LibVlcPlaybackEngine(LogFrom(services));

    [SupportedOSPlatform("macos")]
    private static IPlaybackEngine CreateMacEngine(IServiceProvider services) => new MacAvPlayerPlaybackEngine(LogFrom(services));

    private static IAppLog LogFrom(IServiceProvider services) => services.GetService<IAppLog>() ?? NullAppLog.Instance;
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
