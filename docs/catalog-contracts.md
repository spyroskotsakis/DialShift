# Catalog contracts (brief 3, Phase 0): frozen signatures, rules, ownership and test plan

> **Status: frozen 2026-09-25 by the spec lane, before any implementation lane starts.** Normative for brief 3
> (`docs/add-station-catalog-search.md`, "the brief"; § numbers without a file name are this document's). Lanes
> implement the signatures here **verbatim**; a change goes through the spec lane and a new decision in
> `docs/decisions.md` (D59–D68 are the brief's §11 defaults, D69–D76 the ambiguities resolved in Phase 0). The
> acceptance rows are CAT-01..18 in `docs/acceptance-matrix.md` §11. Nothing here exists as code yet: the
> contracts are written down, not stubbed, so no lane ever finds dead or throwing placeholder code.

## Contents

1. [Facts measured in Phase 0](#1-facts-measured-in-phase-0)
2. [Data contract: `data/output/app-catalog.json`](#2-data-contract-dataoutputapp-catalogjson)
3. [Core contracts: `DialShift.Core/Catalog/`](#3-core-contracts-dialshiftcorecatalog)
4. [App contracts: provider, logo loader, build and package](#4-app-contracts-provider-logo-loader-build-and-package)
5. [View-model and dialog contract](#5-view-model-and-dialog-contract)
6. [Settings: `Station.Notes`](#6-settings-stationnotes)
7. [File ownership map and sequencing](#7-file-ownership-map-and-sequencing)
8. [Test plan by CAT row](#8-test-plan-by-cat-row)
9. [Evidence gate while CI is unavailable](#9-evidence-gate-while-ci-is-unavailable)

---

## 1. Facts measured in Phase 0

Taken on this Mac (Apple Silicon, .NET 10 SDK, `data/canonical/*.csv` at `78b122e`). They drive D69–D76.

| Fact | Value | Consequence |
|---|---|---|
| Canonical rows (3 countries + 1 collection) | 8,665 | — |
| `stream_status == Working` with a non-empty URL | **8,281** (DE 4,761, FR 1,829, GR 1,667, Internet 24) | The brief's "~1,400" is wrong; the ≤5,000 budget is amended to ≤10,000 (D69) |
| Of those, URL longer than the app's 2,048-character limit | 7 | Excluded by the URL rule, so the expected export count is **8,274** (D71) |
| Duplicate `(name, country, stream_url)` | 0 | Validation holds on today's data |
| Duplicate `(name, city, stream_url)` without the country | 1 (`Abdulbasit Abdulsamad`, city `—`, in FR, DE and GR) | Dedupe is per country, or the count rule could never hold (D71) |
| Names longer than 100 characters (BHV-52 limit) | 67 (longest 399) | The fill truncates; the JSON keeps the full name (D73) |
| Names with leading/trailing spaces | 146 | The exporter trims every string (D71) |
| `city == "—"` placeholder / `region == "(unlisted)"` | 3,486 / 3,461 | Normalized to `""` in the JSON, so "—" never becomes a filter value (D71) |
| `frequency_fm` without a dot (AM kHz such as `1593`) | 70 | Frequency display and matching rules in §3.3 and §5.4 |
| Notes that are only `tags:` (radio-browser extras with no tags) | part of 7,330 `tags: …` notes | `tags:` alone becomes `""`; Wikipedia `_emphasis_` markers are stripped (D71) |
| Logos that are not http(s) | 36; empty: 3,259 | Exporter and provider keep only http(s) logos (D71, D74) |
| Distinct values: type / genre / language / city | 7 / 132 / 173 / 434 | Flat, data-driven filter lists (D72) |
| JSON size, one station per line | about 3.5 MB | Loose file, `MaxFileBytes` 32 MiB guard (D74) |
| Search over 8,665 rows × 3 fields with `CompareInfo.IndexOf(IgnoreCase \| IgnoreNonSpace)` per call | 0.8–11.2 ms (`münchen` 11.2 ms, `αθήνα` 8.6 ms) | Over the 10 ms budget before ranking: fold once, match ordinally (D70) |
| Same search on precomputed folded keys | 0.12–0.22 ms; folding all rows once: 7.3 ms | Folding happens once, at load, inside the 50 ms budget |
| `AppContext.BaseDirectory` in the D51 bundle layout (apphost in `Contents/MacOS`, `.dll` symlinked from `Contents/Resources/app`) | `…/Contents/Resources/app/` | The JSON, a non-Mach-O file, stays in `Contents/Resources/app` and is found there (D60) |
| `LatestValueDispatcher<T>` | coalesces pushes into one UI-thread post; **no delay** | It is not a debounce; the 200 ms delay is separate (D72) |
| `SettingsStore.ValidUrl` | absolute URI, scheme `http`/`https`, non-empty host | The pipeline mirrors it, plus the 2,048 limit |

## 2. Data contract: `data/output/app-catalog.json`

Owner: data lane (Phase 1). The pipeline writes it; the App reads it; nobody edits it by hand.

### 2.1 Shape

```json
{"schema_version":1,"generated_utc":"2026-09-25T12:00:00Z","stations":[
{"name":"Antenne Bayern","name_local":"","country":"DE","country_label":"Germany","city":"Ismaning (Munich)","region":"Bavaria","frequency_fm":"101.5","type":"Music","genre":"Pop / Schlager","language":"German","internet_only":false,"stream_url":"https://…","codec":"MP3","bitrate":128,"votes":4210,"notes":"…","logo":"https://…","tag":"Music · Pop / Schlager"},
{"name":"…"}
]}
```

- **File:** UTF-8 without BOM, `ensure_ascii=False`, `\n` line ends. Line 1 is the header up to `"stations":[`; then **one station object per line** (compact separators `,` and `:`), lines joined with `,\n`; the last line is `]}` followed by a final `\n`. One file, readable diffs.
- **Keys:** the brief's §5.1 list plus `tag`, **exactly** these 18 keys in this order: `name`, `name_local`, `country`, `country_label`, `city`, `region`, `frequency_fm`, `type`, `genre`, `language`, `internet_only`, `stream_url`, `codec`, `bitrate`, `votes`, `notes`, `logo`, `tag`. No other key.

| Key | JSON type | Pipeline guarantees | App tolerates (D74) |
|---|---|---|---|
| `schema_version` | integer | `1` | missing, non-integer or ≠ 1 → Unavailable |
| `generated_utc` | string | `yyyy-MM-ddTHH:mm:ssZ`, the run's UTC time, seconds precision | missing or unparsable → `GeneratedUtc = null`, still Loaded |
| `stations` | array of objects | ≥ 1 entry, ≤ 10,000 | missing, `null` or not an array → Unavailable |
| `name` | string | non-empty, trimmed (may exceed 100 characters) | empty/missing → entry skipped |
| `name_local` | string | trimmed, may be `""` | missing/`null` → `""` |
| `country` | string | non-empty: the YAML `code` (`GR`, `FR`, `DE`, …) or `Internet` | empty/missing → entry skipped |
| `country_label` | string | the YAML `name`; `Internet (collections)` for collections | missing/`""` → shown as `country` |
| `city` | string | trimmed; `—` → `""` | missing/`null` → `""` |
| `region` | string | trimmed; `(unlisted)` → `""` | missing/`null` → `""` |
| `frequency_fm` | string | as in the canonical CSV (`"101.5"`, `"1593"`, `""`) | missing/`null` → `""` |
| `type`, `genre`, `language`, `codec` | string | trimmed, may be `""` | missing/`null` → `""` |
| `internet_only` | boolean | canonical `Yes` → `true`; `No` and `Unknown` → `false` | missing/`null` → `false` |
| `stream_url` | string | passes the URL rule below | fails the URL rule → entry skipped |
| `bitrate` | integer or `null` | a positive integer, else `null` (`""`, `0`, non-numeric → `null`) | ≤ 0 → `null` |
| `votes` | integer or `null` | an integer ≥ 0 (canonical default `0`), else `null` | < 0 → `null` |
| `notes` | string | trimmed; `tags:` with nothing after it → `""`; `_text_` → `text` | missing/`null` → `""` |
| `logo` | string | an http(s) URL or `""` | not http(s) → `""` |
| `tag` | string | `app_tag(row)` of `data/build/build_all.py`, non-empty | missing/`null` → `""` |
| any other key | — | never written | ignored (forward compatibility) |

A JSON **type** mismatch on a known key (for example `"votes":"12"`) makes the whole file Unavailable: the pipeline never writes one, so it means a corrupt or foreign file.

### 2.2 Inclusion, dedupe, order (D71)

- **Source rows:** every row the run writes to `data/canonical/*.csv` (all `countries/*.yaml` and all `collections/*.yaml`), taken from the same in-memory rows.
- **URL rule** (mirrors `SettingsStore.ValidUrl` plus BHV-52's limit): `stream_url.strip()` has scheme `http` or `https` (lower case, as `urlsplit` returns it), a non-empty `hostname`, no whitespace or control character, and at most 2,048 characters.
- **Included:** `stream_status == "Working"` and the URL rule holds. Nothing else is filtered.
- **Dedupe key** (the pipeline's final dedupe, per country): `(country, norm(name).replace(' ', ''), norm_city(city, country), url_norm(stream_url))` with `norm`, `norm_city`, `url_norm` from `data/build/common.py`. On a collision the row with the higher `_row_score` (from `build_stations.py`) stays, ties keep the first.
- **Order:** `country` ascending, then `votes` descending (`null` as 0), then `name`, then `stream_url`; strings compare by Unicode code point (Python's default `str` order; the C# check uses a code-point comparer, not UTF-16 ordinal). Deterministic for a given input. The app ranks by its own rules (§3.3), so this order only makes the file stable and diffs readable.

### 2.3 Validation (hard failure, same run)

Implemented as `validate_app_catalog(doc, source_rows)`; any failure prints every problem, exits non-zero and leaves the previous `app-catalog.json` untouched (write to a temp file in `data/output/`, validate, then `os.replace`).

1. `schema_version == 1`; `generated_utc` matches `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$`.
2. Every entry has exactly the 18 keys of §2.1, with the JSON types of the table.
3. Every entry: non-empty `name` and `country`; `stream_url` passes the URL rule.
4. No duplicate `(name, country, stream_url)`.
5. `len(stations) == |{(country, name, stream_url) of the Working rows that pass the URL rule}|`. A dedupe that dropped a row therefore fails the run: fix the YAML, not the script (`.claude/rules/data-catalog-only.md`).
6. `1 <= len(stations) <= 10000` (D69).

The run logs one line: `app-catalog: working=<n> url_excluded=<n> duplicates_removed=<n> exported=<n> -> data/output/app-catalog.json`. Today's expected values: `working=8281 url_excluded=7 duplicates_removed=0 exported=8274`.

### 2.4 Python surface (`data/build/app_catalog.py`)

```python
SCHEMA_VERSION = 1
MAX_ENTRIES = 10_000
MAX_URL_LENGTH = 2_048
KEYS = ('name', 'name_local', 'country', 'country_label', 'city', 'region', 'frequency_fm', 'type', 'genre',
        'language', 'internet_only', 'stream_url', 'codec', 'bitrate', 'votes', 'notes', 'logo', 'tag')

def valid_stream_url(url: str) -> bool: ...
def build_app_catalog(sources: list[tuple[dict, list[dict]]], generated_utc: str) -> tuple[dict, dict]:
    """sources: (yaml cfg, canonical rows) per country and per collection, in build order.
    Returns (document, stats) with stats keys working, url_excluded, duplicates_removed, exported."""
def validate_app_catalog(doc: dict, source_rows: list[dict]) -> list[str]:
    """Every problem found (empty list = valid)."""
def write_app_catalog(doc: dict, path: Path) -> None:
    """Serializes per §2.1 to a temp file next to path, then os.replace."""
```

`python data/build/app_catalog.py --self-test` runs stdlib-only fixture checks (no network, no openpyxl): a Down row, an empty URL, an `ftp://` URL, a 2,049-character URL and a host-less URL are excluded; a duplicate is removed and then reported by rule 5; `tag` equals `app_tag`; a collection row gets `Internet` / `Internet (collections)`; the placeholders, `internet_only`, `bitrate`, `votes`, `notes` and `logo` normalize per §2.1; the order rule holds; the writer's output parses and round-trips byte-for-byte. `build_all.py` calls `build_app_catalog`, `validate_app_catalog` and `write_app_catalog` after the CSVs and before the XLSX.

## 3. Core contracts: `DialShift.Core/Catalog/`

Owner: core lane (Phase 2). Namespace `DialShift.Core.Catalog`. Pure: no file, path, JSON, clock, culture-dependent API, Avalonia or UI reference (DOD-02 grep applies). No JSON attributes (D74).

### 3.1 `StationCatalogEntry.cs`

```csharp
namespace DialShift.Core.Catalog;

/// <summary>One station of the generated app catalog (docs/catalog-contracts.md §2). Plain immutable data.</summary>
/// <remarks>Strings are never null; "" means unknown. The catalog provider guarantees: Name, Country and StreamUrl
/// are non-empty, StreamUrl passes SettingsStore.ValidUrl and is at most 2,048 characters, Logo is "" or an http(s)
/// URL, Bitrate is null or positive, Votes is null or non-negative. The query engine does not re-validate.</remarks>
public sealed record StationCatalogEntry
{
    public required string Name { get; init; }
    public string NameLocal { get; init; } = "";
    public required string Country { get; init; }
    public string CountryLabel { get; init; } = "";
    public string City { get; init; } = "";
    public string Region { get; init; } = "";
    public string FrequencyFm { get; init; } = "";
    public string Type { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Language { get; init; } = "";
    public bool InternetOnly { get; init; }
    public required string StreamUrl { get; init; }
    public string Codec { get; init; } = "";
    public int? Bitrate { get; init; }
    public int? Votes { get; init; }
    public string Notes { get; init; } = "";
    public string Logo { get; init; } = "";
    public string Tag { get; init; } = "";
}
```

### 3.2 `StationCatalogIndex.cs` and the query types

```csharp
namespace DialShift.Core.Catalog;

/// <summary>The loaded catalog with its search keys folded once (D70). Immutable and thread-safe.</summary>
public sealed class StationCatalogIndex
{
    /// <summary>No stations; the catalog of an unavailable load.</summary>
    public static StationCatalogIndex Empty { get; }

    /// <summary>Copies <paramref name="entries"/> (order kept) and folds Name, NameLocal and City and extracts the
    /// FrequencyFm digits of every entry. O(n); throws ArgumentNullException for a null list or a null entry.</summary>
    public StationCatalogIndex(IReadOnlyList<StationCatalogEntry> entries);

    public IReadOnlyList<StationCatalogEntry> Entries { get; }
}

/// <summary>Filter values; null means "All". Values compare ordinally with the entry's field (Country is the code).</summary>
public sealed record CatalogFilters(
    string? Country = null, string? City = null, string? Type = null, string? Genre = null, string? Language = null)
{
    public static CatalogFilters None { get; } = new();
}

/// <summary>The first <c>cap</c> matches in rank order, and how many entries matched in total.</summary>
public sealed record CatalogSearchResult(IReadOnlyList<StationCatalogEntry> Items, int TotalCount);

/// <summary>The fields that have a filter.</summary>
public enum CatalogField { Country, City, Type, Genre, Language }

/// <summary>One distinct filter value. Label is what the user sees: the country label for Country, else Value.</summary>
public sealed record CatalogFilterValue(string Value, string Label);

/// <summary>Pure filter-and-rank engine over a <see cref="StationCatalogIndex"/> (§3.3). No I/O.</summary>
public static class StationCatalogQuery
{
    public const int DefaultCap = 50;

    public static CatalogSearchResult Search(StationCatalogIndex catalog, string? text, CatalogFilters filters, int cap = DefaultCap);

    public static IReadOnlyList<CatalogFilterValue> AvailableValues(IReadOnlyList<StationCatalogEntry> entries, CatalogField field);

    /// <summary>The §3.3 normalization. Internal: used by the index and the query; the tests see it through InternalsVisibleTo.</summary>
    internal static string Fold(string value);
}
```

**Amendment of the brief's signature (D70):** the brief wrote `Search(IReadOnlyList<StationCatalogEntry>, …)`. `Search` takes the `StationCatalogIndex` instead, because folding per call measured up to 11.2 ms on the real data before ranking (§1). The index is built once, inside the load. Files: `StationCatalogEntry.cs`, `StationCatalogIndex.cs`, `StationCatalogQuery.cs` (with `CatalogFilters`, `CatalogSearchResult`, `CatalogField`, `CatalogFilterValue`).

### 3.3 Matching and ranking (normative, testable)

**Fold(s)** (replaces `CompareOptions.IgnoreCase | IgnoreNonSpace`, same intent, culture-independent, D70):

1. `s.Normalize(NormalizationForm.FormKD)` (compatibility decomposition: `ﬁ` → `fi`, full-width → ASCII, `é` → `e` + U+0301).
2. Drop every character whose `CharUnicodeInfo.GetUnicodeCategory` is `NonSpacingMark` or `EnclosingMark`.
3. `char.ToLowerInvariant` per character, then map `ς` → `σ`, `ß` → `ss`, `æ` → `ae`, `œ` → `oe`, `ø` → `o`, `ł` → `l`, `đ` → `d`, `ı` → `i`.
4. Every run of `char.IsWhiteSpace` characters becomes one space; trim both ends.

Examples that are tests: `Fold("München") == "munchen"`, `Fold("ΑΘΗΝΑΣ") == Fold("αθήνας") == "αθηνασ"`, `Fold("Straße") == "strasse"`, `Fold("  Radio\t  FM ") == "radio fm"`, `Fold("ﬁp") == "fip"`.

**Query.** `q = Fold(text ?? "")`. If `q == ""` there is no text constraint.

**Frequency query.** The raw `text.Trim()` matches `^([0-9]{2,4})(?:[.,]([0-9]{0,2}))?(?:\s*(?:fm|mhz|khz))?$` (`RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`, ASCII digits only). Its digits `D` = group 1 + group 2. An entry's frequency digits `F` = the ASCII digits of `FrequencyFm` in order (an empty `F` never matches). It is a frequency match when `F.StartsWith(D, StringComparison.Ordinal)`. So `1015`, `101.5`, `101,5` and `101.5 FM` all match `"101.5"`; `101` matches `101.x` and AM `1017`; `1`, `12345` and `101.555` are not frequency queries (they still text-match).

**Filters.** Every non-null field of `CatalogFilters` must equal the entry's field with `StringComparison.Ordinal` (`Country` against `entry.Country`). All filters AND with each other and with the text.

**Tiers** (an entry takes the lowest tier it satisfies; no tier = no match; with `q == ""` every filtered entry is tier 0):

| Tier | Rule (ordinal on folded keys) |
|---|---|
| 0 | `Fold(Name).StartsWith(q)` |
| 1 | `Fold(Name).Contains(q)` (not at position 0) |
| 2 | `Fold(NameLocal).Contains(q)` or `Fold(City).Contains(q)` |
| 3 | frequency match (frequency queries only) |

Only Name, NameLocal, City and FrequencyFm are searched; genre, notes and tags are not.

**Total order** (fully deterministic, independent of the host culture): tier ascending → `Votes ?? 0` descending → `Fold(Name)` ordinal → `Name` ordinal → `Country` ordinal → `StreamUrl` ordinal → position in `catalog.Entries`.

**Result.** `TotalCount` = number of matches; `Items` = the first `min(cap, TotalCount)` of the total order. `cap < 1` → `ArgumentOutOfRangeException`; `catalog` or `filters` null → `ArgumentNullException`. The scan is O(n) over precomputed keys with a bounded top-`cap` selection; no allocation per non-matching entry.

**AvailableValues(entries, field).** Distinct non-empty values of the field (ordinal distinct). `Label` = `Value`, except for `Country`: the first non-empty `CountryLabel` of an entry with that code (list order), else the code. Ordered by `Fold(Label)` ordinal, then `Label` ordinal, then `Value` ordinal. Filter lists are flat, computed from the whole catalog, never cascading (D72).

## 4. App contracts: provider, logo loader, build and package

Owner: integration lane (Phase 3). Namespace `DialShift.App.Services`.

### 4.1 `ICatalogProvider.cs`

```csharp
using DialShift.Core.Catalog;

namespace DialShift.App.Services;

public enum CatalogLoadState { Loaded, Unavailable }

/// <summary>The outcome of the one catalog load. Message is a diagnostic reason for Unavailable (logged, never shown);
/// null when Loaded. GeneratedUtc is the file's generated_utc, null when absent or unparsable.</summary>
public sealed record CatalogLoadResult(CatalogLoadState State, StationCatalogIndex Catalog, DateTimeOffset? GeneratedUtc, string? Message)
{
    public static CatalogLoadResult Unavailable(string message) => new(CatalogLoadState.Unavailable, StationCatalogIndex.Empty, null, message);
}

/// <summary>Loads app-catalog.json once per process (docs/catalog-contracts.md §4.2).</summary>
public interface ICatalogProvider
{
    /// <summary>The first call starts the one load on the thread pool; every call returns that load's result. Never
    /// faults. <paramref name="cancellationToken"/> only abandons this caller's wait (OperationCanceledException); the
    /// load itself continues for later callers.</summary>
    Task<CatalogLoadResult> GetCatalogAsync(CancellationToken cancellationToken = default);
}
```

### 4.2 `CatalogProvider.cs`

```csharp
namespace DialShift.App.Services;

public enum CatalogLocationSource { AppFolder, Override }

/// <summary>Where the catalog is read from. Path is null exactly when Problem is set.</summary>
public sealed record CatalogLocation(string? Path, CatalogLocationSource Source, string? Problem);

public sealed class CatalogProvider : ICatalogProvider
{
    public const string PathOverrideVariable = "DIALSHIFT_CATALOG_PATH";
    public const string FileName = "app-catalog.json";
    public const int SupportedSchemaVersion = 1;
    public const int MaxEntries = 10_000;
    public const long MaxFileBytes = 32L * 1024 * 1024;

    public CatalogProvider(CatalogLocation location, IAppLog log);

    public static CatalogLocation ResolveLocation(Func<string, string?> getEnvironmentVariable, string baseDirectory);

    public Task<CatalogLoadResult> GetCatalogAsync(CancellationToken cancellationToken = default);
}
```

**`ResolveLocation`** (D60, D74):

| `DIALSHIFT_CATALOG_PATH` | Result |
|---|---|
| unset, empty or whitespace | `(Path.Combine(baseDirectory, FileName), AppFolder, null)` |
| `Path.IsPathFullyQualified(value.Trim())` | `(Path.GetFullPath(value.Trim()), Override, null)`; any file name (tests use fixtures) |
| anything else (relative) | `(null, Override, "DIALSHIFT_CATALOG_PATH must be an absolute path.")`: the load is Unavailable; **no fallback** to the app folder, so a wrong override is visible |

Production passes `Environment.GetEnvironmentVariable` and `AppContext.BaseDirectory` (never the current directory).

**Load** (once, inside `Task.Run`; every exception caught; D74):

1. `location.Problem` → Unavailable(Problem).
2. File missing, a directory, larger than `MaxFileBytes`, or unreadable (`IOException`, `UnauthorizedAccessException`) → Unavailable.
3. `JsonSerializer.DeserializeAsync` into **private** DTOs (all members nullable) with `new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }` and otherwise default options (unknown keys skipped; comments, trailing commas and quoted numbers rejected). `JsonException` or a null root → Unavailable.
4. `schema_version` missing or ≠ `SupportedSchemaVersion` → Unavailable (`"schema_version 2 is not supported (expected 1)."`). `stations` missing or null → Unavailable.
5. Map each station per the "App tolerates" column of §2.1: trim every string, `null` → `""`; skip a null element, an empty `name` or `country`, and a `stream_url` that fails `SettingsStore.ValidUrl` or exceeds 2,048 characters; `logo` that fails `SettingsStore.ValidUrl` → `""`; `bitrate` ≤ 0 → null; `votes` < 0 → null. Skipped > 0 → one `catalog.entries_skipped` warning.
6. No usable entry, or more than `MaxEntries` → Unavailable.
7. `generated_utc` via `DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)`, else null.
8. `new StationCatalogIndex(entries)` (folding is part of the load budget) → Loaded, `Message = null`.

**Log events** (`IAppLog`, lower-case dotted, D74; the redactor shortens the home prefix in paths):

| Event | Level | When | Message (shape) |
|---|---|---|---|
| `catalog.loaded` | Info | Loaded | `Loaded 8274 stations (generated 2026-09-25T12:00:00Z) from <path> [app folder\|DIALSHIFT_CATALOG_PATH] in 31 ms.` |
| `catalog.unavailable` | Warn | every Unavailable, exactly once per process | `Station catalog unavailable: <reason> (<path or variable>).` with the exception when there is one |
| `catalog.entries_skipped` | Warn | step 5 skipped ≥ 1 | `Skipped 3 of 8277 catalog entries without a name, country or valid stream URL.` |

### 4.3 `ICatalogLogoLoader.cs` / `CatalogLogoLoader.cs` (D66)

```csharp
using Avalonia.Media.Imaging;

namespace DialShift.App.Services;

public interface ICatalogLogoLoader
{
    /// <summary>The decoded logo, or null on any failure (not http/https, timeout, too large, not an image,
    /// cancelled). Never throws; never runs network or decoding work on the calling thread.</summary>
    Task<Bitmap?> LoadAsync(string url, CancellationToken cancellationToken);
}

public sealed class CatalogLogoLoader(HttpMessageHandler handler) : ICatalogLogoLoader, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public const int MaxBytes = 256 * 1024;
    public const int MaxConcurrentDownloads = 4;
    public const int CacheCapacity = 128;
    public const int DecodeWidth = 64;

    public Task<Bitmap?> LoadAsync(string url, CancellationToken cancellationToken);
    public void Dispose();
}
```

Rules: only `SettingsStore.ValidUrl` URLs are requested; at most `MaxConcurrentDownloads` at once; each attempt is bounded by `Timeout`; bodies over `MaxBytes` are abandoned; decoding uses `Bitmap.DecodeToWidth(stream, DecodeWidth)` off the UI thread; results (failures included, as `null`) are cached per URL up to `CacheCapacity` URLs for the process, so a dead logo is fetched once. No log line per logo (dead logos are normal). DI passes a `SocketsHttpHandler { ConnectTimeout = Timeout }`; the tests pass a handler of their own.

### 4.4 DI, build item, smoke and package checks

- **`AppComposition`:** `services.AddSingleton<ICatalogProvider>(sp => new CatalogProvider(CatalogProvider.ResolveLocation(Environment.GetEnvironmentVariable, AppContext.BaseDirectory), sp.GetRequiredService<IAppLog>()));` and `services.AddSingleton<ICatalogLogoLoader>(_ => new CatalogLogoLoader(new SocketsHttpHandler { ConnectTimeout = CatalogLogoLoader.Timeout }));`. The UI lane then passes both into `MainWindowViewModel` (§5.1). The load is lazy: the first Add dialog starts it; startup does not wait for it.
- **`DialShift.App.csproj`** (one item, D59/D60):

  ```xml
  <ItemGroup>
    <!-- Brief 3: the generated station catalog (data/output/app-catalog.json), a loose file next to the apphost (D59, D60). -->
    <Content Include="..\data\output\app-catalog.json" Link="app-catalog.json"
             CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest"
             Condition="Exists('..\data\output\app-catalog.json')" />
  </ItemGroup>
  ```

  Never an `AvaloniaResource`. The item also flows to `DialShift.Tests`' output through the project reference, which the tests rely on (§8, CAT-02/CAT-04).
- **Smoke:** one new `--smoke-test` check, `Catalog loads from the app folder`: `GetCatalogAsync` returns Loaded with at least one station from `CatalogLocationSource.AppFolder`; the detail names the count. The smoke total rises by one on each OS, and the docs lane updates every count that cites it.
- **`scripts/verify-mac-app.sh`:** `Contents/Resources/app/app-catalog.json` is a regular file, parses as JSON with `schema_version` 1 and a non-empty `stations` array, using a parser that accepts `null` (`/usr/bin/python3 -c 'import json,sys; …'`; `plutil` rejects `null`). Runs for the `.app`, and for both `ditto` and `unzip` extractions with `--zip`.
- **`scripts/verify-win-package.ps1`:** `app-catalog.json` next to `DialShift.exe`, `ConvertFrom-Json` succeeds, `schema_version -eq 1`, at least one station.

## 5. View-model and dialog contract

Owner: UI lane (Phase 4). Namespace `DialShift.App.ViewModels`.

### 5.1 Wiring

- `ViewModelServices` gains, **appended** to its primary constructor, `ICatalogProvider catalog, ICatalogLogoLoader logos, IUiDispatcher dispatcher`, exposed as `Catalog`, `Logos`, `Dispatcher`, plus `public TimeSpan CatalogSearchDelay { get; init; } = StationEditorViewModel.DefaultSearchDelay;` (tests set `TimeSpan.Zero`).
- `MainWindowViewModel` gains `ICatalogProvider catalog, ICatalogLogoLoader logos` (appended) and passes them, with its `IUiDispatcher`, into `ViewModelServices`. `AppComposition` resolves them for it.
- `StationsPageViewModel.EditAsync` constructs the editor with the new arguments below.

### 5.2 `StationEditorViewModel` surface

```csharp
public sealed class StationEditorViewModel : EditorViewModel
{
    public static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(200);

    public StationEditorViewModel(Settings settings, Station? original, IDialogService dialogs, Action<Exception> onError,
        ICatalogProvider catalog, ICatalogLogoLoader logos, IUiDispatcher dispatcher, TimeSpan searchDelay);

    // Unchanged (HS-02 and Field() depend on them): NameMaxLength 100, TagMaxLength 160, UrlMaxLength 2048,
    // NameLabel, TagLabel, UrlLabel, Original, DeleteLabel, Hint, Name, Tag, Url, Title ("Add a frequency" /
    // "Edit station"), Description, CanDelete, Error/HasError, SaveCommand, CancelCommand, DeleteCommand,
    // FocusRequested with "Name"/"Url", and both validation messages.

    public bool IsAddMode { get; }                               // Original == null; the catalog panel exists only then
    public bool IsCatalogLoading { get; }                        // Add mode, until the load completes
    public bool IsCatalogAvailable { get; }                      // the load result is Loaded
    public string CatalogStatusText { get; }                     // §5.3

    public string SearchText { get; set; }                       // null → ""; debounced (D72)

    public IReadOnlyList<CatalogFilterOption> CountryOptions { get; }    // CatalogFilterOption.All first, then
    public IReadOnlyList<CatalogFilterOption> CityOptions { get; }       // StationCatalogQuery.AvailableValues;
    public IReadOnlyList<CatalogFilterOption> TypeOptions { get; }       // [All] alone until the catalog loads
    public IReadOnlyList<CatalogFilterOption> GenreOptions { get; }
    public IReadOnlyList<CatalogFilterOption> LanguageOptions { get; }
    public CatalogFilterOption SelectedCountry { get; set; }     // never null: a null assignment selects All
    public CatalogFilterOption SelectedCity { get; set; }
    public CatalogFilterOption SelectedType { get; set; }
    public CatalogFilterOption SelectedGenre { get; set; }
    public CatalogFilterOption SelectedLanguage { get; set; }
    public RelayCommand ClearFiltersCommand { get; }             // search "" + every filter All, one search

    public IReadOnlyList<CatalogResultRow> Results { get; }      // at most StationCatalogQuery.DefaultCap rows
    public int TotalCount { get; }
    public string TotalCountText { get; }                        // §5.3
    public bool HasNoMatches { get; }                            // available, a search applied, TotalCount == 0
    public bool IsResultsOpen { get; set; }                      // the overlay; the view may close it
    public CatalogResultRow? HighlightedResult { get; set; }     // keyboard or hover; bound to the list selection
    public void MoveHighlight(int delta);                        // §5.5
    public RelayCommand SelectEntryCommand { get; }              // picks HighlightedResult; no-op when null

    public StationCatalogEntry? SelectedEntry { get; }           // the last picked entry
    public CatalogResultRow? DetailRow { get; }                  // HighlightedResult ?? the row of SelectedEntry
    public Bitmap? DetailLogo { get; }                           // null → the monogram

    internal Task PendingSearch { get; }                         // test seam: the latest scheduled search, applied when it completes
}

public sealed record CatalogFilterOption(string? Value, string Label)
{
    public static CatalogFilterOption All { get; } = new(null, "All");
    public override string ToString() => Label;
}

public sealed class CatalogResultRow : ObservableObject
{
    public CatalogResultRow(StationCatalogEntry entry);
    public StationCatalogEntry Entry { get; }
    public string Name { get; }             // entry.Name
    public string Monogram { get; }         // UiText.Initial(entry.Name): the station tiles' rule (first character, upper-invariant; "?" when empty)
    public string Subtitle { get; }         // City · FrequencyText · CountryLabel-or-Country, empty parts left out
    public string Kind { get; }             // Type · Genre, empty parts left out, Genre left out when equal to Type
    public string Location { get; }         // City, Region, CountryLabel-or-Country: non-empty, distinct, ", "-joined
    public string FrequencyText { get; }    // §5.4
    public string LanguageText { get; }     // entry.Language
    public string VotesText { get; }        // "1 vote", "4210 votes", "" when null
    public string Notes { get; }            // full text, wrapped by the view
    public string AutomationName { get; }   // $"{Name}, {Subtitle}"
    public Bitmap? Logo { get; set; }       // loaded for the applied rows, null → Monogram
}
```

`StationRowViewModel.Initial` moves to the same `UiText.Initial` helper so both tiles follow one rule.

**Behavior rules:**

- **Edit mode** never calls `GetCatalogAsync`; the catalog panel is not in the visual tree's visible part; `Save` never touches `Notes` (BHV-53 and the edit form unchanged, D62).
- **Add mode:** the constructor starts `GetCatalogAsync`; its completion is posted to the UI thread and sets the options, the status and runs the first search (empty text, no filters: the 50 most-voted stations, overlay closed). Typing during the load is kept and searched when it completes. Unavailable disables the search box, the filters and Clear; manual entry and Save work exactly as before.
- **Search scheduling** (D72): a `SearchText` change schedules with `searchDelay`; a filter change, Clear and the load completion schedule with no delay. Each schedule increments a generation on the UI thread, cancels the previous search's `CancellationTokenSource`, and runs `Task.Delay` (when > 0) then `StationCatalogQuery.Search` on the thread pool; the result goes through a `LatestValueDispatcher<…>` and is applied only if its generation is still current. Applying sets `Results`, `TotalCount`, `TotalCountText`, `HasNoMatches`, clears `HighlightedResult`, and starts the row logo loads (cancelling the previous batch). A search or filter change sets `IsResultsOpen = true`. `CloseRequested` cancels everything pending; nothing is applied afterwards.
- **Select** (`SelectEntryCommand`): `Name` = entry name truncated to `NameMaxLength` and trimmed at the end; `Tag` = `entry.Tag` truncated to `TagMaxLength`; `Url` = `entry.StreamUrl`; `SelectedEntry` = entry; `IsResultsOpen = false`; `HighlightedResult = null`. These go through the normal setters, so a stale validation message clears.
- **Save in Add mode** (D73): as today, plus `station.Notes = SelectedEntry is { Notes.Length: > 0 } e && string.Equals(urlText, e.StreamUrl, StringComparison.Ordinal) ? e.Notes : null`. A station entered by hand, or whose URL was changed after picking, gets no notes.
- **Detail logo:** a `DetailRow` change cancels the previous load and loads the new row's logo through `ICatalogLogoLoader`; failure leaves the monogram.

### 5.3 Texts (in `UiText`, exact)

| Name | Text |
|---|---|
| `SearchPlaceholder` | `Search by name, frequency or city…` |
| `CatalogLoading` | `Loading the station catalog…` |
| `CatalogUnavailable` | `Catalog unavailable — enter stream details manually` |
| `CatalogNoMatch` | `No stations match — adjust filters or enter the stream manually` |
| `ManualEntrySeparator` | `Or enter stream details manually` |
| `CatalogStatus(count, generatedUtc)` | `8274 stations · catalog updated 2026-09-25` (`yyyy-MM-dd` of the UTC date); `8274 stations` when `generatedUtc` is null |
| `ResultCount(shown, total)` | `""` when total is 0; `1 match`; `214 matches` when shown == total; `Showing 50 of 214 matches` otherwise |

Numbers are invariant-culture integers without grouping, so every text is machine-independent.

### 5.4 Frequency display (`UiText.FrequencyText`)

`""` when `FrequencyFm` is empty. Otherwise, parsed with `CultureInfo.InvariantCulture`: 64–108 → `"101.5 FM"`; an integer ≥ 150 → `"1593 kHz"` (medium wave, as the Wikipedia lists give it); anything else → the raw value. Display only; the stored data is unchanged.

### 5.5 Dialog and keyboard contract (`StationEditorDialog`)

- Window: `Width="680" MinWidth="620" SizeToContent="Height"`, content height within about 620 so it fits 1366×768. Opening or closing the results overlay never resizes the window.
- Top to bottom in Add mode: search box, filter row (five ComboBoxes, each with a visible field label, plus Clear), catalog status line, results overlay (max height about 230, virtualized, over the content below the search box; the no-match text inside it; the "Showing …" footer), detail pane (while `DetailRow` is not null: logo or monogram, name, location, frequency, `Kind`, language, votes, full wrapped notes), a separator with `Or enter stream details manually`, then the unchanged Name / Description-or-genre / Stream URL fields and hint. Edit mode: exactly today's form.
- **Focus on open:** Add mode → the search box (BHV-52 amended, D68); Edit mode → the name field (unchanged).
- **Search box keys** (handled in the view, calling the view model): Down → opens the overlay if results exist, then `MoveHighlight(+1)`; Up → `MoveHighlight(-1)`; Enter → when `IsResultsOpen` and `HighlightedResult` is set, `SelectEntryCommand` and mark handled, otherwise not handled, so the default button saves (BHV-52/BHV-65); Escape is never handled by the catalog, so `IsCancel` closes the dialog.
- `MoveHighlight(delta)`: no highlight + `delta > 0` → the first row; no highlight + `delta < 0` → stays none; otherwise index + delta, clamped to the first and last row (no wrap).
- **Results list:** not a tab stop; pointer over a row highlights it; pressing a row highlights and selects it.
- **Tab order** (`TabIndex`): search 0, Country 1, City 2, Type 3, Genre 4, Language 5, Clear 6, Name 7, Description 8, Stream URL 9, Save 10, Cancel 11, Delete 12 (Edit mode only).
- **Automation names** (QG-03): `Search stations`, `Country filter`, `City filter`, `Type filter`, `Genre filter`, `Language filter`, `Clear search and filters` (Clear button), `Station catalog results` (list), each result `CatalogResultRow.AutomationName`, `Station details` (detail pane), `Catalog status` (status line). The existing `Station name`, `Description / genre` and `Stream URL · https://…` names are unchanged.
- The code-behind `Field()` mapping (`"Name"` → NameField, `"Url"` → UrlField) is unchanged.

## 6. Settings: `Station.Notes`

Owner: core lane (Phase 2). Exactly (D61, the `ScheduleEntry.TimeZone` precedent, QA-N3):

```csharp
using System.Text.Json.Serialization;

namespace DialShift.Core;

public sealed class Station
{
    // Id, Name, Url, Tag and ToString unchanged.

    /// <summary>Free-text notes about the station, set when it was added from the station catalog (brief 3). Null
    /// for stations entered by hand and for every station saved before this field existed.</summary>
    /// <remarks>Null is not written, so a station without notes serializes exactly as before; adding the field does
    /// not change <see cref="Settings.Version"/>. SettingsStore validates only Name and Url, so any string loads.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }
}
```

`Settings.Version` stays `1`; `SettingsStore` is unchanged. Known hazard, pinned rather than fixed (like CT-SET-11's `"TimeZone": 123`): a hand-edited `"Notes": 123` is a JSON type error, so the file takes the `.unreadable-*` path.

## 7. File ownership map and sequencing

No two lanes own the same file in the same phase. A lane that needs a change in a file it does not own asks the orchestrator.

| Phase | Lane (agent) | Owns (creates or edits) | Must not touch |
|---|---|---|---|
| 0 | spec (`spec-architect`) | `.claude/agents/{data-engineer,app-integration-engineer,docs-engineer,qa-auditor,design-reviewer,perf-auditor}.md`, `docs/catalog-contracts.md`, `docs/acceptance-matrix.md`, `docs/decisions.md`, the brief's status line, `CLAUDE.md` roster, `AGENTS.md` brief list | any code |
| 1 | data (`data-engineer`) | `data/build/**` (new `app_catalog.py`, `build_all.py`), `data/output/app-catalog.json`, `data/output/dialshift-radio-catalog.xlsx` (regenerated), `data/README.md`, `.claude/skills/radio-catalog-pipeline/SKILL.md`, `.claude/rules/data-catalog-only.md`; `data/countries/**`, `data/collections/**` only for a validation-driven YAML fix | the country CSVs in `data/canonical/` (byte-identical: no `--refresh`; `collection-*.csv` may move with the live radio-browser lookup, committed together with the JSON built from it), any C# |
| 2 | core (`core-engineer`) | `DialShift.Core/Catalog/StationCatalogEntry.cs`, `StationCatalogIndex.cs`, `StationCatalogQuery.cs`; `DialShift.Core/Models/Station.cs` (`Notes`) | `Settings.Version`, `SettingsStore`, anything outside `DialShift.Core` |
| 2–3 | test (`test-engineer`) | `DialShift.Tests/Catalog/CatalogTests.cs` (suite entry) and its parts (`CatalogQueryTests.cs`, `CatalogSettingsTests.cs`, `CatalogExportContractTests.cs`, `CatalogProviderTests.cs`, `CatalogFixtures.cs`), the `Catalog` line in `DialShift.Tests/Program.cs` | product code |
| 3 | integration (`app-integration-engineer`) | the Content item in `DialShift.App/DialShift.App.csproj`; `DialShift.App/Services/{ICatalogProvider,CatalogProvider,ICatalogLogoLoader,CatalogLogoLoader}.cs`; the two catalog registrations in `DialShift.App/AppComposition.cs`; the catalog check in `DialShift.App/Smoke/`; the catalog assertions in `scripts/verify-mac-app.sh`, `scripts/verify-win-package.ps1` | view models, views, Core |
| 4a | UI (`ui-engineer`) | `DialShift.App/ViewModels/{StationEditorViewModel,PageViewModel,MainWindowViewModel,StationsPageViewModel,UiText}.cs`, new `CatalogResultRow.cs` and `CatalogFilterOption.cs`, `DialShift.App/Views/Dialogs/StationEditorDialog.axaml(.cs)`, catalog styles in `DialShift.App/App.axaml` if needed, the `MainWindowViewModel` argument lines in `AppComposition.cs`; **in the same commit** the compile-only wiring in `DialShift.Tests/Ui/UiRig.cs` (+ a `FakeCatalogProvider`/`FakeLogoLoader` in `UiFakes.cs`) and the amended HS-02 focus check in `DialShift.Tests/Ui/HeadlessUiTests.cs` (D68) | Services, Core, data, other tests |
| 4b | test (`test-engineer`) | the CAT view-model checks in `DialShift.Tests/Ui/ViewModelTests.cs`, the CAT headless checks in `HeadlessUiTests.cs`, fixtures in `UiFakes.cs`/`UiRig.cs` | product code |
| 5 | test (`test-engineer`) | fixes to its own checks only | product code |
| 5 | perf (`perf-auditor`) | `DialShift.Tests/Catalog/CatalogPerfTests.cs`, the `CatalogPerf` line in `DialShift.Tests/Program.cs` | everything else (read-only) |
| 5 | QA (`qa-auditor`), design (`design-reviewer`) | nothing (read-only reports) | everything |
| 6 | docs (`docs-engineer`) | `README.md`, `docs/**`, `AGENTS.md`, `CLAUDE.md`, `data/README.md` prose, `THIRD-PARTY-NOTICES.md` text (with the release lane) | code, scripts, workflows, generated data |
| 6 | release (`release-engineer`) | `.github/workflows/*.yml` if a step is needed, `.claude/skills/release-packaging/SKILL.md`, the "Station catalog data" source list the notices need (D76) | code, `CHANGELOG.md` release section (release-time only) |

**Dependencies.** Phase 1 and Phase 2 start together. Phase 3 starts with Phase 2 and needs Core's `StationCatalogIndex` before `CatalogProvider` compiles, so the core lane lands `StationCatalogEntry.cs` + `StationCatalogIndex.cs` first; the integration lane lands `ICatalogProvider.cs` + `ICatalogLogoLoader.cs` as its first commit. Phase 4a starts when both first commits are in; 4b follows 4a. Phase 3's end-to-end publish checks need Phase 1's JSON. Phase 5 starts when 1–4 are merged on the branch; Phase 6 after Phase 5 is clean. Implementer ≠ reviewer ≠ auditor: `qa-auditor`, `design-reviewer` and `perf-auditor` never review their own work, and no implementing lane reviews itself.

## 8. Test plan by CAT row

Check names start with the row id (`"CAT-06 …"`), as the timezone rows start with `"Row n …"`, so `grep` finds a row's evidence. "Windows evidence" says what this Mac cannot supply (D75, §9).

| Row | Proof (suite / check / command) | Local macOS evidence | Windows evidence still needed |
|---|---|---|---|
| CAT-01 | `app_catalog.py --self-test`; the pipeline log line (§2.3); `Catalog`: "CAT-01 …" reads the checked-in JSON and `data/canonical/*.csv` (the repo root found by walking up to `DialShift.slnx`; SKIP with a reason when absent), with a small RFC 4180 reader in the test: 18 keys in order, schema 1, every entry valid, no duplicate `(name, country, stream_url)`, the `(country, name, stream_url)` set equals the Working rows that pass the URL rule, order rule, collections labelled `Internet (collections)`, `tag` non-empty | yes | none (OS-independent data; the `Catalog` suite also runs on `windows-latest` for the DoD) |
| CAT-02 | `dotnet build -c Debug` and `-c Release`, `dotnet publish -r osx-arm64 --self-contained` and `-r win-x64 --self-contained`: `app-catalog.json` in each output, `cmp` equal to `data/output/app-catalog.json`; `Catalog`: "CAT-02 …" the test output folder has the file (the Content item flows through the project reference) | yes (win-x64 cross-published) | `windows-latest` build and publish (CI matrix) |
| CAT-03 | `scripts/build-mac-app.sh` + `scripts/verify-mac-app.sh --zip` (the JSON check, after `ditto` and `unzip`); `pwsh scripts/build.ps1 -SkipTests` + `verify-win-package.ps1` on this Mac (D58); the bundle smoke's catalog check | yes | the Windows native smoke from the zip (the file resolves next to `DialShift.exe` at run time) |
| CAT-04 | `Catalog`: "CAT-04 …" for missing, empty, not JSON, truncated, `schema_version` 2 / `"1"` / missing, `stations` missing / null, every entry invalid, 10,001 entries, a directory, an unreadable file (SKIP on Windows): Unavailable, exactly one `catalog.unavailable` (`RecordingAppLog`), no exception; the default location in the test process loads the real file; `UiViewModels`: "CAT-04 …" a provider that never completes leaves the dialog responsive and manual Save working; the smoke catalog check | yes | the Windows smoke catalog check |
| CAT-05 | `Catalog`: "CAT-05 …" `ResolveLocation` for unset / empty / whitespace / relative / absolute values, a fixture loaded through the override, no fallback for a relative value | yes | `windows-latest` (drive-letter and UNC rules of `IsPathFullyQualified`) |
| CAT-06 | `Catalog`: "CAT-06 …" the §3.3 `Fold` examples, name / name_local / city matching, genre and notes not searched, whitespace collapse, Greek with tonos and final sigma, German umlauts and ß, French accents | yes | `windows-latest` (the OS normalization data behind `string.Normalize`) |
| CAT-07 | `Catalog`: "CAT-07 …" `1015`, `101.5`, `101,5`, `101.5 FM` match `101.5`; `101` prefix; `1`, `12345`, `101.555` are not frequency queries; AM `1593`; empty `FrequencyFm` never matches; tier 3 ranks below name matches | yes | DoD only |
| CAT-08 | `Catalog`: "CAT-08 …" each filter alone, all five ANDed with text, `null` = All, ordinal equality, `AvailableValues` distinct / non-empty / ordered / country labels; `UiViewModels`: "CAT-08 …" options = All + catalog values, a filter change re-searches, Clear resets | yes | DoD only |
| CAT-09 | `Catalog`: "CAT-09 …" tier order, votes desc (null = 0), every tie-break, same result for a shuffled catalog, cap 50 with the true total, `cap < 1` throws; `UiViewModels`: `TotalCountText` for 0, 1, 50-of-50 and 50-of-214 | yes | DoD only |
| CAT-10 | `UiViewModels`: "CAT-10 …" select fills Name/Tag/Url (with the 100/160 truncation), `SelectedEntry`, detail texts (notes full, votes, frequency, language, location); Save stores `Notes` only when the URL is unchanged; `HeadlessUi`: pick by keyboard fills the real text boxes (found by automation name) and the detail pane shows the notes | yes | DoD only |
| CAT-11 | `HeadlessUi` + `UiViewModels`: every existing HS-02 check green (the focus check amended per D68), plus "CAT-11 …" Edit mode shows no catalog panel, name field focused, BHV-52 messages unchanged | yes | `windows-latest` headless run |
| CAT-12 | `HeadlessUi`: "CAT-12 …" focus on `Search stations` in Add mode; type, Down, Enter fills; Enter with no highlight saves (validation); Esc closes with nothing added; the Tab order of §5.5 | yes | `windows-latest` headless; native keyboard focus in NC-01 |
| CAT-13 | `UiViewModels`: "CAT-13 …" loading text, unavailable text with search disabled and manual add working end to end, no-match text; design review | yes | none |
| CAT-14 | `HeadlessUi`: "CAT-14 …" every new automation name, no clipped text in the dialog at 620 wide and with the window at 780×650 (the existing clipping helper), `SearchText` returns before any search runs (results unchanged synchronously); design review of the screenshots; the typing half of CAT-16 | yes | Windows native visual pass (Segoe UI metrics), NC-01 |
| CAT-15 | `Catalog`: "CAT-15 …" a golden pre-brief `settings.json` string loads and saves byte-identical (null `Notes` writes nothing); a set `Notes` round-trips; `Settings.Version` is 1 in memory and on disk; an old file loads with `Notes == null` and no `.unreadable-*`; the `"Notes": 123` hazard pinned | yes | DoD only |
| CAT-16 | `CatalogPerf` (perf lane): load and search medians at the real count and at 10,000 synthetic entries, asserted against D69's budgets | yes (Apple Silicon) | none required; Windows hardware is not measured (reported, not gated) |
| CAT-17 | `qa-auditor` grep: no station names, URLs, frequencies or countries as C# literals outside test fixtures; `UiViewModels`: "CAT-17 …" `CatalogStatusText` shows `generated_utc`; `data/README` documents the refresh | yes | none |
| CAT-18 | docs review (`docs-engineer`, then `qa-auditor`): README, `data/README`, this matrix, D59+, the XLSX README tab, `THIRD-PARTY-NOTICES` catalog sources (D76) | yes | none |

Machine independence (as for the timezone rows): the harness already runs under the invariant culture; catalog tests never depend on the host culture, zone, network or the real catalog's contents (fixtures are inline strings written to `TempDirectory`), except CAT-01/02/04's explicit checks of the checked-in file.

## 9. Evidence gate while CI is unavailable

The private repository's GitHub Actions jobs are refused for billing (D58), and pushing to `origin` is out of scope for this brief. So (D75):

- **Per-phase gate:** on this Mac, `export PATH="$HOME/.dotnet:$PATH"; dotnet build DialShift.slnx -c Release -warnaserror` and `dotnet run --project DialShift.Tests -c Release`, both pasted as real output, plus the lane's own commands (§8).
- **Status values:** a CAT row is `GREEN` when all of its evidence exists; a row whose macOS evidence is complete and whose only gap is the column "Windows evidence still needed" is `WINDOWS-PENDING` with that gap named; rows that need a person or real hardware stay `NATIVE-PENDING` with their §9 native check. Rows marked "DoD only" or "none" can be `GREEN` on local evidence.
- **Phase 5 closes** when every CAT row is `GREEN`, `WINDOWS-PENDING` or `NATIVE-PENDING` with evidence, and the final report lists every Windows gap for the next CI run or Windows machine.

Phase 0 baseline at `78b122e` (this Mac, 2026-09-25): `dotnet build DialShift.slnx -c Release -warnaserror` → `Build succeeded. 0 Warning(s) 0 Error(s)`; `dotnet run --project DialShift.Tests -c Release` → `1771 passed, 5 skipped; 24/24 suites green.`
