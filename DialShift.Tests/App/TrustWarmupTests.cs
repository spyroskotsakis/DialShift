using System.Diagnostics;
using DialShift.App.Services;
using DialShift.Core.Playback;
using DialShift.Tests.Core;
using DialShift.Tests.Fakes;
using DialShift.Tests.TestServers;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.App;

/// <summary>
/// TW-01..TW-14: the real <see cref="WindowsTrustWarmup"/> (D100) against <see cref="LocalTlsServer"/>, on every OS
/// (nothing in it is Windows-specific). The checks use the app's own handler from
/// <see cref="WindowsTrustWarmup.CreateHandler"/>; where a handshake has to succeed, the test pins the server's
/// self-signed certificate on that handler, and TW-06 keeps the handler's default validation to prove it is not bypassed.
/// Whether Windows then really adds a missing root is the by-hand check NC-20; the engine's use of the warm-up is HS-17
/// LV-12 (Windows).
/// </summary>
/// <remarks>
/// TW-07 checks the app's bound, <see cref="WindowsTrustWarmup.Timeout"/> (5 s), in real time. Every other check is about
/// something else, and a hosted runner can stall for seconds (Windows CI run 36244042354: TW-11, which takes about 10 ms,
/// took 7.3 s, and a warm-up ran into the 5 s bound after the server had counted its handshake, so a "no answer" warning
/// was logged). Every other check against a live server therefore runs the warm-up through its test seam with
/// <see cref="Patient"/> as the bound, and <see cref="Pinned"/> and <see cref="Unpinned"/> give the handler the same connect
/// timeout, so only a longer stall can turn their warm-up into a timeout (TW-02's user-info check uses the public
/// constructor over an in-memory handler, which has no bound to reach). The checks that time a prompt reaction (a body
/// closed, a cancellation or a disposal taking effect, TW-07's give-up) allow <see cref="Prompt"/>, or TW-07 its bound plus
/// <see cref="Patient"/>: still far from the outcome they rule out (an endless body never closes; a warm-up left alone on
/// <c>/hang</c> runs to its 15 s bound), and each prints the time it measured. A check that expects nothing logged prints
/// what was logged, and TW-11 prints each warm-up's duration and the server's counts, so a recurrence names its cause.
/// </remarks>
public static class TrustWarmupTests
{
    private const string Password = "warm-secret-4d1c";
    private const string Token = "warm-token-77e0";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>The warm-up's bound (and the handler's connect timeout) in every live-server check but TW-07's; below <see cref="Bound"/>.</summary>
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(15);

    /// <summary>How soon a prompt reaction (a body closed, a cancellation or disposal taking effect) must show; below <see cref="Patient"/>.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        HandlerChecks();
        await using var server = LocalTlsServer.Start();
        await using var other = LocalTlsServer.Start();
        await using var third = LocalTlsServer.Start();
        await SuccessChecksAsync(server);
        await RedirectChecksAsync(server, other);
        await RedirectCacheChecksAsync(server, other, third);
        await RedirectLimitChecksAsync(server);
        await NonHttpAnswerChecksAsync();
        await StrictValidationChecksAsync(server);
        await TimeoutChecksAsync(server);
        await HttpChecksAsync();
        await CancellationChecksAsync(server);
        await DisposeChecksAsync(server);
    }

    /// <summary>The app's handler: the D100 settings, and default certificate validation.</summary>
    private static void HandlerChecks()
    {
        using var handler = WindowsTrustWarmup.CreateHandler();
        Check($"TW-01 the app's handler follows redirects, at most {WindowsTrustWarmup.MaxRedirects}, and connects within {WindowsTrustWarmup.Timeout.TotalSeconds:0} s",
            handler.AllowAutoRedirect && handler.MaxAutomaticRedirections == WindowsTrustWarmup.MaxRedirects && handler.ConnectTimeout == WindowsTrustWarmup.Timeout);
        Check("TW-01 ... drains no response body, keeps no cookies and has no credentials",
            handler.MaxResponseDrainSize == 0 && !handler.UseCookies && handler.Credentials is null && !handler.PreAuthenticate);
        Check("TW-01 ... and keeps default certificate validation (no validation callback, no client certificates)",
            handler.SslOptions.RemoteCertificateValidationCallback is null && handler.SslOptions.ClientCertificates is null or { Count: 0 });
        Check("TW-01 the warm-up refuses a null handler or log",
            Throws<ArgumentNullException>(() => new WindowsTrustWarmup(null!, NullAppLog.Instance)) && Throws<ArgumentNullException>(() => new WindowsTrustWarmup(Pinned(), null!)));
        Check("TW-01 ... and its test seam refuses a bound that is not positive",
            Throws<ArgumentOutOfRangeException>(() => new WindowsTrustWarmup(Pinned(), NullAppLog.Instance, TimeSpan.Zero)));
    }

    /// <summary>One GET without user-info, the headers awaited, the body left unread, the origin cached.</summary>
    private static async Task SuccessChecksAsync(LocalTlsServer server)
    {
        var log = new RecordingAppLog();
        using var warmup = Warmup(Pinned(), log);
        var url = new Uri($"https://listener:{Password}@127.0.0.1:{server.Port}/stream?token={Token}#part");
        var requests = server.Requests.Count;
        var watch = Stopwatch.StartNew();
        var warmed = await Wait.Finishes(warmup.WarmAsync(url, CancellationToken.None), Bound);
        var took = watch.Elapsed;
        var request = server.Requests.Skip(requests).FirstOrDefault();
        var version = typeof(WindowsTrustWarmup).Assembly.GetName().Version!.ToString(3);
        Check($"TW-02 an https warm-up returns once the response headers arrive ({took.TotalSeconds:0.00} s; the body never ends, so waiting for it would take the {Patient.TotalSeconds:0} s bound) after one GET for the URL's path",
            warmed && took < Patient && server.Requests.Count == requests + 1 && request is { Method: "GET", Path: "/stream" });
        Check($"TW-02 ... without the URL's user-info (no Authorization header: {request?.AuthorizationSummary}) and with User-Agent DialShift/{version} (got '{request?.Header("User-Agent")}')",
            request is not null && request.Header("Authorization") is null && request.Header("User-Agent") == $"DialShift/{version}");
        var (closed, closedAfter) = await ClosesAsync(() => server.OpenStreams);
        Check($"TW-03 the response is disposed unread: the server's endless chunked body ends {closedAfter.TotalMilliseconds:0} ms after the warm-up returned (within {Prompt.TotalSeconds:0} s; open streams {server.OpenStreams})", closed);
        Check($"TW-03 ... and a successful warm-up logs nothing ({Logged(log)})", log.Entries.Count == 0);

        var accepted = server.Accepted;
        var cachedCall = warmup.WarmAsync(server.Url("/another/path"), CancellationToken.None);
        var atOnce = cachedCall.IsCompletedSuccessfully;
        await cachedCall;
        await Task.Delay(200); // a connection, had one been made, would have been accepted by now
        Check($"TW-04 the warmed origin is cached: another URL on https://127.0.0.1:{server.Port} completes synchronously ({atOnce}) without connecting",
            atOnce && server.Accepted == accepted && server.Requests.Count == requests + 1);

        // A fresh instance (the origin is cached above): a server that asks for credentials still never gets the URL's.
        using var challenged = Warmup(Pinned(), log);
        var before = server.Requests.Count;
        await challenged.WarmAsync(new Uri($"https://listener:{Password}@127.0.0.1:{server.Port}/auth"), CancellationToken.None);
        var seen = server.Requests.Skip(before).ToList();
        Check($"TW-02 ... not even after a 401 Basic challenge: one request, no Authorization header, nothing logged (server saw [{string.Join(", ", seen)}]; {Logged(log)})",
            seen.Count == 1 && seen[0].Path == "/auth" && seen[0].Header("Authorization") is null && log.Entries.Count == 0);

        // SocketsHttpHandler ignores a URL's user-info anyway (the checks above pass without the stripping), so the
        // stripping itself is checked on the request the warm-up hands to its handler.
        var recording = new RecordingHandler();
        using (var recorded = new WindowsTrustWarmup(recording, log))
            await recorded.WarmAsync(url, CancellationToken.None);
        Check($"TW-02 ... the request the handler receives carries no user-info or fragment and keeps the path and query (got {recording.Uris.SingleOrDefault()?.OriginalString})",
            recording.Uris.SingleOrDefault()?.OriginalString == $"https://127.0.0.1:{server.Port}/stream?token={Token}");
    }

    /// <summary>Answers every request 200 with an empty body and records its URI.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Uri> uris = new();

        public IReadOnlyList<Uri> Uris => [.. uris];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            uris.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request });
        }
    }

    /// <summary>A redirect to another origin is followed, so both hops' handshakes happen.</summary>
    private static async Task RedirectChecksAsync(LocalTlsServer server, LocalTlsServer other)
    {
        using var warmup = Warmup(Pinned(), NullAppLog.Instance);
        server.RedirectTarget = other.Url("/stream");
        var (fromRequests, toRequests, toHandshakes) = (server.Requests.Count, other.Requests.Count, other.Handshakes);
        var warmed = await Wait.Finishes(warmup.WarmAsync(server.Url("/redirect"), CancellationToken.None), Bound);
        Check($"TW-05 a redirect to another https origin is followed: the first server saw /redirect, the second a completed handshake and /stream",
            warmed && server.Requests.Skip(fromRequests).Select(r => r.Path).SequenceEqual(["/redirect"])
            && other.Handshakes == toHandshakes + 1 && other.Requests.Skip(toRequests).Select(r => r.Path).SequenceEqual(["/stream"]));
        var (closed, closedAfter) = await ClosesAsync(() => other.OpenStreams);
        Check($"TW-05 ... and the redirect's target body is disposed unread too (it ends {closedAfter.TotalMilliseconds:0} ms after the warm-up returned, within {Prompt.TotalSeconds:0} s)", closed);
    }

    /// <summary>A reply that is not HTTP (Shoutcast's "ICY 200 OK") came after the handshake: not a failure, but its hop is unknown, so not cached.</summary>
    private static async Task NonHttpAnswerChecksAsync()
    {
        await using var icy = LocalTlsServer.Start();
        var log = new RecordingAppLog();
        using var warmup = Warmup(Pinned(), log);
        var first = await TimedAsync(() => warmup.WarmAsync(icy.Url("/icy"), CancellationToken.None));
        var second = await TimedAsync(() => warmup.WarmAsync(icy.Url("/icy"), CancellationToken.None));
        // The server counts a handshake when its side completes, which can be just after the client's warm-up returned.
        var handshakes = await Wait.Until(() => icy.Handshakes == 2, TimeSpan.FromSeconds(5));
        Check($"TW-11 an 'ICY 200 OK' reply is not a failure (nothing is logged) and, its hop unknown, is not cached: the second warm-up completes a handshake again " +
              $"(warm-ups {first.TotalMilliseconds:0} ms and {second.TotalMilliseconds:0} ms; server: {icy.Accepted} accepted, {icy.Handshakes} handshakes, {icy.Requests.Count} requests; {Logged(log)})",
            log.Entries.Count == 0 && handshakes);
    }

    /// <summary>The cache records the origin that answered: A→B records B, not A, so A→C later is warmed again.</summary>
    private static async Task RedirectCacheChecksAsync(LocalTlsServer server, LocalTlsServer other, LocalTlsServer third)
    {
        using var warmup = Warmup(Pinned(), NullAppLog.Instance);
        server.RedirectTarget = other.Url("/stream");
        await warmup.WarmAsync(server.Url("/redirect"), CancellationToken.None);
        var (serverRequests, otherAccepted, thirdRequests) = (server.Requests.Count, other.Accepted, third.Requests.Count);
        var cachedCall = warmup.WarmAsync(other.Url("/another/path"), CancellationToken.None);
        var otherCached = cachedCall.IsCompletedSuccessfully;
        await cachedCall;
        server.RedirectTarget = third.Url("/stream");
        await warmup.WarmAsync(server.Url("/redirect"), CancellationToken.None);
        await Task.Delay(200);
        Check($"TW-12 A redirected to B: B, the origin that answered, is cached (a URL on it completes synchronously: {otherCached}, no new connection)",
            otherCached && other.Accepted == otherAccepted);
        Check("TW-12 ... A is not: when A now redirects to C, the next warm-up requests A again and follows it to C",
            server.Requests.Skip(serverRequests).Select(r => r.Path).SequenceEqual(["/redirect"])
            && third.Requests.Skip(thirdRequests).Select(r => r.Path).SequenceEqual(["/stream"]));
        var (closed, closedAfter) = await ClosesAsync(() => third.OpenStreams);
        Check($"TW-12 ... and C's body is disposed unread (it ends {closedAfter.TotalMilliseconds:0} ms after the warm-up returned, within {Prompt.TotalSeconds:0} s)", closed);
    }

    /// <summary>What the handler does not follow: https → http, and a redirect past MaxRedirects.</summary>
    private static async Task RedirectLimitChecksAsync(LocalTlsServer server)
    {
        await using var plain = LocalMediaServer.Start();
        var log = new RecordingAppLog();
        using (var downgrade = Warmup(Pinned(), log))
        {
            server.RedirectTarget = plain.Url("/live.wav");
            var requests = server.Requests.Count;
            await downgrade.WarmAsync(server.Url("/redirect"), CancellationToken.None);
            await downgrade.WarmAsync(server.Url("/redirect"), CancellationToken.None);
            await Task.Delay(200);
            Check($"TW-13 an https → http redirect is not followed: the http server sees no request ({plain.Requests.Count}), nothing is logged ({Logged(log)}), "
                + "and the redirecting origin is not cached (the second warm-up requests it again)",
                plain.Requests.Count == 0 && log.Entries.Count == 0 && server.Requests.Skip(requests).Count(r => r.Path == "/redirect") == 2);
        }
        using (var multiple = Warmup(Pinned(), log))
        {
            var requests = server.Requests.Count;
            await multiple.WarmAsync(server.Url("/redirect-300"), CancellationToken.None);
            await multiple.WarmAsync(server.Url("/redirect-300"), CancellationToken.None);
            await Task.Delay(200);
            Check($"TW-13 ... the same with 300 Multiple Choices: not followed ({plain.Requests.Count} http requests), not logged ({Logged(log)}), not cached",
                plain.Requests.Count == 0 && log.Entries.Count == 0 && server.Requests.Skip(requests).Count(r => r.Path == "/redirect-300") == 2);
        }

        var max = WindowsTrustWarmup.MaxRedirects;
        using (var chain = Warmup(Pinned(), log))
        {
            var requests = server.Requests.Count;
            await chain.WarmAsync(server.Url($"/chain/{max + 1}"), CancellationToken.None);
            var seen = server.Requests.Skip(requests).Select(r => r.Path).ToList();
            Check($"TW-14 a chain of {max + 1} redirects is followed {max} times and no further: [{string.Join(", ", seen)}] ({Logged(log)})",
                seen.SequenceEqual(Enumerable.Range(1, max + 1).Reverse().Select(n => $"/chain/{n}")) && log.Entries.Count == 0);
        }
        using (var chain = Warmup(Pinned(), log))
        {
            var requests = server.Requests.Count;
            await chain.WarmAsync(server.Url($"/chain/{max}"), CancellationToken.None);
            var seen = server.Requests.Skip(requests).Select(r => r.Path).ToList();
            Check($"TW-14 ... a chain of {max} redirects is followed to its end: [{string.Join(", ", seen)}]",
                seen.SequenceEqual(Enumerable.Range(0, max + 1).Reverse().Select(n => $"/chain/{n}")));
        }
    }

    /// <summary>Default validation, the certificate untrusted: one redacted warning per attempt, nothing cached.</summary>
    private static async Task StrictValidationChecksAsync(LocalTlsServer server)
    {
        var log = new RecordingAppLog();
        using var warmup = Warmup(Unpinned(), log);
        var url = new Uri($"https://listener:{Password}@127.0.0.1:{server.Port}/private/stream?token={Token}");
        var (accepted, requests) = (server.Accepted, server.Requests.Count);
        var first = await Wait.Finishes(warmup.WarmAsync(url, CancellationToken.None), Bound);
        var second = await Wait.Finishes(warmup.WarmAsync(url, CancellationToken.None), Bound);
        var reconnected = await Wait.Until(() => server.Accepted == accepted + 2, TimeSpan.FromSeconds(5));
        // The server may see its side of the handshake finish: .NET validates the chain once the TLS exchange is done.
        Check($"TW-06 default validation refuses the untrusted certificate: the client sends no request ({server.Requests.Count - requests} requests)",
            first && second && server.Requests.Count == requests);
        Check($"TW-06 ... a failure is not cached: the second warm-up connects again ({server.Accepted - accepted} connections)", reconnected);
        var entries = log.Entries;
        var origin = $"https://127.0.0.1:{server.Port}/{StreamUrlRedactor.Ellipsis}";
        Check($"TW-06 ... each failure logs one {WindowsTrustWarmup.LogEvent} warning naming {origin} and the TLS error ({string.Join(" | ", entries.Select(e => e.Message))})",
            entries.Count == 2 && entries.All(e => e.Level == AppLogLevel.Warn && e.EventName == WindowsTrustWarmup.LogEvent
                && e.Message.Contains(origin, StringComparison.Ordinal) && e.Message.Contains("SecureConnectionError", StringComparison.Ordinal)));
        Check("TW-06 ... and the log line holds no user-info, path or query", log.NoEntryContains(Password, "listener", "/private", Token));
    }

    /// <summary>A server that completes the handshake and never answers: bounded by Timeout, logged, not cached.</summary>
    private static async Task TimeoutChecksAsync(LocalTlsServer server)
    {
        var log = new RecordingAppLog();
        using var warmup = new WindowsTrustWarmup(Pinned(WindowsTrustWarmup.Timeout), log); // the app's bound, 5 s
        var watch = Stopwatch.StartNew();
        var finished = await Wait.Finishes(warmup.WarmAsync(server.Url("/hang"), CancellationToken.None), Bound);
        var took = watch.Elapsed;
        Check($"TW-07 a server that never answers: the warm-up gives up after {took.TotalSeconds:0.0} s (Timeout {WindowsTrustWarmup.Timeout.TotalSeconds:0} s; at least that, "
              + $"less than {(WindowsTrustWarmup.Timeout + Patient).TotalSeconds:0} s) without throwing",
            finished && took >= WindowsTrustWarmup.Timeout - TimeSpan.FromMilliseconds(100) && took < WindowsTrustWarmup.Timeout + Patient);
        Check($"TW-07 ... and logs one warning that it got no answer ({string.Join(" | ", log.Entries.Select(e => e.Message))})",
            log.Entries.Count == 1 && log.Entries[0] is { Level: AppLogLevel.Warn } entry && entry.Message.Contains("no answer within 5 s", StringComparison.Ordinal));
        var requests = server.Requests.Count;
        await warmup.WarmAsync(server.Url("/stream"), CancellationToken.None);
        Check("TW-07 ... a timed-out origin is not cached: the next warm-up requests it again",
            server.Requests.Skip(requests).Select(r => r.Path).SequenceEqual(["/stream"]));
    }

    /// <summary>http:// and relative URLs are never requested.</summary>
    private static async Task HttpChecksAsync()
    {
        await using var plain = LocalMediaServer.Start();
        using var warmup = Warmup(Pinned(), NullAppLog.Instance);
        await warmup.WarmAsync(plain.Url("/live.wav"), CancellationToken.None);
        await warmup.WarmAsync(new Uri("/stream", UriKind.Relative), CancellationToken.None);
        await Task.Delay(200);
        Check("TW-08 an http:// URL is never warmed (the server saw no request), nor a relative one", plain.Requests.Count == 0);
        Check("TW-08 a null URL throws ArgumentNullException", await ThrowsAsync<ArgumentNullException>(() => warmup.WarmAsync(null!, CancellationToken.None)));
    }

    /// <summary>The caller's token: OperationCanceledException, promptly, and no log line.</summary>
    private static async Task CancellationChecksAsync(LocalTlsServer server)
    {
        var log = new RecordingAppLog();
        using var warmup = Warmup(Pinned(), log);
        var accepted = server.Accepted;
        Check("TW-09 a cancelled token throws OperationCanceledException before connecting",
            await ThrowsAsync<OperationCanceledException>(() => warmup.WarmAsync(server.Url("/stream"), new CancellationToken(canceled: true))) && server.Accepted == accepted);

        using var cts = new CancellationTokenSource();
        var requests = server.Requests.Count;
        var warming = warmup.WarmAsync(server.Url("/hang"), cts.Token);
        var waiting = await Wait.Until(() => server.Requests.Count == requests + 1, Bound);
        var watch = Stopwatch.StartNew();
        await cts.CancelAsync();
        var threw = await ThrowsAsync<OperationCanceledException>(() => warming.WaitAsync(Bound));
        Check($"TW-09 cancelled while waiting for the answer: OperationCanceledException after {watch.Elapsed.TotalMilliseconds:0} ms (within {Prompt.TotalSeconds:0} s, "
              + $"not at the {Patient.TotalSeconds:0} s bound), and nothing is logged ({Logged(log)})",
            waiting && threw && watch.Elapsed < Prompt && log.Entries.Count == 0);
    }

    /// <summary>Dispose (the engine's disposal) cancels a warm-up in flight silently; later warm-ups do nothing.</summary>
    private static async Task DisposeChecksAsync(LocalTlsServer server)
    {
        var log = new RecordingAppLog();
        var warmup = Warmup(Pinned(), log);
        var requests = server.Requests.Count;
        var warming = warmup.WarmAsync(server.Url("/hang"), CancellationToken.None);
        var waiting = await Wait.Until(() => server.Requests.Count == requests + 1, Bound);
        var watch = Stopwatch.StartNew();
        warmup.Dispose();
        var ended = await NoThrowAsync(() => warming.WaitAsync(Bound));
        Check($"TW-10 Dispose ends a warm-up in flight at once ({watch.Elapsed.TotalMilliseconds:0} ms, within {Prompt.TotalSeconds:0} s, not at the {Patient.TotalSeconds:0} s bound) "
              + $"without an exception or a log line ({Logged(log)})",
            waiting && ended && watch.Elapsed < Prompt && log.Entries.Count == 0);
        var accepted = server.Accepted;
        await warmup.WarmAsync(server.Url("/stream"), CancellationToken.None);
        warmup.Dispose();
        await Task.Delay(200);
        Check($"TW-10 ... after Dispose a warm-up returns without connecting, and a second Dispose is harmless ({Logged(log)})", server.Accepted == accepted && log.Entries.Count == 0);
    }

    /// <summary>The warm-up under test, bounded by <see cref="Patient"/> instead of the app's <see cref="WindowsTrustWarmup.Timeout"/>.</summary>
    private static WindowsTrustWarmup Warmup(HttpMessageHandler handler, IAppLog log) => new(handler, log, Patient);

    /// <summary>The app's handler with its default certificate validation, connecting within <paramref name="connectTimeout"/> (default <see cref="Patient"/>).</summary>
    private static SocketsHttpHandler Unpinned(TimeSpan? connectTimeout = null)
    {
        var handler = WindowsTrustWarmup.CreateHandler();
        handler.ConnectTimeout = connectTimeout ?? Patient;
        return handler;
    }

    /// <summary>The app's handler, trusting only the test server's certificate: besides the connect timeout (see
    /// <see cref="Unpinned"/>), the handshake is the only thing the test changes.</summary>
    private static SocketsHttpHandler Pinned(TimeSpan? connectTimeout = null)
    {
        var handler = Unpinned(connectTimeout);
        handler.SslOptions.RemoteCertificateValidationCallback = static (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(), LocalTlsServer.Thumbprint, StringComparison.OrdinalIgnoreCase);
        return handler;
    }

    /// <summary>Whether the server's open streams (<paramref name="open"/>) drop to 0 within <see cref="Prompt"/>, and how long that took.</summary>
    private static async Task<(bool Closed, TimeSpan After)> ClosesAsync(Func<int> open)
    {
        var watch = Stopwatch.StartNew();
        var closed = await Wait.Until(() => open() == 0, Prompt);
        return (closed, watch.Elapsed);
    }

    /// <summary>How long <paramref name="warm"/> took.</summary>
    private static async Task<TimeSpan> TimedAsync(Func<Task> warm)
    {
        var watch = Stopwatch.StartNew();
        await warm();
        return watch.Elapsed;
    }

    /// <summary>"nothing logged", or every entry (level, event, message): a check that expects no log line names the one it got.</summary>
    private static string Logged(RecordingAppLog log) =>
        log.Entries is { Count: > 0 } entries ? "logged: " + string.Join(" | ", entries.Select(e => $"{e.Level} {e.EventName}: {e.Message}")) : "nothing logged";
}
