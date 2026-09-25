using System.Buffers;
using System.Net.Http.Headers;
using Avalonia.Media.Imaging;
using DialShift.Core;

namespace DialShift.App.Services;

/// <summary>
/// Downloads and decodes catalog station logos for the Add dialog (D66, docs/catalog-contracts.md §4.3). Every failure is
/// a null result, which the dialog shows as the monogram; dead logos are normal, so nothing is logged per logo.
/// </summary>
/// <remarks>
/// <para><b>Bounds.</b> Only <see cref="SettingsStore.ValidUrl"/> URLs are requested; at most
/// <see cref="MaxConcurrentDownloads"/> run at once; each attempt (request and body) is cancelled after
/// <see cref="Timeout"/>; a body over <see cref="MaxBytes"/> is abandoned; decoding scales to <see cref="DecodeWidth"/>
/// pixels wide. All network and decoding work runs on the thread pool.</para>
/// <para><b>Cache.</b> The last <see cref="CacheCapacity"/> results per URL are kept for the process, failures included
/// (as null), so a dead logo is fetched once. Concurrent requests for one URL share one download. A download whose every
/// caller cancelled is itself cancelled and not cached, so a stale search never fills the queue or poisons the cache.
/// Evicted bitmaps are not disposed: a view may still show them, and they are released with their last reference.</para>
/// <para>Owns <c>handler</c> (disposed with the loader). Thread-safe.</para>
/// </remarks>
public sealed class CatalogLogoLoader(HttpMessageHandler handler) : ICatalogLogoLoader, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public const int MaxBytes = 256 * 1024;
    public const int MaxConcurrentDownloads = 4;
    public const int CacheCapacity = 128;
    public const int DecodeWidth = 64;

    private static readonly Task<Bitmap?> NoLogo = Task.FromResult<Bitmap?>(null);

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
        if (cancellationToken.IsCancellationRequested || !SettingsStore.ValidUrl(url)) return NoLogo;
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
            // A download whose callers all left is being cancelled; a new caller starts a fresh one.
            if (!inFlight.TryGetValue(url, out download) || download.Abandon.IsCancellationRequested)
            {
                var started = new Download(url);
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

    /// <summary>One caller's wait: null when it is cancelled. The last caller to leave cancels the download itself.</summary>
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
            lock (gate) abandon = --download.Waiters == 0 && !download.Result.IsCompleted;
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
            (logo, cacheable) = await FetchAsync(download.Url, download.Abandon.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Abandoned by its callers, or the loader was disposed: nothing to keep.
        }
        lock (gate)
        {
            if (inFlight.TryGetValue(download.Url, out var current) && ReferenceEquals(current, download)) inFlight.Remove(download.Url);
            if (cacheable && !disposed) Store(download.Url, logo);
        }
        return logo;
    }

    /// <summary>
    /// Downloads and decodes one logo. Returns (null, true) for a failure worth caching (HTTP error, timeout, too large,
    /// not an image); throws <see cref="OperationCanceledException"/> when <paramref name="abandon"/> fires.
    /// </summary>
    private async Task<(Bitmap? Logo, bool Cacheable)> FetchAsync(string url, CancellationToken abandon)
    {
        var body = ArrayPool<byte>.Shared.Rent(MaxBytes + 1);
        try
        {
            int length;
            await downloads.WaitAsync(abandon).ConfigureAwait(false);
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
            finally
            {
                downloads.Release();
            }
            return (length < 0 ? null : Decode(body, length), true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>The image scaled to <see cref="DecodeWidth"/>, or null for anything Skia can't decode (SVG, an HTML
    /// error page, a truncated file).</summary>
    private static Bitmap? Decode(byte[] body, int length)
    {
        try
        {
            using var image = new MemoryStream(body, 0, length, writable: false);
            return Bitmap.DecodeToWidth(image, DecodeWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The body's length in <paramref name="buffer"/>, or -1 for a non-success status or a body over <see cref="MaxBytes"/>.</summary>
    private async Task<int> DownloadAsync(string url, byte[] buffer, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
    private sealed class Download(string url)
    {
        public string Url { get; } = url;

        /// <summary>Fires when every caller has left or the loader is disposed. Holds no timer, so it needs no disposal.</summary>
        public CancellationTokenSource Abandon { get; } = new();

        public Task<Bitmap?> Result { get; set; } = NoLogo;

        public int Waiters { get; set; }
    }
}
