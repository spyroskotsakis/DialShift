using System.Collections.Concurrent;
using DialShift.Core.Playback;

namespace DialShift.App.Services;

/// <summary>
/// The <see cref="IStreamTrustWarmup"/> of the Windows engine (D100): one .NET HTTPS request to the stream, so that
/// Windows downloads any trusted root its certificate chain needs before LibVLC connects.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> Windows ships with part of its trusted roots and adds a missing one from Windows Update ("Automatic Root
/// Certificates Update") only while a CryptoAPI chain build asks for it, as SChannel and .NET do. LibVLC's GnuTLS reads the
/// roots already in the store and never triggers that download, so on a Windows 11 without it every station whose chain
/// ends at a root not yet present (SomaFM's: USERTrust RSA Certification Authority) failed with "TLS session handshake error". A .NET
/// handshake with the same server makes Windows add the root, and LibVLC's next handshake finds it.</para>
/// <para><b>The request.</b> A GET for the stream URL without its user-info (station credentials never go into the
/// warm-up; the query stays, as the player sends it to the same server), with <see cref="AppUserAgent"/>, through
/// <see cref="CreateHandler"/>'s handler: redirects followed (at most <see cref="MaxRedirects"/>, never https to http), so
/// every https hop LibVLC will follow has its chain built; default certificate validation, never bypassed; no cookies,
/// no credentials. Only the response headers are awaited: any HTTP status will do, because the handshakes are complete
/// by then. The response is disposed at once and its body is never read (the handler drains nothing, so the connection
/// closes). A reply that is not HTTP (<see cref="HttpRequestError.InvalidResponse"/>, such as a Shoutcast <c>ICY 200
/// OK</c> status line) or that ends early (<see cref="HttpRequestError.ResponseEnded"/>) also came after the handshake,
/// so it is not a failure and is not logged.</para>
/// <para><b>Bounds and failures.</b> The whole request, redirects included, is cancelled after <see cref="Timeout"/> or
/// when the caller's token is cancelled. Any other failure (DNS, refused, TLS, the timeout) is logged once as a redacted
/// <c>playback.tls_warmup</c> warning (<c>scheme://host[:port]/…</c> and the exception chain) and swallowed: the player
/// connects anyway and reports its own failure as before.</para>
/// <para><b>Cache.</b> The engine keeps one instance for the process lifetime. When a warm-up gets an HTTP response, the
/// origin (<c>scheme://host[:port]</c>) that sent it, the last hop, is recorded if it is https, and a later warm-up of a
/// URL on a recorded origin returns at once. A requested origin that redirected elsewhere is therefore not recorded: its
/// next redirect may go to another host (a load balancer), whose chain must be warmed again. A final response that is
/// itself a redirect (one past <see cref="MaxRedirects"/>, or to http, which is not followed), a failed or timed-out
/// warm-up, and a reply that is not HTTP (its last hop is unknown) record nothing. http:// URLs are never requested.</para>
/// <para>Owns <c>handler</c>; <see cref="Dispose"/> cancels requests in flight (they return without a log line). Thread-safe.
/// Nothing in it is Windows-specific, so its tests run on every OS; only the Windows engine uses it.</para>
/// </remarks>
public sealed class WindowsTrustWarmup : IStreamTrustWarmup
{
    /// <summary>The bound on one warm-up, connection and redirects included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The most redirects one warm-up follows.</summary>
    public const int MaxRedirects = 5;

    /// <summary>The log event of a failed warm-up.</summary>
    public const string LogEvent = "playback.tls_warmup";

    private readonly IAppLog log;
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, byte> warmed = new(StringComparer.Ordinal);
    private int disposed;

    /// <param name="handler">The handler the requests go through: <see cref="CreateHandler"/> in the app. Owned.</param>
    /// <param name="log">Receives the redacted line of a failed warm-up.</param>
    public WindowsTrustWarmup(HttpMessageHandler handler, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
        // Each warm-up carries its own Timeout token, so the client's own timeout stays out of the way.
        client = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(AppUserAgent.Create());
    }

    /// <summary>
    /// The handler the app uses: redirects followed (at most <see cref="MaxRedirects"/>), <see cref="Timeout"/> to
    /// connect, no response body drained on dispose, no cookies; default proxy, credentials (none) and certificate
    /// validation.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = MaxRedirects,
        ConnectTimeout = Timeout,
        MaxResponseDrainSize = 0,
        UseCookies = false,
    };

    public async Task WarmAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ct.ThrowIfCancellationRequested();
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || Volatile.Read(ref disposed) != 0) return;
        if (warmed.ContainsKey(Origin(url))) return;

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WithoutUserInfo(url));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token).ConfigureAwait(false);
            // The handler updates the request's URI on each redirect it follows: this is the origin that answered.
            // A redirect it did not follow (past MaxRedirects, or to http) means the chain goes on: nothing is recorded.
            if (!IsRedirect(response) && (response.RequestMessage ?? request).RequestUri is { IsAbsoluteUri: true } last && last.Scheme == Uri.UriSchemeHttps)
                warmed.TryAdd(Origin(last), 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (Volatile.Read(ref disposed) != 0)
        {
            // Disposed with the engine while connecting: nobody is waiting for the answer.
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded)
        {
            // A server answered after its handshake, just not in HTTP. Which hop it was is unknown, so nothing is recorded.
        }
        catch (Exception ex)
        {
            var reason = bound.IsCancellationRequested ? $"no answer within {Timeout.TotalSeconds:0} s" : Describe(ex);
            log.Warn(LogEvent, StreamUrlRedactor.RedactDiagnostic(
                $"TLS trust warm-up for {StreamUrlRedactor.RedactUrl(url)} failed ({reason}); the player connects anyway.", maxLength: 400));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Dispose(); // cancels the requests in flight
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is not null;

    /// <summary><c>scheme://host[:port]</c> (the port only when it is not the scheme's default), without user-info.</summary>
    private static string Origin(Uri url) => url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);

    /// <summary><paramref name="url"/> without user name and password (and fragment, which is never sent anyway).</summary>
    private static Uri WithoutUserInfo(Uri url) =>
        url.UserInfo.Length == 0 ? url : new UriBuilder(url) { UserName = "", Password = "", Fragment = "" }.Uri;

    /// <summary>"HttpRequestException SecureConnectionError: … &lt;- AuthenticationException: …": the first three exceptions of the chain.</summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>(3);
        for (var e = ex; e is not null && parts.Count < 3; e = e.InnerException)
            parts.Add(e is HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } http
                ? $"{e.GetType().Name} {http.HttpRequestError}: {e.Message}"
                : $"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }
}
