using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DialShift.Tests.TestServers;

/// <summary>
/// In-process HTTPS server on 127.0.0.1 for the TLS trust warm-up checks (D100), with a self-signed certificate made
/// for the test run, which no system trusts: a client with default validation fails its handshake, and a test client
/// can pin <see cref="Thumbprint"/>. Paths (the query string is ignored):
/// <list type="bullet">
/// <item><c>/stream</c>: an endless chunked body (HTTP/1.1, no <c>Connection: close</c>, so a client may try to drain it
/// for reuse), 4 KiB every 50 ms until the client closes; <see cref="OpenStreams"/> counts the ones still open.</item>
/// <item><c>/icy</c>: a Shoutcast v1 status line (<c>ICY 200 OK</c>), then the same endless body unchunked.</item>
/// <item><c>/auth</c>: 401 with a Basic challenge, whatever the request carries.</item>
/// <item><c>/redirect</c>: 302 to <see cref="RedirectTarget"/>.</item>
/// <item><c>/chain/&lt;n&gt;</c>: 302 to <c>/chain/&lt;n-1&gt;</c> on this server; <c>/chain/0</c> answers 200 with an empty body.</item>
/// <item><c>/hang</c>: reads the request and never answers.</item>
/// <item>anything else: 404.</item>
/// </list>
/// </summary>
public sealed class LocalTlsServer : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> SharedCertificate = new(CreateCertificate);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PaceInterval = TimeSpan.FromMilliseconds(50);
    private static readonly byte[] Chunk = new byte[4096];

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Task, byte> connections = new();
    private readonly ConcurrentQueue<ServerRequest> requests = new();
    private readonly Task acceptLoop;
    private int accepted;
    private int handshakes;
    private int openStreams;

    private LocalTlsServer()
    {
        _ = SharedCertificate.Value; // made before the first client connects
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>The SHA-1 thumbprint of the server's certificate, for a test client that pins it.</summary>
    public static string Thumbprint => SharedCertificate.Value.Thumbprint;

    /// <summary>Where <c>/redirect</c> sends the client.</summary>
    public Uri? RedirectTarget { get; set; }

    /// <summary>TCP connections accepted, whether or not their handshake completed.</summary>
    public int Accepted => Volatile.Read(ref accepted);

    /// <summary>TLS handshakes that completed.</summary>
    public int Handshakes => Volatile.Read(ref handshakes);

    /// <summary><c>/stream</c> and <c>/icy</c> bodies the server is still sending.</summary>
    public int OpenStreams => Volatile.Read(ref openStreams);

    /// <summary>Every request the server has read, in arrival order.</summary>
    public IReadOnlyList<ServerRequest> Requests => [.. requests];

    public static LocalTlsServer Start() => new();

    /// <summary><c>https://127.0.0.1:port</c> + <paramref name="pathAndQuery"/>.</summary>
    public Uri Url(string pathAndQuery) => new($"https://127.0.0.1:{Port}{pathAndQuery}");

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        await Task.WhenAll([acceptLoop, .. connections.Keys]).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            Interlocked.Increment(ref accepted);
            var task = ServeAsync(client);
            connections.TryAdd(task, 0);
            _ = task.ContinueWith(t => connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        var ct = stopping.Token;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(HandshakeTimeout);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = SharedCertificate.Value }, handshake.Token).ConfigureAwait(false);
            }
            Interlocked.Increment(ref handshakes);
            if (await ServerRequest.ReadAsync(tls, ct).ConfigureAwait(false) is not { } request) return;
            requests.Enqueue(request);
            switch (request.Path)
            {
                case "/stream":
                    await StreamAsync(tls, "HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nTransfer-Encoding: chunked\r\n\r\n", chunked: true, ct).ConfigureAwait(false);
                    break;
                case "/icy":
                    await StreamAsync(tls, "ICY 200 OK\r\nContent-Type: audio/mpeg\r\n\r\n", chunked: false, ct).ConfigureAwait(false);
                    break;
                case "/auth":
                    await WriteAsync(tls, "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"DialShift warm-up\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                    break;
                case "/redirect":
                    await WriteAsync(tls, $"HTTP/1.1 302 Found\r\nLocation: {RedirectTarget}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                    break;
                case "/hang":
                    var buffer = new byte[256];
                    while (await tls.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
                    break;
                case var chain when chain.StartsWith("/chain/", StringComparison.Ordinal) && int.TryParse(chain["/chain/".Length..], out var left) && left > 0:
                    await WriteAsync(tls, $"HTTP/1.1 302 Found\r\nLocation: {Url($"/chain/{left - 1}")}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                    break;
                case "/chain/0":
                    await WriteAsync(tls, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(tls, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            // A client that refused the certificate, went away, or the server is stopping.
        }
    }

    /// <summary>Sends <paramref name="head"/>, then 4 KiB every 50 ms until a write fails (the client closed) or the server stops.</summary>
    private async Task StreamAsync(SslStream tls, string head, bool chunked, CancellationToken ct)
    {
        Interlocked.Increment(ref openStreams);
        try
        {
            await WriteAsync(tls, head, ct).ConfigureAwait(false);
            var prefix = Encoding.ASCII.GetBytes($"{Chunk.Length:x}\r\n");
            var suffix = "\r\n"u8.ToArray();
            while (true)
            {
                if (chunked) await tls.WriteAsync(prefix, ct).ConfigureAwait(false);
                await tls.WriteAsync(Chunk, ct).ConfigureAwait(false);
                if (chunked) await tls.WriteAsync(suffix, ct).ConfigureAwait(false);
                await tls.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(PaceInterval, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref openStreams);
        }
    }

    private static async Task WriteAsync(SslStream tls, string text, CancellationToken ct)
    {
        await tls.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
        await tls.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>RSA 2048, CN=localhost with the SANs localhost and 127.0.0.1, server authentication, valid for a week.</summary>
    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        // SslStream on Windows cannot use the ephemeral key of a freshly created certificate: load it back from PKCS#12.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null);
    }
}
