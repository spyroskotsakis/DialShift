using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Avalonia.Media.Imaging;
using DialShift.App.Services;
using DialShift.Tests.Ui;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// <see cref="CatalogLogoLoader"/> with a fake <see cref="HttpMessageHandler"/> (docs/catalog-contracts.md §4.3; D66,
/// D80), the loader checks of CAT-10: which URLs are requested, the User-Agent, failures cached, the size cap, the
/// timeout, the concurrency cap, one download per URL, caller cancellation and abandonment, the LRU cache, Dispose, and
/// decoding (on Avalonia's headless platform, where Skia is initialized). No network: every response is the handler's.
/// </summary>
/// <remarks>
/// <see cref="CatalogLogoLoader.Timeout"/> is a static 5 s with no seam, so the one timeout scenario takes 5 s of real
/// time; it starts first and runs while the other checks do. Every wait is bounded by <see cref="Bound"/>.
/// </remarks>
internal static class CatalogLogoLoaderTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public static async Task RunAsync()
    {
        var timeouts = TimeoutsAsync();
        InvalidUrls();
        await HttpFailuresAsync();
        await OversizeAsync();
        await OffCallerThreadAsync();
        await DedupeAsync();
        await ConcurrencyCapAsync();
        await CallerCancellationAsync();
        await CacheCapacityAsync();
        await DisposeAsync();
        await Headless.RunAsync(DecodingAsync);
        CheckTimeouts(await timeouts.WaitAsync(Bound));
    }

    // ─── Fakes ───

    /// <summary>Answers with <c>respond</c>; records every request, the peak number in flight, cancellations and disposal.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly Lock gate = new();
        private readonly List<HttpRequestMessage> requests = [];
        private int active, peak, cancelled, disposed;

        public IReadOnlyList<HttpRequestMessage> Requests { get { lock (gate) return [.. requests]; } }
        public int Count(string url) { lock (gate) return requests.Count(r => r.RequestUri!.OriginalString == url); }
        public int Active => Volatile.Read(ref active);
        public int Peak => Volatile.Read(ref peak);
        /// <summary>Requests that ended because their token was cancelled.</summary>
        public int Cancelled => Volatile.Read(ref cancelled);
        public bool Disposed => Volatile.Read(ref disposed) > 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                requests.Add(request);
                peak = Math.Max(peak, ++active);
            }
            try { return await respond(request, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
            finally { Interlocked.Decrement(ref active); }
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref disposed);
            base.Dispose(disposing);
        }
    }

    /// <summary>A body without a length that never ends; counts what is read.</summary>
    private sealed class EndlessStream : Stream
    {
        private long read;
        public long BytesRead => Interlocked.Read(ref read);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fill(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Fill(Span<byte> span)
        {
            span.Fill((byte)'A');
            Interlocked.Add(ref read, span.Length);
            return span.Length;
        }
    }

    /// <summary>A body whose every read waits until cancelled; records the cancellation.</summary>
    private sealed class StalledStream : Stream
    {
        private int cancelled;
        public bool WasCancelled => Volatile.Read(ref cancelled) > 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref cancelled, 1);
                throw;
            }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new ByteArrayContent([]) };

    private static HttpResponseMessage Body(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static Task<HttpResponseMessage> NotFound() => Task.FromResult(Status(HttpStatusCode.NotFound));

    private static string Url(string path) => "https://logo.example.org/" + path;

    /// <summary>Polls <paramref name="condition"/> until it holds; false after <see cref="Bound"/>.</summary>
    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Bound) return false;
            await Task.Delay(5);
        }
        return true;
    }

    /// <summary>The task completed successfully with a null logo.</summary>
    private static bool IsNull(Task<Bitmap?> task) => task.IsCompletedSuccessfully && task.Result is null;

    // ─── Checks ───

    private static void InvalidUrls()
    {
        var handler = new FakeHandler((_, _) => NotFound());
        using var loader = new CatalogLogoLoader(handler);
        string?[] invalid =
            [null, "", "   ", "logo.png", "/logos/a.png", "//logo.example.org/a.png", "ftp://logo.example.org/a.png", "file:///etc/passwd",
             "javascript:alert(1)", "data:image/png;base64,iVBORw0KGgo=", "mailto:logo@example.org", "http://", "https://"];
        var results = invalid.Select(url => loader.LoadAsync(url!, CancellationToken.None)).ToList();
        Check("CAT-10 a URL that is not absolute http(s) with a host (null, \"\", relative, protocol-relative, ftp, file, javascript, data, mailto, " +
              "no host) is never requested and returns a completed null at once",
            results.All(IsNull) && handler.Requests.Count == 0);
        var cancelled = loader.LoadAsync(Url("pre-cancelled.png"), new CancellationToken(canceled: true));
        Check("CAT-10 a token already cancelled at the call returns a completed null without a request", IsNull(cancelled) && handler.Requests.Count == 0);
    }

    private static async Task HttpFailuresAsync()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/404.png" => NotFound(),
            "/500.png" => Task.FromResult(Status(HttpStatusCode.InternalServerError)),
            "/throws.png" => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")),
            "/html.png" => Task.FromResult(Body(Encoding.UTF8.GetBytes("<html><body>Not an image</body></html>"))),
            "/empty.png" => Task.FromResult(Body([])),
            _ => NotFound(),
        });
        using var loader = new CatalogLogoLoader(handler);
        string[] urls = [Url("404.png"), Url("500.png"), Url("throws.png"), Url("html.png"), Url("empty.png")];
        var first = await Task.WhenAll(urls.Select(u => loader.LoadAsync(u, CancellationToken.None))).WaitAsync(Bound);
        var second = urls.Select(u => loader.LoadAsync(u, CancellationToken.None)).ToList();
        Check("CAT-10 404, 500, a network exception, an HTML body and an empty body each give null, never an exception",
            first.All(b => b is null));
        Check("CAT-10 a failure is cached: the second call for each of the five URLs returns a completed null without a new request (one request each)",
            second.All(IsNull) && urls.All(u => handler.Count(u) == 1) && handler.Requests.Count == urls.Length);
        var request = handler.Requests[0];
        var agent = request.Headers.UserAgent.ToString();
        var version = typeof(CatalogLogoLoader).Assembly.GetName().Version?.ToString(3) ?? "0";
        Check($"CAT-10 every request is a GET for the URL as given with User-Agent: DialShift/{version} (the App assembly version, three parts)",
            handler.Requests.All(r => r.Method == HttpMethod.Get && r.Headers.UserAgent.ToString() == $"DialShift/{version}")
            && urls.Contains(request.RequestUri!.OriginalString) && agent == $"DialShift/{version}");
    }

    private static async Task OversizeAsync()
    {
        var declared = new EndlessStream();
        var endless = new EndlessStream();
        var handler = new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/declared.png")
            {
                var content = new StreamContent(declared);
                content.Headers.ContentLength = CatalogLogoLoader.MaxBytes + 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });
        });
        using var loader = new CatalogLogoLoader(handler);
        var byHeader = await loader.LoadAsync(Url("declared.png"), CancellationToken.None).WaitAsync(Bound);
        Check("CAT-10 a Content-Length of MaxBytes + 1 gives null without reading the body", byHeader is null && declared.BytesRead == 0);
        var byBody = await loader.LoadAsync(Url("endless.png"), CancellationToken.None).WaitAsync(Bound);
        Check($"CAT-10 a body without a length that never ends gives null, abandoned at the first byte over MaxBytes (read {endless.BytesRead} of at most {CatalogLogoLoader.MaxBytes + 1})",
            byBody is null && endless.BytesRead == CatalogLogoLoader.MaxBytes + 1);
        Check("CAT-10 an oversize result is cached (one request per URL)",
            IsNull(loader.LoadAsync(Url("endless.png"), CancellationToken.None)) && handler.Requests.Count == 2);
    }

    private static async Task OffCallerThreadAsync()
    {
        using var release = new ManualResetEventSlim();
        // A handler that blocks its thread: were the request sent on the caller's thread, LoadAsync could not return.
        var handler = new FakeHandler((_, _) =>
        {
            release.Wait(Bound);
            return NotFound();
        });
        using var loader = new CatalogLogoLoader(handler);
        var clock = Stopwatch.StartNew();
        var pending = loader.LoadAsync(Url("blocking.png"), CancellationToken.None);
        var returnedAt = clock.Elapsed;
        var started = await EventuallyAsync(() => handler.Requests.Count == 1);
        var stillPending = !pending.IsCompleted;
        release.Set();
        var result = await pending.WaitAsync(Bound);
        Check("CAT-10 LoadAsync returns before the request runs: a handler blocking its thread leaves the call pending (network work on the thread pool)",
            returnedAt < TimeSpan.FromSeconds(5) && started && stillPending && result is null);
    }

    private static async Task DedupeAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Status(HttpStatusCode.NotFound);
        });
        using var loader = new CatalogLogoLoader(handler);
        var url = Url("shared.png");
        var callers = Enumerable.Range(0, 5).Select(_ => loader.LoadAsync(url, CancellationToken.None)).ToList();
        var oneRequest = await EventuallyAsync(() => handler.Requests.Count == 1);
        release.SetResult();
        var results = await Task.WhenAll(callers).WaitAsync(Bound);
        var later = loader.LoadAsync(url, CancellationToken.None);
        Check("CAT-10 five concurrent calls for one URL share one download (one request) and all get its result; a later call is a cache hit",
            oneRequest && results.All(r => r is null) && IsNull(later) && handler.Requests.Count == 1);
    }

    private static async Task ConcurrencyCapAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Status(HttpStatusCode.NotFound);
        });
        using var loader = new CatalogLogoLoader(handler);
        var loads = Enumerable.Range(0, 10).Select(i => loader.LoadAsync(Url($"cap-{i}.png"), CancellationToken.None)).ToList();
        var full = await EventuallyAsync(() => handler.Active == CatalogLogoLoader.MaxConcurrentDownloads);
        await Task.Delay(100); // a fifth request, were the cap missing, would have started by now
        var heldAtCap = handler.Active == CatalogLogoLoader.MaxConcurrentDownloads && handler.Requests.Count == CatalogLogoLoader.MaxConcurrentDownloads;
        release.SetResult();
        var results = await Task.WhenAll(loads).WaitAsync(Bound);
        Check("CAT-10 at most MaxConcurrentDownloads (4) downloads run at once: of 10 distinct URLs, 4 are requested while those hang; " +
              "the other 6 follow, never more than 4 in flight",
            full && heldAtCap && results.All(r => r is null) && handler.Requests.Count == 10 && handler.Peak == CatalogLogoLoader.MaxConcurrentDownloads);
    }

    private static async Task CallerCancellationAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHandler? self = null;
        var handler = self = new FakeHandler(async (request, ct) =>
        {
            // The first request of each URL hangs until released; later ones answer 404 at once.
            if (self!.Count(request.RequestUri!.OriginalString) == 1) await release.Task.WaitAsync(ct);
            return Status(HttpStatusCode.NotFound);
        });
        using var loader = new CatalogLogoLoader(handler);

        var alone = Url("alone.png");
        using (var cts = new CancellationTokenSource())
        {
            var load = loader.LoadAsync(alone, cts.Token);
            var started = await EventuallyAsync(() => handler.Requests.Count == 1);
            cts.Cancel();
            var result = await load.WaitAsync(Bound);
            var abandoned = await EventuallyAsync(() => handler.Cancelled == 1);
            Check("CAT-10 when the only caller cancels, it gets null (not an exception) and the request itself is cancelled",
                started && result is null && load.IsCompletedSuccessfully && abandoned);
        }
        var again = await loader.LoadAsync(alone, CancellationToken.None).WaitAsync(Bound);
        Check("CAT-10 an abandoned download is not cached: the next call for the URL requests again (and that result is cached)",
            again is null && handler.Count(alone) == 2 && IsNull(loader.LoadAsync(alone, CancellationToken.None)) && handler.Count(alone) == 2);

        var shared = Url("shared-cancel.png");
        using (var cts = new CancellationTokenSource())
        {
            var leaving = loader.LoadAsync(shared, cts.Token);
            var staying = loader.LoadAsync(shared, CancellationToken.None);
            var started = await EventuallyAsync(() => handler.Count(shared) == 1);
            cts.Cancel();
            var left = await leaving.WaitAsync(Bound);
            await Task.Delay(50); // an abandonment, were it wrongly triggered, would have cancelled the request by now
            var notAbandoned = handler.Cancelled == 1 && !staying.IsCompleted;
            release.TrySetResult();
            var stayed = await staying.WaitAsync(Bound);
            Check("CAT-10 when one of two callers cancels, only its wait ends (null); the download continues for the other, one request in all",
                started && left is null && notAbandoned && stayed is null && staying.IsCompletedSuccessfully && handler.Count(shared) == 1);
        }
    }

    private static async Task CacheCapacityAsync()
    {
        var handler = new FakeHandler((_, _) => NotFound());
        using var loader = new CatalogLogoLoader(handler);
        async Task Load(int i) => await loader.LoadAsync(Url($"lru-{i}.png"), CancellationToken.None).WaitAsync(Bound);
        for (var i = 0; i < CatalogLogoLoader.CacheCapacity; i++) await Load(i);
        var filled = handler.Requests.Count;
        await Load(0);                                    // a hit: lru-0 becomes the most recent
        var afterHit = handler.Requests.Count;
        await Load(CatalogLogoLoader.CacheCapacity);      // one more URL evicts the least recent, lru-1
        await Load(0);
        var zeroKept = handler.Requests.Count;
        await Load(1);
        Check("CAT-10 the cache holds CacheCapacity (128) URLs, least recently used out first: after 128 misses and a hit on the first, " +
              "a 129th URL evicts the second (requested again), not the first (still a hit)",
            filled == CatalogLogoLoader.CacheCapacity && afterHit == filled && zeroKept == filled + 1 && handler.Requests.Count == filled + 2);
    }

    private static async Task DisposeAsync()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return Status(HttpStatusCode.NotFound);
        });
        var loader = new CatalogLogoLoader(handler);
        var pending = loader.LoadAsync(Url("in-flight.png"), CancellationToken.None);
        var started = await EventuallyAsync(() => handler.Requests.Count == 1);
        loader.Dispose();
        var result = await pending.WaitAsync(Bound);
        var cancelled = await EventuallyAsync(() => handler.Cancelled == 1);
        var after = loader.LoadAsync(Url("after-dispose.png"), CancellationToken.None);
        Check("CAT-10 Dispose cancels a download in flight (its caller gets null) and disposes the handler; a later LoadAsync returns a completed null " +
              "without a request; a second Dispose is harmless",
            started && result is null && pending.IsCompletedSuccessfully && cancelled && handler.Disposed && IsNull(after)
            && handler.Requests.Count == 1 && NoThrow(loader.Dispose));
    }

    // ─── The one real-time scenario: the 5 s attempt timeout ───

    private sealed record TimeoutOutcome(Bitmap?[] First, bool CachedNull, TimeSpan Elapsed, int Requests, int HandlerCancelled, bool BodyCancelled);

    /// <summary>A request whose headers never come and one whose body stalls, together on one loader. Gathers only; the
    /// checks run in <see cref="CheckTimeouts"/>, so their output stays in order.</summary>
    private static async Task<TimeoutOutcome> TimeoutsAsync()
    {
        var stalled = new StalledStream();
        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/no-headers.png") await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) };
        });
        using var loader = new CatalogLogoLoader(handler);
        string[] urls = [Url("no-headers.png"), Url("stalled-body.png")];
        var clock = Stopwatch.StartNew();
        var first = await Task.WhenAll(urls.Select(u => loader.LoadAsync(u, CancellationToken.None))).WaitAsync(Bound);
        var elapsed = clock.Elapsed;
        var cached = urls.Select(u => loader.LoadAsync(u, CancellationToken.None)).All(IsNull);
        return new TimeoutOutcome(first, cached, elapsed, handler.Requests.Count, handler.Cancelled, stalled.WasCancelled);
    }

    private static void CheckTimeouts(TimeoutOutcome outcome)
    {
        Console.WriteLine($"  timeout scenario: both nulls after {outcome.Elapsed.TotalSeconds:0.00} s");
        Check("CAT-10 each attempt is cancelled after Timeout (5 s): a request whose headers never come and a body that stalls both give null, " +
              "no sooner than the timeout, and both the request and the body read see the cancellation",
            outcome.First.All(b => b is null) && outcome.Elapsed >= CatalogLogoLoader.Timeout - TimeSpan.FromMilliseconds(50)
            && outcome.HandlerCancelled == 1 && outcome.BodyCancelled);
        Check("CAT-10 a timed-out logo is cached as null (two URLs, two requests in all)", outcome.CachedNull && outcome.Requests == 2);
    }

    // ─── Decoding (headless: Bitmap needs Skia) ───

    /// <summary>A valid RGB PNG of <paramref name="width"/> × <paramref name="height"/>, padded with a tEXt chunk to
    /// exactly <paramref name="totalBytes"/> bytes when given.</summary>
    private static byte[] Png(int width, int height, int totalBytes = 0)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour RGB
        Chunk(png, "IHDR", header);
        var raw = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * (width * 3 + 1) + 1 + x * 3;
                (raw[at], raw[at + 1], raw[at + 2]) = ((byte)(x * 2), (byte)(y * 2), 0x80);
            }
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
            Chunk(png, "IDAT", compressed.ToArray());
        }
        if (totalBytes > 0)
        {
            // Chunk overhead is 12 bytes (length, type, CRC); IEND is one more empty chunk.
            var text = totalBytes - (int)png.Length - 12 - 12;
            if (text < 8) throw new ArgumentOutOfRangeException(nameof(totalBytes));
            Chunk(png, "tEXt", [.. "Comment\0"u8, .. Enumerable.Repeat((byte)'x', text - 8)]);
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    /// <summary>The PNG (ISO 3309) CRC-32 of <paramref name="bytes"/>.</summary>
    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static async Task DecodingAsync()
    {
        var logo = Png(128, 96);
        var exact = Png(128, 96, CatalogLogoLoader.MaxBytes);
        var over = Png(128, 96, CatalogLogoLoader.MaxBytes + 1);
        var bodies = new Dictionary<string, byte[]>
        {
            ["/logo.png"] = logo, ["/exact.png"] = exact, ["/over.png"] = over,
            ["/logo.svg"] = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\"><rect width=\"64\" height=\"64\"/></svg>"),
            ["/corrupt.png"] = [.. logo.AsSpan(0, 8), .. Enumerable.Repeat((byte)0xA5, 256)],
        };
        var handler = new FakeHandler((request, _) => Task.FromResult(Body(bodies[request.RequestUri!.AbsolutePath])));
        using var loader = new CatalogLogoLoader(handler);

        async Task<Bitmap?> Load(string path)
        {
            var task = loader.LoadAsync(Url(path), CancellationToken.None);
            if (!await Headless.CompletesAsync(task)) throw new TimeoutException($"LoadAsync({path}) did not complete.");
            return await task;
        }

        var first = await Load("logo.png");
        Check("CAT-10 a 128 × 96 PNG decodes to DecodeSize (64) pixels wide, aspect kept (64 × 48)",
            first is { PixelSize.Width: CatalogLogoLoader.DecodeSize, PixelSize.Height: 48 });
        var hit = loader.LoadAsync(Url("logo.png"), CancellationToken.None);
        Check("CAT-10 a decoded logo is cached: the next call returns a completed task with the same Bitmap, without a request",
            hit.IsCompletedSuccessfully && ReferenceEquals(hit.Result, first) && handler.Count(Url("logo.png")) == 1);
        Check($"CAT-10 the size cap is inclusive: a valid PNG of exactly MaxBytes ({CatalogLogoLoader.MaxBytes} bytes) decodes; one byte more gives null",
            exact.Length == CatalogLogoLoader.MaxBytes && over.Length == CatalogLogoLoader.MaxBytes + 1
            && await Load("exact.png") is { PixelSize.Width: CatalogLogoLoader.DecodeSize } && await Load("over.png") is null);
        Check("CAT-10 bytes Skia cannot decode (an SVG; a PNG signature followed by garbage) give null, never an exception",
            await Load("logo.svg") is null && await Load("corrupt.png") is null);
    }
}
