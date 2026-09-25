using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DialShift.Tests.TestServers;

/// <summary>
/// In-process HTTP/ICY server on 127.0.0.1 for the real-engine tests: deterministic stand-ins for the §11 corpus transport
/// cases, with no network access and no copyrighted audio. Audio is a synthesized 440 Hz tone as 16 kHz mono 16-bit WAV,
/// which every LibVLC build demuxes and decodes. Paths (the query string is ignored):
/// <list type="bullet">
/// <item><c>/live.wav</c>: endless stream, paced at real time after a 3 s burst (HTTP/1.0, no length, like Icecast).</item>
/// <item><c>/icy.wav</c>: the same as a Shoutcast v1 server (<c>ICY 200 OK</c>), with ICY metadata (<c>icy-metaint</c>,
/// <see cref="IcyTitle"/> as StreamTitle) when the request asks for it.</item>
/// <item><c>/ends.wav</c>: 6 s of audio (3 s burst, 3 s paced), then the server closes the connection (a live stream that ends).</item>
/// <item><c>/status/404</c>, <c>/status/403</c>, <c>/status/500</c>: that status with a text body.</item>
/// <item><c>/auth/live.wav</c>: 401 with a Basic challenge unless the request carries <see cref="AuthUser"/>:<see cref="AuthPassword"/>.</item>
/// <item><c>/portal.html</c>: 200 text/html (a captive-portal-style page).</item>
/// <item><c>/redirect</c>: 302 to <c>/live.wav</c>.</item>
/// <item><c>/hang</c>: reads the request and never answers; the socket stays open until the client closes it.</item>
/// <item><c>/station.pls</c>, <c>/station.m3u</c>: playlist files whose single entry is <c>/live.wav</c>.</item>
/// <item>anything else: 404.</item>
/// </list>
/// <see cref="OpenConnections"/> counts client connections per path that the server still holds (streams and /hang), so
/// tests can prove a stopped player really released its stream, and that rapid switching leaves exactly one.
/// </summary>
public sealed class LocalMediaServer : IAsyncDisposable
{
    public const string IcyTitle = "DialShift Test Title";
    public const string AuthUser = "listener";
    public const string AuthPassword = "secret-pass-7f3a";

    private const int SampleRate = 16000;
    private const int BytesPerSecond = SampleRate * 2;
    private const int IcyMetaInterval = 8000;
    private static readonly TimeSpan PaceInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One second of a 440 Hz tone at -20 dBFS: 440 whole cycles, so consecutive copies join seamlessly.</summary>
    private static readonly byte[] ToneSecond = CreateToneSecond();

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Task, byte> connections = new();
    private readonly ConcurrentDictionary<string, int> open = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ServerRequest> requests = new();
    private readonly Task acceptLoop;

    private LocalMediaServer()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>Every request the server has read, in arrival order.</summary>
    public IReadOnlyList<ServerRequest> Requests => [.. requests];

    public static LocalMediaServer Start() => new();

    /// <summary><c>http://127.0.0.1:port</c> + <paramref name="pathAndQuery"/>.</summary>
    public Uri Url(string pathAndQuery) => new($"http://127.0.0.1:{Port}{pathAndQuery}");

    /// <summary>Connections for <paramref name="path"/> the server still holds open (stream paths and /hang).</summary>
    public int OpenConnections(string path) => open.TryGetValue(path, out var count) ? count : 0;

    /// <summary>Open stream and /hang connections over all paths.</summary>
    public int OpenConnectionsTotal => open.Values.Sum();

    /// <summary>"open connections: /live.wav=1" style summary for check names.</summary>
    public string OpenConnectionsText =>
        "open connections: " + (open.Where(p => p.Value != 0).Select(p => $"{p.Key}={p.Value}").ToList() is { Count: > 0 } list ? string.Join(", ", list) : "none");

    /// <summary>A loopback port with no listener (connection refused), found by binding and releasing one.</summary>
    public static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

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
            var task = ServeAsync(client);
            connections.TryAdd(task, 0);
            _ = task.ContinueWith(t => connections.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            if (await ReadRequestAsync(stream).ConfigureAwait(false) is not { } request) return;
            requests.Enqueue(request);
            await RouteAsync(request, stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away (a stopped player) or the server is shutting down.
        }
    }

    private async Task RouteAsync(ServerRequest request, NetworkStream stream)
    {
        var ct = stopping.Token;
        switch (request.Path)
        {
            case "/live.wav":
                await StreamAsync(request.Path, stream, icy: false, duration: null, ct).ConfigureAwait(false);
                break;
            case "/icy.wav":
                await StreamAsync(request.Path, stream, icy: request.Header("Icy-MetaData") == "1", duration: null, ct, shoutcast: true).ConfigureAwait(false);
                break;
            case "/ends.wav":
                await StreamAsync(request.Path, stream, icy: false, duration: TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
                break;
            case "/auth/live.wav":
                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AuthUser}:{AuthPassword}"));
                if (request.Header("Authorization") == expected)
                    await StreamAsync(request.Path, stream, icy: false, duration: null, ct).ConfigureAwait(false);
                else
                    await WriteResponseAsync(stream, "401 Unauthorized", "text/plain", "Authorization required.", ct, "WWW-Authenticate: Basic realm=\"DialShift test\"").ConfigureAwait(false);
                break;
            case "/status/403":
                await WriteResponseAsync(stream, "403 Forbidden", "text/plain", "Forbidden.", ct).ConfigureAwait(false);
                break;
            case "/status/500":
                await WriteResponseAsync(stream, "500 Internal Server Error", "text/plain", "Server error.", ct).ConfigureAwait(false);
                break;
            case "/portal.html":
                await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8",
                    "<!DOCTYPE html><html><head><title>Sign in to the network</title></head><body><form><input name=\"email\"><button>Connect</button></form></body></html>", ct).ConfigureAwait(false);
                break;
            case "/redirect":
                await WriteResponseAsync(stream, "302 Found", "text/plain", "", ct, $"Location: {Url("/live.wav")}").ConfigureAwait(false);
                break;
            case "/station.pls":
                await WriteResponseAsync(stream, "200 OK", "audio/x-scpls", $"[playlist]\r\nNumberOfEntries=1\r\nFile1={Url("/live.wav")}\r\nTitle1=DialShift test\r\nLength1=-1\r\nVersion=2\r\n", ct).ConfigureAwait(false);
                break;
            case "/station.m3u":
                await WriteResponseAsync(stream, "200 OK", "audio/x-mpegurl", $"#EXTM3U\r\n#EXTINF:-1,DialShift test\r\n{Url("/live.wav")}\r\n", ct).ConfigureAwait(false);
                break;
            case "/hang":
                await HoldAsync(request.Path, stream, ct).ConfigureAwait(false);
                break;
            default:
                await WriteResponseAsync(stream, "404 Not Found", "text/plain", "Not found.", ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// WAV header, a 3 s burst, then real-time pacing. With <paramref name="duration"/> the server closes after that much
    /// audio. <paramref name="shoutcast"/> answers with the Shoutcast v1 status line <c>ICY 200 OK</c>: LibVLC 3's newer
    /// HTTP module rejects it and LibVLC retries with its legacy module, the only one that sends <c>Icy-MetaData: 1</c>
    /// (the same path a real Shoutcast v1 station takes).
    /// </summary>
    private async Task StreamAsync(string path, NetworkStream stream, bool icy, TimeSpan? duration, CancellationToken ct, bool shoutcast = false)
    {
        using var tracked = Track(path);
        var head = new StringBuilder(shoutcast ? "ICY 200 OK" : "HTTP/1.0 200 OK")
            .Append("\r\nContent-Type: audio/wav\r\nCache-Control: no-cache\r\nConnection: close\r\n");
        if (icy) head.Append($"icy-name: DialShift test\r\nicy-metaint: {IcyMetaInterval}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);

        var writer = new IcyWriter(stream, icy ? IcyMetaInterval : 0);
        await writer.WriteAsync(WavHeader(duration), ct).ConfigureAwait(false);
        var total = duration is { } d ? (long)(d.TotalSeconds * BytesPerSecond) : long.MaxValue;
        var sent = 0L;
        var position = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const long burst = 3L * BytesPerSecond;
        while (sent < total)
        {
            // Stay 3 s ahead of real time: enough for LibVLC's 1.5 s network cache, bounded like a live source.
            var due = Math.Min(total, burst + (long)(clock.Elapsed.TotalSeconds * BytesPerSecond));
            while (sent < due)
            {
                var count = (int)Math.Min(due - sent, ToneSecond.Length - position);
                await writer.WriteAsync(ToneSecond.AsMemory(position, count), ct).ConfigureAwait(false);
                sent += count;
                position = (position + count) % ToneSecond.Length;
            }
            if (sent < total) await Task.Delay(PaceInterval, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Never answers; returns when the client closes the connection or the server stops.</summary>
    private async Task HoldAsync(string path, NetworkStream stream, CancellationToken ct)
    {
        using var tracked = Track(path);
        var buffer = new byte[256];
        while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, string body, CancellationToken ct, params string[] headers)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n");
        foreach (var header in headers) head.Append(header).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<ServerRequest?> ReadRequestAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        timeout.CancelAfter(RequestReadTimeout);
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0) return null;
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var lines = text[..end].Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;
            var target = parts[1];
            var query = target.IndexOf('?', StringComparison.Ordinal);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            return new ServerRequest(parts[0], query < 0 ? target : target[..query], headers);
        }
        return null;
    }

    private Tracked Track(string path)
    {
        open.AddOrUpdate(path, 1, static (_, count) => count + 1);
        return new Tracked(() => open.AddOrUpdate(path, 0, static (_, count) => count - 1));
    }

    /// <summary>RIFF/WAVE header for 16 kHz mono PCM16. Endless streams declare a data size of ~18 h.</summary>
    private static byte[] WavHeader(TimeSpan? duration)
    {
        var dataSize = duration is { } d ? (uint)(d.TotalSeconds * BytesPerSecond) : 0x7FFF0000u;
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), dataSize + 36);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2); // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16); // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), dataSize);
        return header;
    }

    private static byte[] CreateToneSecond()
    {
        var bytes = new byte[BytesPerSecond];
        for (var i = 0; i < SampleRate; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / SampleRate) * 0.1 * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), sample);
        }
        return bytes;
    }

    /// <summary>Writes the audio body, inserting an ICY metadata block after every <c>metaInterval</c> bytes when it is non-zero.</summary>
    private sealed class IcyWriter(NetworkStream stream, int metaInterval)
    {
        private static readonly byte[] MetadataBlock = CreateMetadataBlock();
        private readonly int interval = metaInterval;
        private int untilMetadata = metaInterval;

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (interval == 0)
            {
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                return;
            }
            while (!data.IsEmpty)
            {
                var count = Math.Min(untilMetadata, data.Length);
                await stream.WriteAsync(data[..count], ct).ConfigureAwait(false);
                data = data[count..];
                untilMetadata -= count;
                if (untilMetadata > 0) continue;
                await stream.WriteAsync(MetadataBlock, ct).ConfigureAwait(false);
                untilMetadata = interval;
            }
        }

        private static byte[] CreateMetadataBlock()
        {
            var text = Encoding.UTF8.GetBytes($"StreamTitle='{IcyTitle}';");
            var blocks = (text.Length + 15) / 16;
            var block = new byte[1 + blocks * 16];
            block[0] = (byte)blocks;
            text.CopyTo(block, 1);
            return block;
        }
    }

    private sealed class Tracked(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}

/// <summary>A request line and headers as the server read them.</summary>
public sealed record ServerRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
