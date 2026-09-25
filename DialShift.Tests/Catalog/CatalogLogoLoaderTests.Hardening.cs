using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using Avalonia.Media.Imaging;
using DialShift.App.Services;
using static DialShift.Tests.TestHarness;

namespace DialShift.Tests.Catalog;

/// <summary>
/// The D81 and D82 checks of CAT-10 (docs/catalog-contracts.md §4.3 and §8 CAT-10 (a)–(g)): pixel bombs and
/// <see cref="CatalogLogoLoader.MaxPixels"/>, the longest side <see cref="CatalogLogoLoader.DecodeSize"/>, refused hosts,
/// redirects, the join-after-abandon race, and the single decode gate.
/// </summary>
/// <remarks>
/// <para>Every image is generated here (<see cref="Png"/>, <see cref="BlankPng"/>, <see cref="TruncatedBomb"/>); no
/// binary is checked in and nothing touches the network.</para>
/// <para><b>Memory.</b> The two working-set checks sample <see cref="Process.WorkingSet64"/> every millisecond on a
/// background thread (<see cref="WorkingSetSampler"/>; <see cref="Process.PeakWorkingSet64"/> reads 0 on macOS) and assert
/// generous bounds, 128 MiB for the bombs and 256 MiB for four 4096 × 4096 logos, far above what one decode at a time
/// retains (about 90 MiB) and far below the failures they guard against (2.4 GiB before D81; about 340 MiB with four
/// concurrent decodes). The four large logos load first, before any other large decode in the process, so an allocator
/// that kept an earlier decode's memory cannot hide a regression.</para>
/// <para><b>The decode gate</b> is a private static <see cref="SemaphoreSlim"/> with no seam. <see cref="DecodeGate"/>
/// reaches it by reflection so the test can hold the one slot itself: serialization and cancellation at the gate are then
/// observed deterministically, with no timing race. A rename fails the suite with a message naming the field.</para>
/// </remarks>
internal static partial class CatalogLogoLoaderTests
{
    private const long MiB = 1024 * 1024;

    // ─── Generated images and helpers ───

    /// <summary>A greyscale PNG, every pixel black, written row by row: 4096 × 4096 is about 17 KiB of body, and no raw
    /// pixel buffer is held while it is built.</summary>
    private static byte[] BlankPng(int width, int height)
    {
        using var png = new MemoryStream();
        Header(png, width, height, colourType: 0);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[width + 1]; // filter type 0, then black pixels
                for (var y = 0; y < height; y++) zlib.Write(row);
            }
            Chunk(png, "IDAT", compressed.ToArray());
        }
        Chunk(png, "IEND", []);
        if (png.Length > CatalogLogoLoader.MaxBytes) throw new InvalidOperationException($"BlankPng({width}, {height}) is over MaxBytes.");
        return png.ToArray();
    }

    /// <summary>The pixel bomb: a header declaring <paramref name="width"/> × <paramref name="height"/> truecolour pixels,
    /// then the first 32 bytes of their compressed data, and nothing more (no IEND).</summary>
    private static byte[] TruncatedBomb(int width, int height)
    {
        using var png = new MemoryStream();
        Header(png, width, height, colourType: 2);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(new byte[64 * 1024]);
        Chunk(png, "IDAT", compressed.ToArray()[..32]);
        return png.ToArray();
    }

    private static bool Is(Bitmap? logo, int width, int height) => logo is { } l && l.PixelSize.Width == width && l.PixelSize.Height == height;

    private static string Describe(Bitmap? logo) => logo is null ? "null" : $"{logo.PixelSize.Width} × {logo.PixelSize.Height}";

    /// <summary>True when <paramref name="task"/> completes within <paramref name="limit"/>.</summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan limit) => await Task.WhenAny(task, Task.Delay(limit)) == task;

    /// <summary>The loader's process-wide decode gate (D82 (a)), by reflection: there is no seam.</summary>
    private static SemaphoreSlim DecodeGate() =>
        typeof(CatalogLogoLoader).GetField("Decodes", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SemaphoreSlim
        ?? throw new InvalidOperationException("CatalogLogoLoader.Decodes, the private static SemaphoreSlim that is the decode gate (D82 (a)), " +
                                               "was not found: the CAT-10 decode-gate checks reach it by reflection; update them with the rename.");

    /// <summary>Samples this process's working set every millisecond on a background thread until <see cref="Stop"/>.</summary>
    private sealed class WorkingSetSampler : IDisposable
    {
        private readonly Process process = Process.GetCurrentProcess();
        private readonly Thread thread;
        private readonly long baseline;
        private long peak;
        private volatile bool stopping;

        private WorkingSetSampler()
        {
            process.Refresh();
            baseline = peak = process.WorkingSet64;
            thread = new Thread(() =>
            {
                while (!stopping)
                {
                    Update();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true, Name = "CAT-10 working-set sampler" };
            thread.Start();
        }

        /// <summary>Collects garbage first, so the baseline holds no managed garbage that a collection during the loads
        /// could release and hide a rise behind.</summary>
        public static WorkingSetSampler Start()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return new WorkingSetSampler();
        }

        /// <summary>The largest rise above the baseline seen, in bytes.</summary>
        public long Stop()
        {
            stopping = true;
            thread.Join();
            Update();
            return peak - baseline;
        }

        public void Dispose()
        {
            stopping = true;
            if (thread.IsAlive) thread.Join();
            process.Dispose();
        }

        private void Update()
        {
            process.Refresh();
            peak = Math.Max(peak, process.WorkingSet64);
        }
    }

    /// <summary>A readable body that records when a read first reaches its end.</summary>
    private sealed class SignallingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private int ended;
        public bool Ended => Volatile.Read(ref ended) > 0;
        public override int Read(byte[] buffer, int offset, int count) => Signal(base.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Signal(base.Read(buffer));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Signal(base.Read(buffer, offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Signal(base.Read(buffer.Span)));

        private int Signal(int read)
        {
            if (read == 0) Interlocked.Exchange(ref ended, 1);
            return read;
        }
    }

    // ─── (a), (b), (f) memory: pixel bounds and the longest side ───

    private static async Task BoundsAsync()
    {
        // Every body is built before any sampling starts.
        var bomb = TruncatedBomb(20000, 20000);
        var tall = Png(1, 20000);
        var atCap = BlankPng(4096, 4096);
        var overCap = BlankPng(4097, 4096);
        var wideAtCap = BlankPng(8192, 2048);
        var whole = Png(128, 96);
        var cut = whole[..(whole.Length / 2)];
        (int Width, int Height, int ScaledWidth, int ScaledHeight)[] shapes =
            [(128, 96, 64, 48), (96, 128, 48, 64), (300, 20, 64, 4), (20, 300, 4, 64), (16, 16, 64, 64)];
        var bodies = new Dictionary<string, byte[]>
        {
            ["/bomb.png"] = bomb, ["/tall.png"] = tall, ["/at-cap.png"] = atCap, ["/over-cap.png"] = overCap,
            ["/wide-at-cap.png"] = wideAtCap, ["/cut.png"] = cut,
        };
        for (var i = 0; i < 4; i++) bodies[$"/large-{i}.png"] = atCap;
        foreach (var s in shapes) bodies[$"/shape-{s.Width}x{s.Height}.png"] = Png(s.Width, s.Height);
        var handler = new FakeHandler((request, _) => Task.FromResult(Body(bodies[request.RequestUri!.AbsolutePath])));
        using var loader = new CatalogLogoLoader(handler);
        Task<Bitmap?> Load(string path) => loader.LoadAsync(Url(path), CancellationToken.None).WaitAsync(Bound);

        await Task.Run(async () =>
        {
            // (f) Four distinct large logos at once, first, before any other large decode in this process.
            Bitmap?[] large;
            long largeRise;
            using (var sampler = WorkingSetSampler.Start())
            {
                large = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Load($"large-{i}.png")));
                largeRise = sampler.Stop();
            }
            Console.WriteLine($"  four 4096 × 4096 logos at once: working set rose at most {largeRise / (double)MiB:0.0} MiB");
            Check("CAT-10 (f) four distinct URLs serving a 4096 × 4096 PNG, started together, all decode to 64 × 64",
                large.All(l => Is(l, 64, 64)) && handler.Requests.Count == 4);
            Check("CAT-10 (f) while those four load, the working set (sampled every millisecond on a background thread) never rises more than " +
                  "256 MiB above its value before the loads (one decode at a time; four at once retained about 340 MiB more)",
                largeRise <= 256 * MiB);

            // (a) The two bombs together, with nothing else running.
            Bitmap?[] bombs;
            long bombRise;
            using (var sampler = WorkingSetSampler.Start())
            {
                bombs = await Task.WhenAll(Load("bomb.png"), Load("tall.png"));
                bombRise = sampler.Stop();
            }
            Console.WriteLine($"  the two bombs: {Describe(bombs[0])} and {Describe(bombs[1])}; working set rose at most {bombRise / (double)MiB:0.0} MiB");
            Check($"CAT-10 (a) the {bomb.Length}-byte truncated PNG whose header declares 20000 × 20000 gives null", bombs[0] is null);
            Check($"CAT-10 (a) the {tall.Length}-byte 1 × 20000 PNG decodes to 1 × 64 (no single-side cap; the longest side becomes DecodeSize)",
                Is(bombs[1], 1, 64));
            Check("CAT-10 (a) while the two bombs load, the working set (sampled every millisecond on a background thread) never rises more than " +
                  "128 MiB above its value before the loads (the pre-D81 loader peaked at about 2.4 GiB)",
                bombRise <= 128 * MiB);

            var atCapLogo = await Load("at-cap.png");
            var overCapLogo = await Load("over-cap.png");
            Check($"CAT-10 (a) a PNG of exactly MaxPixels (4096 × 4096) decodes to 64 × 64 ({Describe(atCapLogo)}); the same image one column wider, " +
                  $"4097 × 4096, gives null ({Describe(overCapLogo)}): the header's pixel count decides",
                Is(atCapLogo, 64, 64) && overCapLogo is null);
            var wide = await Load("wide-at-cap.png");
            Check($"CAT-10 (a) an 8192 × 2048 PNG (exactly MaxPixels, one side twice 4096) decodes to 64 × 16 ({Describe(wide)}): MaxPixels alone " +
                  "bounds the decode, there is no single-side cap",
                Is(wide, 64, 16));
            var cutLogo = await Load("cut.png");
            Check($"CAT-10 (a) a valid 128 × 96 PNG cut in half, inside its pixel data ({cut.Length} of {whole.Length} bytes), gives null: only a complete decode counts",
                cutLogo is null);
            Check("CAT-10 (a) an image over MaxPixels, a truncated bomb and an incomplete decode are cached as null (a completed null, one request each)",
                new[] { "over-cap.png", "bomb.png", "cut.png" }.All(p => IsNull(loader.LoadAsync(Url(p), CancellationToken.None)) && handler.Count(Url(p)) == 1));

            // (b) The longest side.
            var shaped = await Task.WhenAll(shapes.Select(s => Load($"shape-{s.Width}x{s.Height}.png")));
            Console.WriteLine("  longest side: " + string.Join(", ", shapes.Select((s, i) => $"{s.Width} × {s.Height} → {Describe(shaped[i])}")));
            Check("CAT-10 (b) the longest side becomes DecodeSize (64) and the other keeps the aspect ratio, rounded: 128 × 96 → 64 × 48, " +
                  "96 × 128 → 48 × 64, 300 × 20 → 64 × 4, 20 × 300 → 4 × 64, and 16 × 16 is scaled up to 64 × 64",
                shapes.Select((s, i) => Is(shaped[i], s.ScaledWidth, s.ScaledHeight)).All(ok => ok));
        });
    }

    // ─── (c) Refused hosts ───

    private static async Task RefusedHostsAsync()
    {
        var handler = new FakeHandler((_, _) => NotFound());
        using var loader = new CatalogLogoLoader(handler);

        void Refused(string what, string[] urls)
        {
            var first = urls.Select(u => loader.LoadAsync(u, CancellationToken.None)).ToList();
            var second = urls.Select(u => loader.LoadAsync(u, CancellationToken.None)).ToList();
            var wrong = urls.Where((u, i) => !IsNull(first[i]) || !IsNull(second[i]) || handler.Count(u) > 0).ToList();
            if (wrong.Count > 0) Console.WriteLine("  requested, or not a completed null: " + string.Join(", ", wrong));
            Check($"CAT-10 (c) {what}: each gives a completed null at the call with no request, and a second call makes none either (refused, not cached)",
                wrong.Count == 0 && handler.Requests.Count == 0);
        }

        Refused("the name localhost and every *.localhost name, any case, a trailing dot ignored (localhost, LOCALHOST, localhost., " +
                "localhost:8080, https://localhost, foo.localhost, FOO.LOCALHOST., a.b.localhost)",
            ["http://localhost/logo.png", "http://LOCALHOST/logo.png", "http://localhost./logo.png", "http://localhost:8080/logo.png",
             "https://localhost/logo.png", "http://foo.localhost/logo.png", "http://FOO.LOCALHOST./logo.png", "https://a.b.localhost/logo.png"]);
        Refused("literal IPv4 hosts in 0/8, 10/8, 127/8, 169.254/16, 172.16/12 and 192.168/16, at both ends of each range " +
                "(0.0.0.0, 0.1.2.3, 0.255.255.255, 10.0.0.0, 10.255.255.255, 127.0.0.1, 127.255.255.254, 169.254.0.1, 169.254.169.254, " +
                "172.16.0.1, 172.31.255.255, 192.168.0.1, 192.168.255.255), and behind user info (user@127.0.0.1)",
            ["http://0.0.0.0/logo.png", "http://0.1.2.3/logo.png", "http://0.255.255.255/logo.png", "http://10.0.0.0/logo.png",
             "http://10.255.255.255/logo.png", "http://127.0.0.1/logo.png", "http://127.255.255.254/logo.png", "http://169.254.0.1/logo.png",
             "http://169.254.169.254/latest/meta-data/", "http://172.16.0.1/logo.png", "http://172.31.255.255/logo.png",
             "http://192.168.0.1/logo.png", "http://192.168.255.255/logo.png", "http://logo.example.org@127.0.0.1/logo.png"]);
        Refused("shorthand IPv4 spellings, judged as the dotted quad System.Uri makes of them (127.1, 2130706433, 0x7f000001, 0x7f.1, " +
                "0177.0.0.1, 017700000001, 127.000.000.001, http://0/, 10.1, 3232235521)",
            ["http://127.1/logo.png", "http://2130706433/logo.png", "http://0x7f000001/logo.png", "http://0x7f.1/logo.png",
             "http://0177.0.0.1/logo.png", "http://017700000001/logo.png", "http://127.000.000.001/logo.png", "http://0/",
             "http://10.1/logo.png", "http://3232235521/logo.png"]);
        Refused("literal IPv6 hosts: ::1, ::, fc00::/7 (fc00::1, fd00::1, fdff:ffff::1), fe80::/10 (fe80::1, febf::1), fec0::/10 " +
                "(fec0::1, feff::1), with a zone (fe80::1%25en0, ::1%25lo0), IPv4-mapped private (::ffff:127.0.0.1, ::ffff:10.0.0.1, " +
                "::ffff:192.168.0.1, 0:0:0:0:0:ffff:7f00:1) and IPv4-compatible private (::127.0.0.1, ::10.0.0.1)",
            ["http://[::1]/logo.png", "http://[::]/logo.png", "http://[fc00::1]/logo.png", "http://[fd00::1]/logo.png",
             "http://[fdff:ffff::1]/logo.png", "http://[fe80::1]/logo.png", "http://[febf::1]/logo.png", "http://[fec0::1]/logo.png",
             "http://[feff::1]/logo.png", "http://[fe80::1%25en0]/logo.png", "http://[::1%25lo0]/logo.png",
             "http://[::ffff:127.0.0.1]/logo.png", "http://[::ffff:10.0.0.1]/logo.png", "http://[::ffff:192.168.0.1]/logo.png",
             "http://[0:0:0:0:0:ffff:7f00:1]/logo.png", "http://[::127.0.0.1]/logo.png", "http://[::10.0.0.1]/logo.png"]);

        async Task Requested(string what, string[] urls)
        {
            var results = await Task.WhenAll(urls.Select(u => loader.LoadAsync(u, CancellationToken.None))).WaitAsync(Bound);
            var again = urls.Select(u => loader.LoadAsync(u, CancellationToken.None)).ToList();
            var wrong = urls.Where((u, i) => results[i] is not null || !IsNull(again[i]) || handler.Count(u) != 1).ToList();
            if (wrong.Count > 0) Console.WriteLine("  not requested exactly once: " + string.Join(", ", wrong));
            Check($"CAT-10 (c) {what} (once each; the handler's 404 is cached as null)", wrong.Count == 0);
        }

        await Requested("public neighbours of the refused names and ranges (localhost.example.org, localhost.example, mylocalhost, " +
                        "localhostx.org, 172.15.255.255, 172.32.0.1, 192.167.255.255, 192.169.0.1, 169.253.255.255, 169.255.0.1, 9.255.255.255, " +
                        "11.0.0.0, 126.255.255.255, 128.0.0.0, 1.0.0.1, fbff::1, fe7f::1, ::1:0:0:1, 2001:db8::1, ::ffff:8.8.8.8) are requested, " +
                        "so the refused names and ranges are no wider than §4.3",
            ["https://localhost.example.org/logo.png", "http://localhost.example/logo.png", "http://mylocalhost/logo.png",
             "http://localhostx.org/logo.png", "http://172.15.255.255/logo.png", "http://172.32.0.1/logo.png",
             "http://192.167.255.255/logo.png", "http://192.169.0.1/logo.png", "http://169.253.255.255/logo.png",
             "http://169.255.0.1/logo.png", "http://9.255.255.255/logo.png", "http://11.0.0.0/logo.png", "http://126.255.255.255/logo.png",
             "http://128.0.0.0/logo.png", "http://1.0.0.1/logo.png", "http://[fbff::1]/logo.png", "http://[fe7f::1]/logo.png",
             "http://[::1:0:0:1]/logo.png", "http://[2001:db8::1]/logo.png", "http://[::ffff:8.8.8.8]/logo.png"]);
        // The code at f3a9567 judges ::a.b.c.d by its IPv4 part; contracts §4.3, §8 CAT-10 (c) and D82 (b) say it is
        // refused whatever that part is. Pinned as implemented, per this lane's brief; the divergence is reported.
        await Requested("an IPv4-compatible address with a public IPv4 part (::8.8.8.8) is requested: judged by that IPv4 part like " +
                        "::ffff:8.8.8.8, as implemented at f3a9567 (contracts §4.3 and D82 (b) say refused whatever the IPv4 part)",
            ["http://[::8.8.8.8]/logo.png"]);

        // IDNA maps U+3002 and U+FF0E to '.' and fullwidth digits to ASCII, so System.Uri types these hosts Dns while their
        // IdnHost, the host SocketsHttpHandler connects to, is a private dotted quad (measured on macOS: a GET for
        // http://127。0。0。1:port/ reached a listener on 127.0.0.1). Only the localhost names are checked for a Dns host.
        string[] mapped = ["http://127。0。0。1/logo.png", "http://１２７.０.０.１/logo.png",
                           "http://127．0．0．1/logo.png", "http://１０.０.０.１/logo.png",
                           "http://１９２.１６８.０.１/logo.png"];
        var denotePrivate = mapped.All(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.HostNameType == UriHostNameType.Dns
                                            && IPAddress.TryParse(uri.IdnHost, out var address) && address.GetAddressBytes()[0] is 127 or 10 or 192);
        var mappedResults = await Task.WhenAll(mapped.Select(u => loader.LoadAsync(u, CancellationToken.None))).WaitAsync(Bound);
        Check("[quirk] CAT-10 (c) a host that is a loopback or private IPv4 address only after IDNA mapping (ideographic or fullwidth full stops, " +
              "fullwidth digits: 127.0.0.1, 10.0.0.1 and 192.168.0.1 spelled so) IS requested: System.Uri types it Dns and only the localhost " +
              "names are checked, while its IdnHost, which SocketsHttpHandler connects to, is the dotted quad (a D81 defect; flips with the fix)",
            denotePrivate && mappedResults.All(r => r is null) && mapped.All(u => handler.Count(u) == 1));
    }

    // ─── (d) Redirects ───

    private sealed record RedirectRun(Bitmap? Logo, string[] Requested, bool Cached, bool AgentOnEveryHop);

    /// <summary>A response with <paramref name="location"/> as the raw <c>Location</c> header, so the loader sees it as
    /// HttpClient's own header parser reads it from the wire.</summary>
    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var response = Status(code);
        if (!response.Headers.TryAddWithoutValidation("Location", location)) throw new InvalidOperationException("Location was not added.");
        return response;
    }

    /// <summary>Loads <paramref name="start"/> on a fresh loader whose handler answers each absolute URL from
    /// <paramref name="routes"/> (404 otherwise), then asks again to see whether the result was cached.</summary>
    private static async Task<RedirectRun> FollowAsync(string start, IReadOnlyDictionary<string, Func<HttpResponseMessage>> routes)
    {
        var handler = new FakeHandler((request, _) =>
            Task.FromResult(routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer) ? answer() : Status(HttpStatusCode.NotFound)));
        using var loader = new CatalogLogoLoader(handler);
        var logo = await loader.LoadAsync(start, CancellationToken.None).WaitAsync(Bound);
        var requested = handler.Requests.Select(r => r.RequestUri!.AbsoluteUri).ToArray();
        var again = loader.LoadAsync(start, CancellationToken.None);
        var cached = again.IsCompletedSuccessfully && ReferenceEquals(again.Result, logo) && handler.Requests.Count == requested.Length;
        var agent = $"DialShift/{typeof(CatalogLogoLoader).Assembly.GetName().Version?.ToString(3) ?? "0"}";
        return new RedirectRun(logo, requested, cached, handler.Requests.All(r => r.Headers.UserAgent.ToString() == agent));
    }

    private static async Task RedirectsAsync()
    {
        var body = Png(32, 24); // decodes to 64 × 48
        Func<HttpResponseMessage> logo = () => Body(body);
        const string https = "https://logo.example.org/", http = "http://logo.example.org/";

        await Task.Run(async () =>
        {
            // To a refused host or scheme: the target is never requested, the logo is null and cached. The start is http,
            // so a downgrade is not the reason.
            string[] refusedTargets =
                ["http://127.0.0.1/logo.png", "http://[::1]/logo.png", "http://localhost/logo.png", "http://cdn.localhost/logo.png",
                 "http://10.0.0.1/logo.png", "http://192.168.1.1/logo.png", "http://169.254.169.254/latest/meta-data/",
                 "http://2130706433/logo.png", "//127.0.0.1/logo.png", "file:///etc/passwd", "ftp://logo.example.org/logo.png"];
            var wrong = new List<string>();
            foreach (var target in refusedTargets)
            {
                var start = http + "away.png";
                var resolved = new Uri(new Uri(start), target).AbsoluteUri;
                var run = await FollowAsync(start, new Dictionary<string, Func<HttpResponseMessage>>
                {
                    [start] = () => Redirect(HttpStatusCode.Found, target), [resolved] = logo,
                });
                if (run.Logo is not null || !run.Requested.SequenceEqual([start]) || !run.Cached) wrong.Add($"{target} (requested {string.Join(" → ", run.Requested)})");
            }
            if (wrong.Count > 0) Console.WriteLine("  followed, not null or not cached: " + string.Join("; ", wrong));
            Check("CAT-10 (d) a 302 to loopback, private or link-local IPs (127.0.0.1, [::1], 10.0.0.1, 192.168.1.1, 169.254.169.254, 2130706433), " +
                  "to localhost or cdn.localhost, to protocol-relative //127.0.0.1, to file:///etc/passwd or to ftp:// gives null after exactly " +
                  "one request (the target never requested), and is cached (a second call makes no request)",
                wrong.Count == 0);

            // No downgrade (D82 (b)).
            var down = await FollowAsync(https + "down.png", new Dictionary<string, Func<HttpResponseMessage>>
            {
                [https + "down.png"] = () => Redirect(HttpStatusCode.Found, http + "logo.png"), [http + "logo.png"] = logo,
            });
            var downLater = await FollowAsync(https + "down-2.png", new Dictionary<string, Func<HttpResponseMessage>>
            {
                [https + "down-2.png"] = () => Redirect(HttpStatusCode.Found, https + "hop.png"),
                [https + "hop.png"] = () => Redirect(HttpStatusCode.Found, http + "logo.png"),
                [http + "logo.png"] = logo,
            });
            Check("CAT-10 (d) a 302 from https to http gives null after one request (the http target never requested), cached; also as the second " +
                  "hop, after an https → https hop (two requests)",
                down.Logo is null && down.Requested.SequenceEqual([https + "down.png"]) && down.Cached
                && downLater.Logo is null && downLater.Requested.SequenceEqual([https + "down-2.png", https + "hop.png"]) && downLater.Cached);

            // Followed: every one of the five codes, http → https, relative locations.
            HttpStatusCode[] codes = [HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther,
                                      HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect];
            var byCode = new List<RedirectRun>();
            foreach (var code in codes)
                byCode.Add(await FollowAsync(https + $"code-{(int)code}.png", new Dictionary<string, Func<HttpResponseMessage>>
                {
                    [https + $"code-{(int)code}.png"] = () => Redirect(code, https + "logo.png"), [https + "logo.png"] = logo,
                }));
            Check("CAT-10 (d) a 301, 302, 303, 307 or 308 from https to https is followed and decodes (64 × 48, two requests), with the " +
                  "User-Agent on every hop, and the logo is cached",
                byCode.All(r => Is(r.Logo, 64, 48) && r.Requested.Length == 2 && r.Requested[1] == https + "logo.png" && r.AgentOnEveryHop && r.Cached));
            var up = await FollowAsync(http + "up.png", new Dictionary<string, Func<HttpResponseMessage>>
            {
                [http + "up.png"] = () => Redirect(HttpStatusCode.MovedPermanently, https + "logo.png"), [https + "logo.png"] = logo,
            });
            Check("CAT-10 (d) a 301 from http to https is followed and decodes (http → https is not a downgrade)",
                Is(up.Logo, 64, 48) && up.Requested.SequenceEqual([http + "up.png", https + "logo.png"]) && up.AgentOnEveryHop);
            var relative = await FollowAsync(https + "dir/sub/relative.png", new Dictionary<string, Func<HttpResponseMessage>>
            {
                [https + "dir/sub/relative.png"] = () => Redirect(HttpStatusCode.Found, "../logo.png"), [https + "dir/logo.png"] = logo,
            });
            var rooted = await FollowAsync(https + "dir/rooted.png", new Dictionary<string, Func<HttpResponseMessage>>
            {
                [https + "dir/rooted.png"] = () => Redirect(HttpStatusCode.Found, "/logo.png"), [https + "logo.png"] = logo,
            });
            var againstHop = await FollowAsync(https + "a/start.png", new Dictionary<string, Func<HttpResponseMessage>>
            {
                [https + "a/start.png"] = () => Redirect(HttpStatusCode.Found, "https://cdn.example.org/b/hop.png"),
                ["https://cdn.example.org/b/hop.png"] = () => Redirect(HttpStatusCode.Found, "img.png"),
                ["https://cdn.example.org/b/img.png"] = logo,
            });
            Check("CAT-10 (d) a relative Location is resolved against the URL that returned it: ../logo.png from /dir/sub/, /logo.png from /dir/, " +
                  "and img.png from a second host's /b/hop.png (→ that host's /b/img.png, not the first URL's)",
                Is(relative.Logo, 64, 48) && relative.Requested.SequenceEqual([https + "dir/sub/relative.png", https + "dir/logo.png"])
                && Is(rooted.Logo, 64, 48) && rooted.Requested.SequenceEqual([https + "dir/rooted.png", https + "logo.png"])
                && Is(againstHop.Logo, 64, 48)
                && againstHop.Requested.SequenceEqual([https + "a/start.png", "https://cdn.example.org/b/hop.png", "https://cdn.example.org/b/img.png"]));
            var other = new List<RedirectRun>();
            foreach (var code in new[] { HttpStatusCode.MultipleChoices, HttpStatusCode.NotModified })
                other.Add(await FollowAsync(https + $"other-{(int)code}.png", new Dictionary<string, Func<HttpResponseMessage>>
                {
                    [https + $"other-{(int)code}.png"] = () => Redirect(code, https + "logo.png"), [https + "logo.png"] = logo,
                }));
            Check("CAT-10 (d) a 300 or a 304 with a Location is not followed: null after one request, cached",
                other.All(r => r.Logo is null && r.Requested.Length == 1 && r.Cached));

            // MaxRedirects.
            async Task<RedirectRun> Chain(int hops)
            {
                var routes = new Dictionary<string, Func<HttpResponseMessage>>();
                for (var i = 0; i < hops; i++)
                {
                    var next = https + $"chain-{hops}/{i + 1}.png";
                    routes[https + $"chain-{hops}/{i}.png"] = () => Redirect(HttpStatusCode.Found, next);
                }
                routes[https + $"chain-{hops}/{hops}.png"] = logo;
                return await FollowAsync(https + $"chain-{hops}/0.png", routes);
            }
            var five = await Chain(CatalogLogoLoader.MaxRedirects);
            var six = await Chain(CatalogLogoLoader.MaxRedirects + 1);
            Check($"CAT-10 (d) MaxRedirects ({CatalogLogoLoader.MaxRedirects}) redirects in a row load (6 requests, in chain order, the User-Agent on " +
                  $"each); one more gives null after 6 requests (the 7th URL never requested), cached ({five.Requested.Length} and {six.Requested.Length} requests)",
                Is(five.Logo, 64, 48) && five.Requested.SequenceEqual(Enumerable.Range(0, 6).Select(i => https + $"chain-5/{i}.png")) && five.AgentOnEveryHop
                && six.Logo is null && six.Requested.SequenceEqual(Enumerable.Range(0, 6).Select(i => https + $"chain-6/{i}.png")) && six.Cached);
        });
    }

    // ─── (e) Join after abandon ───

    private static async Task JoinAfterAbandonAsync()
    {
        const int rounds = 10_000;
        var body = Png(8, 8); // decodes to 64 × 64
        var hold = new ConcurrentDictionary<string, TaskCompletionSource>();
        var arrived = new ConcurrentDictionary<string, TaskCompletionSource>();
        var made = new ConcurrentDictionary<string, int>();
        // The first request of each URL waits until the round releases it (or its download is cancelled); later ones answer at once.
        var handler = new FakeHandler(async (request, ct) =>
        {
            var url = request.RequestUri!.OriginalString;
            if (made.AddOrUpdate(url, 1, (_, n) => n + 1) == 1)
            {
                arrived[url].TrySetResult();
                await hold[url].Task.WaitAsync(ct);
            }
            return Body(body);
        });
        using var loader = new CatalogLogoLoader(handler);
        using var barrier = new Barrier(2);
        int newCallerNull = 0, leaverNotNull = 0, afterNotNew = 0, racedJoined = 0, racedNew = 0;
        var clock = Stopwatch.StartNew();
        await Task.Run(async () =>
        {
            for (var round = 0; round < rounds; round++)
            {
                var url = Url($"race-{round}.png");
                hold[url] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                arrived[url] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cts = new CancellationTokenSource();
                var leaving = loader.LoadAsync(url, cts.Token);
                await arrived[url].Task.WaitAsync(Bound); // the download's request is running
                Task<Bitmap?> joining;
                var raced = round % 2 == 0;
                if (raced)
                {
                    // The cancel and the new call start together on two threads. The call's task is kept, not awaited
                    // there: it cannot complete before the round releases the first request.
                    Task<Bitmap?>? placed = null;
                    var cancel = Task.Run(() => { barrier.SignalAndWait(); cts.Cancel(); });
                    var join = Task.Run(() => { barrier.SignalAndWait(); placed = loader.LoadAsync(url, CancellationToken.None); });
                    await Task.WhenAll(cancel, join).WaitAsync(Bound);
                    joining = placed!;
                }
                else
                {
                    // The new call comes right after the only wait has ended.
                    cts.Cancel();
                    await leaving.WaitAsync(Bound);
                    joining = loader.LoadAsync(url, CancellationToken.None);
                }
                hold[url].TrySetResult();
                var logo = await joining.WaitAsync(Bound);
                var left = await leaving.WaitAsync(Bound);
                var requests = made[url];
                if (!Is(logo, 64, 64)) newCallerNull++;
                if (left is not null) leaverNotNull++;
                if (raced) { if (requests == 1) racedJoined++; else racedNew++; }
                else if (requests != 2) afterNotNew++;
            }
        });
        Console.WriteLine($"  join after abandon: {rounds} rounds in {clock.Elapsed.TotalSeconds:0.00} s; of the {rounds / 2} raced rounds " +
                          $"{racedJoined} joined the running download and {racedNew} started a new one");
        Check($"CAT-10 (e) join after abandon, {rounds} rounds: the only caller of a download whose request is still running cancels while a new " +
              "caller asks for the same URL, at once from another thread (every other round) or right after the first wait ended; the new " +
              "caller always gets the logo (64 × 64, never null) and the cancelled one always gets null",
            newCallerNull == 0 && leaverNotNull == 0 && racedJoined + racedNew == rounds / 2);
        Check("CAT-10 (e) a new caller arriving after the only wait ended always makes a new request (two per URL): the abandoned download had " +
              "already left the in-flight table",
            afterNotNew == 0);
    }

    // ─── (f), (g) The decode gate ───

    /// <summary>
    /// Holds the loader's single decode slot (by reflection) for longer than <see cref="CatalogLogoLoader.Timeout"/> while
    /// downloaded logos wait for it: none may decode meanwhile (one slot), a caller cancelled at the gate leaves at once
    /// and frees its download slot, and the waiting logos still decode afterwards (the wait is outside the attempt's
    /// timeout).
    /// </summary>
    private static async Task DecodeGateAsync()
    {
        var gate = DecodeGate();
        var body = Png(32, 32); // decodes to 64 × 64
        var bodies = new ConcurrentDictionary<string, SignallingStream>();
        var handler = new FakeHandler((request, _) =>
        {
            var stream = new SignallingStream(body);
            bodies[request.RequestUri!.OriginalString] = stream;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        });
        using var loader = new CatalogLogoLoader(handler);
        string[] waiting = [Url("gate-0.png"), Url("gate-1.png"), Url("gate-2.png")];
        var leavingUrl = Url("gate-leaving.png");
        var queuedUrl = Url("gate-queued.png");
        Task<bool> Downloaded(params string[] urls) => EventuallyAsync(() => urls.All(u => bodies.TryGetValue(u, out var s) && s.Ended));

        await Task.Run(async () =>
        {
            if (!await gate.WaitAsync(Bound)) throw new TimeoutException("The logo loader's decode gate never became free.");
            var oneSlot = gate.CurrentCount == 0;
            var clock = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource();
            List<Task<Bitmap?>> loads;
            Task<Bitmap?> leaving, queued;
            bool downloaded, queuedHeldBack, leftAtOnce, queuedAfter, noneDecoded;
            TimeSpan heldAfterLastDownload;
            try
            {
                loads = [.. waiting.Select(u => loader.LoadAsync(u, CancellationToken.None))];
                leaving = loader.LoadAsync(leavingUrl, cts.Token);
                downloaded = await Downloaded([.. waiting, leavingUrl]);
                // All MaxConcurrentDownloads slots are now held by downloads waiting for the decode gate.
                queued = loader.LoadAsync(queuedUrl, CancellationToken.None);
                await Task.Delay(100);
                queuedHeldBack = handler.Count(queuedUrl) == 0;
                cts.Cancel();
                leftAtOnce = await CompletesWithinAsync(leaving, TimeSpan.FromSeconds(2)) && IsNull(leaving);
                queuedAfter = await Downloaded(queuedUrl);
                var lastDownload = clock.Elapsed;
                var until = lastDownload + CatalogLogoLoader.Timeout + TimeSpan.FromMilliseconds(500);
                if (until > clock.Elapsed) await Task.Delay(until - clock.Elapsed);
                noneDecoded = !loads.Any(t => t.IsCompleted) && !queued.IsCompleted;
                heldAfterLastDownload = clock.Elapsed - lastDownload;
            }
            finally
            {
                gate.Release();
            }
            var decoded = await Task.WhenAll([.. loads, queued]).WaitAsync(Bound);
            var freeAgain = gate.CurrentCount == 1;
            var hit = loader.LoadAsync(waiting[0], CancellationToken.None);
            var again = await loader.LoadAsync(leavingUrl, CancellationToken.None).WaitAsync(Bound);

            Check($"CAT-10 (f) decodes are serialized by one process-wide gate: while the test holds its only slot, every downloaded logo " +
                  $"(its body read to the end) waits, and none decodes in {heldAfterLastDownload.TotalSeconds:0.0} s",
                oneSlot && downloaded && noneDecoded);
            Check("CAT-10 (g) the only caller of a downloaded logo waiting for the decode gate cancels: it gets null within 2 s while the gate is " +
                  "still held, not an exception, and its download stops waiting and gives up its download slot (a queued fifth URL is then requested)",
                leftAtOnce && queuedAfter);
            Check("CAT-10 (f) a download keeps its download slot while it waits for the decode gate: with MaxConcurrentDownloads (4) logos " +
                  "waiting there, a fifth URL is not requested until one of them leaves",
                queuedHeldBack && queuedAfter);
            Check("CAT-10 (g) a download abandoned at the decode gate is neither decoded nor cached: the next call for its URL requests it again " +
                  "(two requests) and decodes it (64 × 64)",
                Is(again, 64, 64) && handler.Count(leavingUrl) == 2);
            Check("CAT-10 (f) the wait for the decode gate is outside the attempt's Timeout: logos held there more than Timeout (5 s) past their " +
                  "download all decode (64 × 64) once it is free and are cached (a hit, no new request); the gate is free again afterwards",
                decoded.All(l => Is(l, 64, 64)) && heldAfterLastDownload > CatalogLogoLoader.Timeout
                && hit.IsCompletedSuccessfully && ReferenceEquals(hit.Result, decoded[0]) && handler.Count(waiting[0]) == 1 && freeAgain);
        });
    }
}
