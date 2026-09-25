using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
/// <para>The JSON maps into private DTOs by their snake_case keys; Core's <see cref="StationCatalogEntry"/> carries no
/// JSON attributes (D74). Unknown keys are ignored; a JSON type mismatch on a known key makes the whole file Unavailable,
/// because the pipeline never writes one (§2.1).</para>
/// </remarks>
public sealed partial class CatalogProvider : ICatalogProvider
{
    public const string PathOverrideVariable = "DIALSHIFT_CATALOG_PATH";
    public const string FileName = "app-catalog.json";
    public const int SupportedSchemaVersion = 1;
    public const int MaxEntries = 10_000;
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>BHV-52's stream URL limit; the pipeline applies the same one (D71).</summary>
    private const int MaxStreamUrlLength = 2_048;

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
        // Before opening: opening a FIFO that has no writer blocks until one appears, which may be never.
        if (MacFileType.IsSpecial(path)) return Unavailable("the path is not a regular file.");

        byte[] json;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan))
        {
            // A device or pipe has no length and could stream without end (the check for platforms without MacFileType).
            if (!stream.CanSeek) return Unavailable("the path is not a regular file.");
            if (stream.Length > MaxFileBytes)
                return Unavailable($"the file is {stream.Length} bytes, more than the {MaxFileBytes} bytes allowed.");
            json = new byte[stream.Length];
            await stream.ReadExactlyAsync(json).ConfigureAwait(false);
        }
        // The whole (size-checked) file is parsed in one pass from memory (D80).
        var utf8 = json.AsSpan();
        if (utf8.StartsWith(Utf8Bom)) utf8 = utf8[Utf8Bom.Length..];
        var document = CatalogJson.Parse(utf8);

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
            TryLog(() => log.Warn("catalog.entries_skipped",
                $"Skipped {skipped} of {stations.Count} catalog entries without a name, country or valid stream URL."));
        if (entries.Count == 0) return Unavailable("the file has no usable station entry.");

        DateTimeOffset? generated = DateTimeOffset.TryParseExact(document.GeneratedUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
        var index = new StationCatalogIndex(entries);
        clock.Stop();
        TryLog(() => log.Info("catalog.loaded",
            $"Loaded {index.Entries.Count} stations " +
            $"(generated {(generated is { } g ? g.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "unknown")}) " +
            $"from {path} [{(location.Source == CatalogLocationSource.AppFolder ? "app folder" : PathOverrideVariable)}] " +
            $"in {clock.ElapsedMilliseconds} ms."));
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
        TryLog(() => log.Warn("catalog.unavailable", $"Station catalog unavailable: {reason.TrimEnd('.')} ({where}).", ex));
        return CatalogLoadResult.Unavailable(reason);
    }

    /// <summary>Writes one log line. A failing log never changes the load's result: it neither faults the load nor turns a
    /// good catalog Unavailable.</summary>
    private static void TryLog(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Nothing to report it to; the result is what matters to the caller.
        }
    }

    /// <summary>
    /// Whether a path names something other than a regular file (a FIFO, a device or a socket), read from
    /// <c>stat</c>'s <c>st_mode</c> without opening it. .NET's file APIs report such an entry as an ordinary
    /// <see cref="FileAttributes.Normal"/> file with length 0, and <see cref="FileStream"/> can only tell after the open,
    /// which for a FIFO without a writer never returns. Apple Silicon macOS only, the one Unix DialShift ships on (D2):
    /// opening a named pipe or device on Windows does not block, and the CanSeek check after opening covers it there.
    /// </summary>
    private static partial class MacFileType
    {
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";

        /// <summary>The arm64 <c>struct stat</c> (64-bit inodes, the only layout there) is 144 bytes, with the 16-bit
        /// <c>st_mode</c> at offset 4. The plain <c>stat</c> symbol on x64 has the older 32-bit-inode layout.</summary>
        private const int StatSize = 144;
        private const int ModeOffset = 4;
        private const int FileTypeMask = 0xF000; // S_IFMT
        private const int RegularFile = 0x8000; // S_IFREG

        /// <summary>True when <c>stat</c> (which follows symbolic links) reports a type other than a regular file. False on
        /// other platforms and when <c>stat</c> fails, which leaves the error to the open.</summary>
        public static unsafe bool IsSpecial(string path)
        {
            if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return false;
            var buffer = stackalloc byte[StatSize];
            if (Stat(path, buffer) != 0) return false;
            return (*(ushort*)(buffer + ModeOffset) & FileTypeMask) != RegularFile;
        }

        [LibraryImport(LibSystem, EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
        private static unsafe partial int Stat(string path, byte* buffer);
    }

    // ---- JSON shape (docs/catalog-contracts.md §2.1); every member nullable, so missing keys are visible ----------------

    private sealed class CatalogDocumentDto
    {
        public int? SchemaVersion { get; set; }
        public string? GeneratedUtc { get; set; }
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
    /// Reads the catalog in one <see cref="Utf8JsonReader"/> pass and accepts exactly what <c>JsonSerializer.Deserialize</c>
    /// with the snake_case policy accepted into the DTOs above (D80): strict JSON (no comments, no trailing commas, depth
    /// 64, nothing but white space after the root value); case-sensitive keys, unescaped first when the file escapes them;
    /// the last of duplicate keys wins; unknown keys are skipped; <c>null</c> for any member; and a
    /// <see cref="JsonException"/> for a type mismatch, a number that is not an Int32, or text that is not valid UTF-8 or
    /// UTF-16.
    /// </summary>
    /// <remarks>
    /// <para>Why not the serializer: its first use in a process spends most of the first load on type metadata, converter
    /// setup and JIT, and that first load is the only one the app does (brief 3 §9 budget: 50 ms). On the real catalog in
    /// a fresh process (Release, Apple M4 Max) the whole load measured about 55 ms with the serializer, 52 ms with a
    /// source-generated context, and 38 ms with this reader. The hot methods are compiled fully optimized at once, because
    /// the load runs once, long before tiering would promote them; that alone is worth about 8 ms of the 38.</para>
    /// <para>The <c>stations</c> array materializes at most <see cref="MaxEntries"/> elements and only counts (and skips,
    /// unchecked) the rest, so a 32 MiB file of <c>{}</c> elements allocates no more than 10,000 DTOs before the count
    /// check.</para>
    /// </remarks>
    private static class CatalogJson
    {
        /// <summary>The decoder <see cref="Utf8JsonReader.GetString"/> uses: it throws on invalid UTF-8 instead of
        /// replacing it.</summary>
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static readonly byte[] SchemaVersionKey = "schema_version"u8.ToArray();
        private static readonly byte[] GeneratedUtcKey = "generated_utc"u8.ToArray();
        private static readonly byte[] StationsKey = "stations"u8.ToArray();

        /// <summary>The station keys, in <see cref="StationField"/> order.</summary>
        private static readonly byte[][] StationKeys =
        [
            "name"u8.ToArray(), "name_local"u8.ToArray(), "country"u8.ToArray(), "country_label"u8.ToArray(),
            "city"u8.ToArray(), "region"u8.ToArray(), "frequency_fm"u8.ToArray(), "type"u8.ToArray(), "genre"u8.ToArray(),
            "language"u8.ToArray(), "internet_only"u8.ToArray(), "stream_url"u8.ToArray(), "codec"u8.ToArray(),
            "bitrate"u8.ToArray(), "votes"u8.ToArray(), "notes"u8.ToArray(), "logo"u8.ToArray(), "tag"u8.ToArray()
        ];

        private enum StationField
        {
            Name, NameLocal, Country, CountryLabel, City, Region, FrequencyFm, Type, Genre, Language, InternetOnly,
            StreamUrl, Codec, Bitrate, Votes, Notes, Logo, Tag, Unknown
        }

        /// <summary>The document, or null when the root is JSON <c>null</c>. Throws only <see cref="JsonException"/> itself:
        /// the reader's own syntax errors are wrapped, as the serializer wrapped them.</summary>
        public static CatalogDocumentDto? Parse(ReadOnlySpan<byte> utf8)
        {
            try
            {
                return ParseDocument(utf8);
            }
            catch (JsonException ex) when (ex.GetType() != typeof(JsonException))
            {
                throw new JsonException(ex.Message, ex);
            }
        }

        private static CatalogDocumentDto? ParseDocument(ReadOnlySpan<byte> utf8)
        {
            var reader = new Utf8JsonReader(utf8);
            // An empty or white-space-only input throws here; the check is for the reader's documented contract.
            if (!reader.Read()) throw new JsonException("The file holds no JSON value.");
            CatalogDocumentDto? document = null;
            if (reader.TokenType != JsonTokenType.Null)
            {
                Expect(ref reader, JsonTokenType.StartObject, "The root");
                document = new CatalogDocumentDto();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = Key(ref reader);
                    reader.Read();
                    if (key.SequenceEqual(SchemaVersionKey)) document.SchemaVersion = Int(ref reader);
                    else if (key.SequenceEqual(GeneratedUtcKey)) document.GeneratedUtc = Text(ref reader);
                    else if (key.SequenceEqual(StationsKey)) document.Stations = Stations(ref reader);
                    else reader.Skip();
                }
            }
            // White space may follow the root value; anything else makes Read throw.
            if (reader.Read()) throw new JsonException("The file has data after its JSON value.");
            return document;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static StationList? Stations(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            Expect(ref reader, JsonTokenType.StartArray, "stations");
            var items = new List<StationDto?>();
            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count >= MaxEntries) reader.Skip();
                else items.Add(reader.TokenType == JsonTokenType.Null ? null : Station(ref reader));
                count++;
            }
            return new StationList(items, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static StationDto Station(ref Utf8JsonReader reader)
        {
            Expect(ref reader, JsonTokenType.StartObject, "A station");
            var station = new StationDto();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var field = Field(Key(ref reader));
                reader.Read();
                switch (field)
                {
                    case StationField.Name: station.Name = Text(ref reader); break;
                    case StationField.NameLocal: station.NameLocal = Text(ref reader); break;
                    case StationField.Country: station.Country = Text(ref reader); break;
                    case StationField.CountryLabel: station.CountryLabel = Text(ref reader); break;
                    case StationField.City: station.City = Text(ref reader); break;
                    case StationField.Region: station.Region = Text(ref reader); break;
                    case StationField.FrequencyFm: station.FrequencyFm = Text(ref reader); break;
                    case StationField.Type: station.Type = Text(ref reader); break;
                    case StationField.Genre: station.Genre = Text(ref reader); break;
                    case StationField.Language: station.Language = Text(ref reader); break;
                    case StationField.InternetOnly: station.InternetOnly = Bool(ref reader); break;
                    case StationField.StreamUrl: station.StreamUrl = Text(ref reader); break;
                    case StationField.Codec: station.Codec = Text(ref reader); break;
                    case StationField.Bitrate: station.Bitrate = Int(ref reader); break;
                    case StationField.Votes: station.Votes = Int(ref reader); break;
                    case StationField.Notes: station.Notes = Text(ref reader); break;
                    case StationField.Logo: station.Logo = Text(ref reader); break;
                    case StationField.Tag: station.Tag = Text(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            return station;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static StationField Field(ReadOnlySpan<byte> key)
        {
            for (var i = 0; i < StationKeys.Length; i++)
                if (key.SequenceEqual(StationKeys[i])) return (StationField)i;
            return StationField.Unknown;
        }

        /// <summary>The current key as UTF-8 bytes, unescaped when the file escapes it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static ReadOnlySpan<byte> Key(ref Utf8JsonReader reader)
        {
            if (!reader.ValueIsEscaped) return reader.ValueSpan;
            var unescaped = new byte[reader.ValueSpan.Length];
            try
            {
                return unescaped.AsSpan(0, reader.CopyString(unescaped));
            }
            catch (InvalidOperationException ex)
            {
                throw new JsonException("A key is not valid UTF-8 or UTF-16 text.", ex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static string? Text(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            Expect(ref reader, JsonTokenType.String, "A text value");
            try
            {
                return reader.ValueIsEscaped ? reader.GetString() : StrictUtf8.GetString(reader.ValueSpan);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DecoderFallbackException)
            {
                throw new JsonException("A text value is not valid UTF-8 or UTF-16 text.", ex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int? Int(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            Expect(ref reader, JsonTokenType.Number, "A whole-number value");
            return reader.TryGetInt32(out var value) ? value : throw new JsonException("A number is not a 32-bit whole number.");
        }

        private static bool? Bool(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            _ => throw new JsonException($"A true/false value is {reader.TokenType}.")
        };

        private static void Expect(ref Utf8JsonReader reader, JsonTokenType expected, string what)
        {
            if (reader.TokenType != expected) throw new JsonException($"{what} is {reader.TokenType}, not {expected}.");
        }
    }
}
