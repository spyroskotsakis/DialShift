using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DialShift.Core;
using DialShift.Core.Catalog;
using DialShift.Core.Playback;

namespace DialShift.App.Services;

/// <summary>Where <see cref="CatalogProvider.ResolveLocation"/> found the catalog path.</summary>
public enum CatalogLocationSource { AppFolder, Override }

/// <summary>Where the catalog is read from. Path is null exactly when Problem is set.</summary>
public sealed record CatalogLocation(string? Path, CatalogLocationSource Source, string? Problem);

/// <summary>
/// Reads the generated station catalog (<c>app-catalog.json</c>, docs/catalog-contracts.md §2) once per process and
/// builds its <see cref="StationCatalogIndex"/> (brief 3 §5.2; D60, D74).
/// </summary>
/// <remarks>
/// <para>The first <see cref="GetCatalogAsync"/> starts the load on the thread pool; startup never waits for it. Every
/// problem (a relative override, a missing, unreadable, oversized or malformed file, an unsupported
/// <c>schema_version</c>, no usable entry, too many entries) ends as <see cref="CatalogLoadState.Unavailable"/> with one
/// <c>catalog.unavailable</c> warning, and the Add dialog falls back to manual entry. Nothing here throws to a caller.</para>
/// <para>The JSON maps into private DTOs with the snake_case policy; Core's <see cref="StationCatalogEntry"/> carries no
/// JSON attributes (D74). Unknown keys are ignored; a JSON type mismatch on a known key makes the whole file Unavailable,
/// because the pipeline never writes one (§2.1).</para>
/// </remarks>
public sealed class CatalogProvider : ICatalogProvider
{
    public const string PathOverrideVariable = "DIALSHIFT_CATALOG_PATH";
    public const string FileName = "app-catalog.json";
    public const int SupportedSchemaVersion = 1;
    public const int MaxEntries = 10_000;
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>BHV-52's stream URL limit; the pipeline applies the same one (D71).</summary>
    private const int MaxStreamUrlLength = 2_048;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The pipeline writes no byte order mark (§2.1), but a hand-saved fixture may carry one; parsing bytes, unlike a
    /// stream, does not skip it.</summary>
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private readonly CatalogLocation location;
    private readonly IAppLog log;
    private readonly Lazy<Task<CatalogLoadResult>> load;

    public CatalogProvider(CatalogLocation location, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(log);
        this.location = location;
        this.log = log;
        load = new Lazy<Task<CatalogLoadResult>>(() => Task.Run(LoadAsync), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The catalog path: <c>DIALSHIFT_CATALOG_PATH</c> when it is set to an absolute path, else <see cref="FileName"/> in
    /// <paramref name="baseDirectory"/>. A relative override is a <see cref="CatalogLocation.Problem"/>, never a fallback to
    /// the app folder, so a wrong override is visible (D74). Production passes <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// and <see cref="AppContext.BaseDirectory"/>, never the current directory (D60).
    /// </summary>
    public static CatalogLocation ResolveLocation(Func<string, string?> getEnvironmentVariable, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        var value = getEnvironmentVariable(PathOverrideVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
            return new CatalogLocation(Path.Combine(baseDirectory, FileName), CatalogLocationSource.AppFolder, null);
        if (!Path.IsPathFullyQualified(value))
            return new CatalogLocation(null, CatalogLocationSource.Override, $"{PathOverrideVariable} must be an absolute path.");
        try
        {
            return new CatalogLocation(Path.GetFullPath(value), CatalogLocationSource.Override, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An embedded NUL character, for example: still Unavailable, never an exception at composition time.
            return new CatalogLocation(null, CatalogLocationSource.Override, $"{PathOverrideVariable} is not a valid path.");
        }
    }

    public Task<CatalogLoadResult> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        load.Value.WaitAsync(cancellationToken);

    /// <summary>The one load. Never faults: every exception becomes an Unavailable result.</summary>
    private async Task<CatalogLoadResult> LoadAsync()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            return await ReadAsync(clock).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return Unavailable("the file is not valid catalog JSON.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable("the file could not be read.", ex);
        }
        catch (Exception ex)
        {
            return Unavailable("the catalog could not be loaded.", ex);
        }
    }

    private async Task<CatalogLoadResult> ReadAsync(Stopwatch clock)
    {
        if (location.Path is not { } path) return Unavailable(location.Problem ?? $"{PathOverrideVariable} has no path.");
        if (Directory.Exists(path)) return Unavailable("the path is a directory, not a file.");
        if (!File.Exists(path)) return Unavailable("the file does not exist.");

        byte[] json;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan))
        {
            // A device or pipe has no length and could stream without end.
            if (!stream.CanSeek) return Unavailable("the path is not a regular file.");
            if (stream.Length > MaxFileBytes)
                return Unavailable($"the file is {stream.Length} bytes, more than the {MaxFileBytes} bytes allowed.");
            json = new byte[stream.Length];
            await stream.ReadExactlyAsync(json).ConfigureAwait(false);
        }
        // The whole (size-checked) file is parsed in one pass: DeserializeAsync over the stream re-scans the buffered
        // stations array for StationListConverter on every refill, which measured twice as slow on the real catalog.
        var utf8 = json.AsSpan();
        if (utf8.StartsWith(Utf8Bom)) utf8 = utf8[Utf8Bom.Length..];
        var document = JsonSerializer.Deserialize<CatalogDocumentDto>(utf8, JsonOptions);

        if (document == null) return Unavailable("the file holds no JSON object.");
        if (document.SchemaVersion is not { } version)
            return Unavailable($"schema_version is missing (expected {SupportedSchemaVersion}).");
        if (version != SupportedSchemaVersion)
            return Unavailable($"schema_version {version} is not supported (expected {SupportedSchemaVersion}).");
        if (document.Stations is not { } stations) return Unavailable("the file has no stations array.");
        if (stations.Count > MaxEntries)
            return Unavailable($"the file lists {stations.Count} stations, more than the {MaxEntries} supported.");

        var entries = new List<StationCatalogEntry>(stations.Count);
        foreach (var station in stations.Items)
            if (Map(station) is { } entry) entries.Add(entry);
        var skipped = stations.Count - entries.Count;
        if (skipped > 0)
            log.Warn("catalog.entries_skipped",
                $"Skipped {skipped} of {stations.Count} catalog entries without a name, country or valid stream URL.");
        if (entries.Count == 0) return Unavailable("the file has no usable station entry.");

        DateTimeOffset? generated = DateTimeOffset.TryParseExact(document.GeneratedUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
        var index = new StationCatalogIndex(entries);
        clock.Stop();
        log.Info("catalog.loaded",
            $"Loaded {index.Entries.Count} stations " +
            $"(generated {(generated is { } g ? g.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "unknown")}) " +
            $"from {path} [{(location.Source == CatalogLocationSource.AppFolder ? "app folder" : PathOverrideVariable)}] " +
            $"in {clock.ElapsedMilliseconds} ms.");
        return new CatalogLoadResult(CatalogLoadState.Loaded, index, generated, null);
    }

    /// <summary>One station per the "App tolerates" column of docs/catalog-contracts.md §2.1; null when it is skipped.</summary>
    private static StationCatalogEntry? Map(StationDto? station)
    {
        if (station == null) return null;
        var name = Text(station.Name);
        var country = Text(station.Country);
        var url = Text(station.StreamUrl);
        if (name.Length == 0 || country.Length == 0 || url.Length > MaxStreamUrlLength || !SettingsStore.ValidUrl(url)) return null;
        var logo = Text(station.Logo);
        return new StationCatalogEntry
        {
            Name = name,
            NameLocal = Text(station.NameLocal),
            Country = country,
            CountryLabel = Text(station.CountryLabel),
            City = Text(station.City),
            Region = Text(station.Region),
            FrequencyFm = Text(station.FrequencyFm),
            Type = Text(station.Type),
            Genre = Text(station.Genre),
            Language = Text(station.Language),
            InternetOnly = station.InternetOnly ?? false,
            StreamUrl = url,
            Codec = Text(station.Codec),
            Bitrate = station.Bitrate is > 0 ? station.Bitrate : null,
            Votes = station.Votes is >= 0 ? station.Votes : null,
            Notes = Text(station.Notes),
            Logo = SettingsStore.ValidUrl(logo) ? logo : "",
            Tag = Text(station.Tag)
        };
    }

    private static string Text(string? value) => value?.Trim() ?? "";

    private CatalogLoadResult Unavailable(string reason, Exception? ex = null)
    {
        var where = location.Path ?? PathOverrideVariable;
        try
        {
            log.Warn("catalog.unavailable", $"Station catalog unavailable: {reason.TrimEnd('.')} ({where}).", ex);
        }
        catch (Exception)
        {
            // A failing log must not turn a degraded catalog into a fault.
        }
        return CatalogLoadResult.Unavailable(reason);
    }

    // ---- JSON shape (docs/catalog-contracts.md §2.1); every member nullable, so missing keys are visible ----------------

    private sealed class CatalogDocumentDto
    {
        public int? SchemaVersion { get; set; }
        public string? GeneratedUtc { get; set; }

        [JsonConverter(typeof(StationListConverter))]
        public StationList? Stations { get; set; }
    }

    private sealed class StationDto
    {
        public string? Name { get; set; }
        public string? NameLocal { get; set; }
        public string? Country { get; set; }
        public string? CountryLabel { get; set; }
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? FrequencyFm { get; set; }
        public string? Type { get; set; }
        public string? Genre { get; set; }
        public string? Language { get; set; }
        public bool? InternetOnly { get; set; }
        public string? StreamUrl { get; set; }
        public string? Codec { get; set; }
        public int? Bitrate { get; set; }
        public int? Votes { get; set; }
        public string? Notes { get; set; }
        public string? Logo { get; set; }
        public string? Tag { get; set; }
    }

    /// <summary>The <c>stations</c> array: at most <see cref="MaxEntries"/> materialized elements, and the true count.</summary>
    private sealed record StationList(List<StationDto?> Items, int Count);

    /// <summary>
    /// Reads the <c>stations</c> array but materializes no more than <see cref="MaxEntries"/> elements; the rest are only
    /// counted. A 32 MiB file of <c>{}</c> elements would otherwise allocate millions of DTOs before the count check.
    /// </summary>
    private sealed class StationListConverter : JsonConverter<StationList>
    {
        public override StationList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("stations is not an array.");
            // The element converter once, not JsonSerializer.Deserialize per element: that call's per-use setup made the
            // whole load about 40 % slower on the real catalog.
            var element = (JsonConverter<StationDto>)options.GetConverter(typeof(StationDto));
            var items = new List<StationDto?>();
            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count >= MaxEntries) reader.Skip();
                else items.Add(reader.TokenType == JsonTokenType.Null ? null : element.Read(ref reader, typeof(StationDto), options));
                count++;
            }
            return new StationList(items, count);
        }

        public override void Write(Utf8JsonWriter writer, StationList value, JsonSerializerOptions options) =>
            throw new NotSupportedException("The catalog is read-only.");
    }
}
