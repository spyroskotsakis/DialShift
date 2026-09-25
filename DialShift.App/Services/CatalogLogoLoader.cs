using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DialShift.Core;
using SkiaSharp;

namespace DialShift.App.Services;

/// <summary>
/// Downloads and decodes catalog station logos for the Add dialog (D66, D80, D81; docs/catalog-contracts.md §4.3). Every
/// failure is a null result, which the dialog shows as the monogram; dead logos are normal, so nothing is logged per logo.
/// </summary>
/// <remarks>
/// <para><b>Requests (D81).</b> Logo URLs come from radio-browser, where anyone can submit a station, so only
/// <see cref="SettingsStore.ValidUrl"/> URLs with a public host are requested: <c>localhost</c> and a literal loopback,
/// private, link-local or unspecified IP address are refused at the call, without a request and without caching. The
/// loader follows redirects itself, so <c>handler</c> must not (DI passes a <see cref="SocketsHttpHandler"/> with
/// <c>AllowAutoRedirect = false</c>): at most <see cref="MaxRedirects"/> per attempt, each target resolved against the URL
/// that returned it and held to the same URL and host rules, a refused target never requested. Only literal hosts are
/// checked; a public name that resolves to a private address is still requested (D81's stated limit).</para>
/// <para><b>Bounds.</b> At most <see cref="MaxConcurrentDownloads"/> downloads run at once, and a download keeps its slot
/// while it decodes, so at most that many decodes run at once too. Each attempt (every request and the final body) is
/// cancelled after <see cref="Timeout"/>; a body over <see cref="MaxBytes"/> is abandoned. The image header is read before
/// any pixel: an image declaring more than <see cref="MaxPixels"/> pixels is refused, since a few hundred bytes can declare
/// 20,000 × 20,000 and a decode is full size before it is scaled (at most <see cref="MaxPixels"/> × 4 bytes = 64 MiB, plus
/// its mipmaps). The logo is scaled so its longest side is <see cref="DecodeSize"/>. All network and decoding work runs on
/// the thread pool.</para>
/// <para><b>Cache.</b> The last <see cref="CacheCapacity"/> results per URL are kept for the process, failures included
/// (as null), so a dead logo is fetched once; each logo is at most <see cref="DecodeSize"/> × <see cref="DecodeSize"/>
/// (16 KiB). Concurrent requests for one URL share one download. A download whose every caller cancelled is itself
/// cancelled and not cached, so a stale search never fills the queue or poisons the cache. Evicted bitmaps are not
/// disposed: a view may still show them, and they are released with their last reference.</para>
/// <para>Owns <c>handler</c> (disposed with the loader). Thread-safe.</para>
/// </remarks>
public sealed class CatalogLogoLoader(HttpMessageHandler handler) : ICatalogLogoLoader, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public const int MaxBytes = 256 * 1024;
    public const int MaxConcurrentDownloads = 4;
    public const int CacheCapacity = 128;

    /// <summary>The longest side of a decoded logo, in pixels; the other side keeps the aspect ratio, rounded, at least 1.</summary>
    public const int DecodeSize = 64;

    /// <summary>The most pixels (width × height) an image header may declare for the image to be decoded.</summary>
    public const long MaxPixels = 4096L * 4096;

    /// <summary>The most redirects the loader follows in one attempt.</summary>
    public const int MaxRedirects = 5;

    private static readonly Task<Bitmap?> NoLogo = Task.FromResult<Bitmap?>(null);

    /// <summary>Linear filtering between mipmap levels: a downscale averages the pixels it drops instead of aliasing
    /// (fine stripes scaled 1024 → 64 are off by 7 levels on average with it, by 57–63 with linear or Mitchell alone).</summary>
    private static readonly SKSamplingOptions Scaling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly HttpClient client = CreateClient(handler);
    private readonly SemaphoreSlim downloads = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly Lock gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, Bitmap? Logo)>> cache = new(StringComparer.Ordinal);
    /// <summary>Most recently used first.</summary>
    private readonly LinkedList<(string Url, Bitmap? Logo)> recency = new();
    private readonly Dictionary<string, Download> inFlight = new(StringComparer.Ordinal);
    private bool disposed;

    public Task<Bitmap?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Requestable(url) is not { } uri) return NoLogo;
        Download? download;
        lock (gate)
        {
            if (disposed) return NoLogo;
            if (cache.TryGetValue(url, out var node))
            {
                recency.Remove(node);
                recency.AddFirst(node);
                return Task.FromResult(node.Value.Logo);
            }
            // The last caller to leave a download takes it out of inFlight as it decides to cancel it, so any download
            // found here is still live.
            if (!inFlight.TryGetValue(url, out download))
            {
                var started = new Download(url, uri);
                inFlight[url] = started;
                // RunAsync takes the gate before it finishes, so Result is set before anything reads it.
                started.Result = Task.Run(() => RunAsync(started));
                download = started;
            }
            download.Waiters++;
        }
        return WaitAsync(download, cancellationToken);
    }

    public void Dispose()
    {
        List<Download> pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending = [.. inFlight.Values];
            inFlight.Clear();
            cache.Clear();
            recency.Clear();
        }
        foreach (var download in pending) download.Abandon.Cancel();
        client.Dispose();
    }

    /// <summary><paramref name="url"/> as a URI when it may be requested: a <see cref="SettingsStore.ValidUrl"/> URL whose
    /// host is public (<see cref="IsRefusedHost"/>); otherwise null.</summary>
    private static Uri? Requestable(string url) =>
        SettingsStore.ValidUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && !IsRefusedHost(uri) ? uri : null;

    /// <summary>
    /// True for <c>localhost</c> or a literal IP address in IPv4 127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16,
    /// 169.254.0.0/16 or 0.0.0.0, or IPv6 ::1, ::, fc00::/7 or fe80::/10; an IPv4-mapped IPv6 address is judged by its IPv4
    /// address (D81). <see cref="Uri"/> has already turned the shorthand IPv4 forms (<c>127.1</c>, <c>2130706433</c>,
    /// <c>0x7f.1</c>, <c>0</c>) into dotted quads. Any other host type (not a DNS name or an IP address) is refused too.
    /// </summary>
    private static bool IsRefusedHost(Uri uri)
    {
        switch (uri.HostNameType)
        {
            case UriHostNameType.Dns:
                return uri.IdnHost.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase);
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                return !IPAddress.TryParse(uri.IdnHost, out var address) || IsRefusedAddress(address);
            default:
                return true;
        }
    }

    private static bool IsRefusedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is 10 or 127
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || address.Equals(IPAddress.Any);
        return address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any)
            || (bytes[0] & 0xFE) == 0xFC                        // fc00::/7
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80); // fe80::/10
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Each attempt carries its own Timeout token, so the client's own timeout stays out of the way.
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        // Some logo hosts (Wikimedia among them) refuse requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DialShift",
            typeof(CatalogLogoLoader).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return client;
    }

    /// <summary>One caller's wait: null when it is cancelled. The last caller to leave cancels the download itself, after
    /// taking it out of <see cref="inFlight"/> in the same locked step, so no new caller joins a download being cancelled.</summary>
    private async Task<Bitmap?> WaitAsync(Download download, CancellationToken cancellationToken)
    {
        try
        {
            return await download.Result.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            bool abandon;
            lock (gate)
            {
                abandon = --download.Waiters == 0 && !download.Result.IsCompleted;
                if (abandon) ForgetInFlight(download);
            }
            if (abandon) download.Abandon.Cancel();
        }
    }

    /// <summary>The shared download. Never faults; caches its result unless it was abandoned.</summary>
    private async Task<Bitmap?> RunAsync(Download download)
    {
        Bitmap? logo = null;
        var cacheable = false;
        try
        {
            (logo, cacheable) = await FetchAsync(download.Uri, download.Abandon.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Abandoned by its callers, or the loader was disposed: nothing to keep.
        }
        lock (gate)
        {
            ForgetInFlight(download);
            if (cacheable && !disposed) Store(download.Url, logo);
        }
        return logo;
    }

    /// <summary>Takes <paramref name="download"/> out of <see cref="inFlight"/> unless a newer download of its URL has
    /// replaced it there. Under <see cref="gate"/>.</summary>
    private void ForgetInFlight(Download download)
    {
        if (inFlight.TryGetValue(download.Url, out var current) && ReferenceEquals(current, download)) inFlight.Remove(download.Url);
    }

    /// <summary>
    /// Downloads and decodes one logo, holding a download slot throughout. Returns (null, true) for a failure worth caching
    /// (HTTP error, timeout, a refused redirect, too many bytes or pixels, not an image); throws
    /// <see cref="OperationCanceledException"/> when <paramref name="abandon"/> fires.
    /// </summary>
    private async Task<(Bitmap? Logo, bool Cacheable)> FetchAsync(Uri url, CancellationToken abandon)
    {
        var body = ArrayPool<byte>.Shared.Rent(MaxBytes + 1);
        try
        {
            await downloads.WaitAsync(abandon).ConfigureAwait(false);
            try
            {
                int length;
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(abandon);
                    attempt.CancelAfter(Timeout);
                    length = await DownloadAsync(url, body, attempt.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!abandon.IsCancellationRequested)
                {
                    // The attempt's timeout, a network or HTTP failure: the logo is dead for this process.
                    length = -1;
                }
                return (length < 0 ? null : Decode(body, length), true);
            }
            finally
            {
                downloads.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>
    /// The final body's length in <paramref name="buffer"/>, or -1 for a non-success status, a redirect that is refused or
    /// past <see cref="MaxRedirects"/>, or a body over <see cref="MaxBytes"/>.
    /// </summary>
    private async Task<int> DownloadAsync(Uri url, byte[] buffer, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                if (redirects == MaxRedirects || Requestable(new Uri(url, location).AbsoluteUri) is not { } target) return -1;
                url = target;
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return -1;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var window = buffer.AsMemory(0, MaxBytes + 1);
            var total = 0;
            while (true)
            {
                var read = await source.ReadAsync(window[total..], cancellationToken).ConfigureAwait(false);
                if (read == 0) return total;
                total += read;
                // One byte past the cap proves the body is too large without reading the rest.
                if (total > MaxBytes) return -1;
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// The image scaled so its longest side is <see cref="DecodeSize"/>, or null for bytes Skia does not recognize (SVG, an
    /// HTML error page), a header with a side ≤ 0 or more than <see cref="MaxPixels"/> pixels, or pixels that do not
    /// decode completely (a truncated file).
    /// </summary>
    private static Bitmap? Decode(byte[] body, int length)
    {
        try
        {
            using var data = SKData.CreateCopy(body, (ulong)length);
            // Creating the codec reads the header only: no pixel is decoded or allocated before the size check.
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;
            var (width, height) = (codec.Info.Width, codec.Info.Height);
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return null;
            var full = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(full);
            if (decoded.GetPixels() == IntPtr.Zero || codec.GetPixels(full, decoded.GetPixels()) != SKCodecResult.Success) return null;
            var size = ScaledSize(width, height);
            using var scaled = decoded.Resize(full.WithSize(size.Width, size.Height), Scaling);
            if (scaled is null) return null;
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, scaled.GetPixels(), size, new Vector(96, 96), scaled.RowBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The longest side <see cref="DecodeSize"/>, the other scaled by the same factor, rounded, at least 1.</summary>
    private static PixelSize ScaledSize(int width, int height)
    {
        var shorter = (int)Math.Max(1, Math.Round((double)Math.Min(width, height) * DecodeSize / Math.Max(width, height), MidpointRounding.AwayFromZero));
        return width >= height ? new PixelSize(DecodeSize, shorter) : new PixelSize(shorter, DecodeSize);
    }

    /// <summary>Caches <paramref name="logo"/> as the most recent entry, evicting the least recent past <see cref="CacheCapacity"/>. Under <see cref="gate"/>.</summary>
    private void Store(string url, Bitmap? logo)
    {
        if (cache.Remove(url, out var existing)) recency.Remove(existing);
        cache[url] = recency.AddFirst((url, logo));
        while (cache.Count > CacheCapacity)
        {
            var last = recency.Last!;
            recency.RemoveLast();
            cache.Remove(last.Value.Url);
        }
    }

    /// <summary>One shared download of one URL. <see cref="Waiters"/> is guarded by the loader's gate.</summary>
    private sealed class Download(string url, Uri uri)
    {
        /// <summary>The URL as the caller gave it: the key of <see cref="inFlight"/> and of the cache.</summary>
        public string Url { get; } = url;

        public Uri Uri { get; } = uri;

        /// <summary>Fires when every caller has left or the loader is disposed. Holds no timer, so it needs no disposal.</summary>
        public CancellationTokenSource Abandon { get; } = new();

        public Task<Bitmap?> Result { get; set; } = NoLogo;

        public int Waiters { get; set; }
    }
}
