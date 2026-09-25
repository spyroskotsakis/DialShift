# Catalog contracts (brief 3, Phase 0): frozen signatures, rules, ownership and test plan

> **Status: frozen 2026-09-25 by the spec lane, before any implementation lane starts.** Normative for brief 3
> (`docs/add-station-catalog-search.md`, "the brief"; § numbers without a file name are this document's). Lanes
> implement the signatures here **verbatim**; a change goes through the spec lane and a new decision in
> `docs/decisions.md` (D59–D68 are the brief's §11 defaults, D69–D76 the ambiguities resolved in Phase 0, D77–D80
> post-freeze amendments). The
> acceptance rows are CAT-01..18 in `docs/acceptance-matrix.md` §11. Phase 0 wrote the contracts down without
> stubbing them, so no lane ever found dead or throwing placeholder code. §4 was amended after Phase 3 (D80) so that
> it states exactly what `d05180b` and `3a10dae` implement.

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

Taken on this Mac (Apple Silicon, .NET 10 SDK, `data/canonical/*.csv` at `78b122e` unless a row names another commit). They drive D69–D76; the later rows drive D79 and D80.

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
| `frequency_fm` shapes in the exported JSON (measured at `7a6e1e5` for D79) | empty 7,482; `dd.d` 483 and `ddd.d` 239 (all 87.1–108.0, always one decimal, `89.0`-style trailing zeros in 87 entries); `ddd` 14 and `dddd` 55 (522–1650 and one `8500`, all kHz); `Shortwave` 1 | No field says AM or FM, but the value does: 722 FM, 69 kHz, 1 neither (`BandOf`, §3.3, D79) |
| FM and kHz entries whose frequency digits are equal | 8 digit strings (`891`, `918`, `927`, `936`, `945`, `972`, `1008`, `1017`); `101.7` found 3 FM and 2 kHz entries before D79 | A decimal separator or a band token restricts the band (D79) |
| Notes that are only `tags:` (radio-browser extras with no tags) | part of 7,330 `tags: …` notes | `tags:` alone becomes `""`; Wikipedia `_emphasis_` markers are stripped (D71) |
| Logos that are not http(s) | 36; empty: 3,259 | Exporter and provider keep only http(s) logos (D71, D74) |
| Distinct values: type / genre / language / city | 7 / 132 / 173 / 434 | Flat, data-driven filter lists (D72) |
| JSON size, one station per line | about 3.5 MB | Loose file, `MaxFileBytes` 32 MiB guard (D74) |
| Search over 8,665 rows × 3 fields with `CompareInfo.IndexOf(IgnoreCase \| IgnoreNonSpace)` per call | 0.8–11.2 ms (`münchen` 11.2 ms, `αθήνα` 8.6 ms) | Over the 10 ms budget before ranking: fold once, match ordinally (D70) |
| Same search on precomputed folded keys | 0.12–0.22 ms; folding all rows once: 7.3 ms | Folding happens once, at load, inside the 50 ms budget |
| `AppContext.BaseDirectory` in the D51 bundle layout (apphost in `Contents/MacOS`, `.dll` symlinked from `Contents/Resources/app`) | `…/Contents/Resources/app/` | The JSON, a non-Mach-O file, stays in `Contents/Resources/app` and is found there (D60) |
| `LatestValueDispatcher<T>` | coalesces pushes into one UI-thread post; **no delay** | It is not a debounce; the 200 ms delay is separate (D72) |
| `SettingsStore.ValidUrl` | absolute URI, scheme `http`/`https`, non-empty host | The pipeline mirrors it, plus the 2,048 limit |
| Full load of the real file (8,274 stations, Release, Apple M4 Max; measured at `d05180b` for D80), D69's definition: a fresh `CatalogProvider` per load, median of 7 loads in one process after one warm-up load | **33.1 ms** (integration lane); **32.5–34.0 ms** over 5 processes (spec lane, re-measured at `d489465`) | CAT-16's load budget (< 50 ms, D69) is met |
| Same loads after many repetitions (tier-1 JIT) | about 14 ms (integration lane); 16–17 ms after 200 loads (spec lane) | Headroom once the code is JIT-compiled |
| **First load in a fresh process: the only load the app ever performs** (D60, §4.4: one lazy load per process) | **40–68 ms** (integration lane); **60–62 ms** by the `catalog.loaded` line, 63–66 ms wall, over 8 standalone processes that had not used `System.Text.Json` before (spec lane) | **Can exceed 50 ms.** D69 reports this cold load and does not gate it, so CAT-16 gates the warm median (§8). The load runs off the UI thread, and meanwhile the dialog shows `CatalogLoading` and keeps what the user types (§5.2) |
| Parse strategy on the real file (integration lane, D80) | `DeserializeAsync` over the file stream took about twice the parse time of one pass over the bytes in memory; calling `JsonSerializer.Deserialize` once per element made the whole load about 40 % slower than one reused element converter | §4.2 step 3 reads the file into memory and uses one element converter |
| Bundle smoke with the catalog check (`3a10dae`, integration lane) | **33 checks** (32 before brief 3) | §4.4 |

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
| `tag` | string | `common.app_tag(row)` of `data/build/common.py`, non-empty | missing/`null` → `""` |
| any other key | — | never written | ignored (forward compatibility) |

A JSON **type** mismatch on a known key (for example `"votes":"12"`) makes the whole file Unavailable: the pipeline never writes one, so it means a corrupt or foreign file.

### 2.2 Inclusion, dedupe, order (D71)

- **Source rows:** every row the run writes to `data/canonical/*.csv` (all `countries/*.yaml` and all `collections/*.yaml`), taken from the same in-memory rows.
- **URL rule** (mirrors `SettingsStore.ValidUrl` plus BHV-52's limit): `stream_url.strip()` has scheme `http` or `https` (lower case, as `urlsplit` returns it), a non-empty `hostname`, no whitespace or control character, and at most 2,048 characters.
- **Included:** `stream_status == "Working"` and the URL rule holds. Nothing else is filtered.
- **Dedupe key** (the pipeline's final dedupe, per country): `(country, norm(name).replace(' ', ''), norm_city(city, aliases), url_norm(stream_url))` with `norm`, `norm_city`, `url_norm` from `data/build/common.py`, where `aliases` is the `city_aliases` block of the row's own country YAML (checked by `common.city_aliases`; `{}` for a collection). Aliases come only from the YAMLs; the Python holds no alias list (`common.CITY_ALIASES` is gone, D71 update). A key matches only when it equals `norm(city)` verbatim, so every key must be in `norm()` form. On a collision the row with the higher `common.row_score` (in `data/build/common.py`, shared with `build_stations.py`) stays, ties keep the first.
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

`python data/build/app_catalog.py --self-test` runs stdlib-only fixture checks (no network, no openpyxl): a Down row, an empty URL, an `ftp://` URL, a 2,049-character URL and a host-less URL are excluded; a duplicate is removed and then reported by rule 5; `tag` equals `common.app_tag`; a collection row gets `Internet` / `Internet (collections)`; the placeholders, `internet_only`, `bitrate`, `votes`, `notes` and `logo` normalize per §2.1; the order rule holds; the writer's output parses and round-trips byte-for-byte. `app_catalog.py` imports only the standard library and `data/build/common.py` (never `build_all.py`, which needs openpyxl, or `build_stations.py`), so the self-test stays stdlib-only (D71). `build_all.py` calls `build_app_catalog`, `validate_app_catalog` and `write_app_catalog` after the CSVs and before the XLSX.

### 2.5 The workbook (`data/output/dialshift-radio-catalog.xlsx`, D78)

The workbook stays the manual-copy fallback (brief §5.1). It is regenerated in the same run as the JSON and changes in exactly three ways, all intended:

1. The README tab's "How to add a station to the DialShift app" section points at the in-app picker, with manual copy as the fallback (brief §5.1).
2. Every tab's **"Description / Genre"** column is `common.app_tag(row)`, the text the in-app pick fills (§2.1 `tag`, D73). The Import Ready and country tabs already were; the collection tab (`Ambient & Chill`) moves from `genre` to `app_tag` (for example `1.FM · Chillout lounge` becomes `Music · 1.FM · Chillout lounge`, 24 rows today), so manual copy and the picker agree (D78).
3. Votes, and the collection rows themselves, may move with the live radio-browser lookup (the `collection-*.csv` allowance in §7).

Everything else in the workbook, and every canonical CSV column, is unchanged.

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

    /// <summary>Copies <paramref name="entries"/> (order kept) and folds Name, NameLocal and City, extracts the
    /// FrequencyFm digits and computes the FrequencyFm band (<see cref="StationCatalogQuery.BandOf"/>) of every entry.
    /// O(n); throws ArgumentNullException for a null list or a null entry, and never throws on text content (§3.3, D77, D79).</summary>
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

/// <summary>How a FrequencyFm value reads (§3.3, §5.4, D79): FM MHz, AM kHz, or neither.</summary>
public enum FrequencyBand { None, Fm, Kilohertz }

/// <summary>Pure filter-and-rank engine over a <see cref="StationCatalogIndex"/> (§3.3). No I/O.</summary>
public static class StationCatalogQuery
{
    public const int DefaultCap = 50;

    public static CatalogSearchResult Search(StationCatalogIndex catalog, string? text, CatalogFilters filters, int cap = DefaultCap);

    public static IReadOnlyList<CatalogFilterValue> AvailableValues(IReadOnlyList<StationCatalogEntry> entries, CatalogField field);

    /// <summary>The §3.3 band of a FrequencyFm value; the one classification behind both the frequency query and the
    /// §5.4 label. Throws ArgumentNullException for null; never throws on content (D79).</summary>
    public static FrequencyBand BandOf(string frequencyFm);

    /// <summary>The §3.3 normalization. Internal: used by the index and the query; the tests see it through InternalsVisibleTo.</summary>
    internal static string Fold(string value);
}
```

**Amendment of the brief's signature (D70):** the brief wrote `Search(IReadOnlyList<StationCatalogEntry>, …)`. `Search` takes the `StationCatalogIndex` instead, because culture-aware matching per call (`CompareInfo.IndexOf` with `IgnoreCase | IgnoreNonSpace`, per field) measured up to 11.2 ms on the real data before ranking, while folding every entry once took 7.3 ms (§1). The index is built once, inside the load. Files: `StationCatalogEntry.cs`, `StationCatalogIndex.cs`, `StationCatalogQuery.cs` (with `CatalogFilters`, `CatalogSearchResult`, `CatalogField`, `CatalogFilterValue`).

### 3.3 Matching and ranking (normative, testable)

**Fold(s)** (replaces `CompareOptions.IgnoreCase | IgnoreNonSpace`, same intent, culture-independent, D70):

1. Replace every unpaired surrogate **and every U+FFFE** with U+FFFD, then `Normalize(NormalizationForm.FormKD)` (compatibility decomposition: `ﬁ` → `fi`, full-width → ASCII, `é` → `e` + U+0301). An unpaired surrogate is a high surrogate (U+D800–U+DBFF) not followed by a low surrogate, or a low surrogate (U+DC00–U+DFFF) not preceded by a high surrogate; a valid pair is kept. `Normalize` throws `ArgumentException` on an unpaired surrogate and on U+FFFE, and on nothing else (an exhaustive probe of every code point on .NET 10 / ICU found U+FFFE to be the only non-surrogate it rejects; U+FFFF and the other noncharacters pass), so the replacement is what makes `Fold` total (D77). A fast path may skip the replacement scan when the string contains no code unit in U+D800–U+DFFF and no U+FFFE (for example `IndexOfAnyInRange('\uD800', '\uDFFF') < 0 && IndexOf('\uFFFE') < 0`); `string.IsNormalized` is not a validity test, because it throws on the same inputs.
2. Drop every character whose `CharUnicodeInfo.GetUnicodeCategory` is `NonSpacingMark` or `EnclosingMark`.
3. `char.ToLowerInvariant` per character, then map `ς` → `σ`, `ß` → `ss`, `æ` → `ae`, `œ` → `oe`, `ø` → `o`, `ł` → `l`, `đ` → `d`, `ı` → `i`.
4. Every run of `char.IsWhiteSpace` characters becomes one space; trim both ends.

Examples that are tests: `Fold("München") == "munchen"`, `Fold("ΑΘΗΝΑΣ") == Fold("αθήνας") == "αθηνασ"`, `Fold("Straße") == "strasse"`, `Fold("  Radio\t  FM ") == "radio fm"`, `Fold("ﬁp") == "fip"`, `Fold("\uD800") == "\uFFFD"`, `Fold("a\uDC00b") == "a\uFFFDb"`, `Fold("\uD83D\uDCFB") == "\uD83D\uDCFB"` (a valid pair, unchanged), `Fold("\uFFFE") == "\uFFFD"`, `Fold("a\uFFFEb") == "a\uFFFDb"`. `Fold` never throws for a non-null string.

**Query.** `q = Fold(text ?? "")`. If `q == ""` there is no text constraint.

**Frequency query** (D79, amends the Phase 0 rule). Let `t = text.Trim()`. `t` is a frequency query when it matches

```text
^(?:(fm|am)\s*)?([0-9]{2,4})(?:([.,])([0-9]{0,2}))?(?:\s*(fm|mhz|am|khz))?$
```

with `RegexOptions.IgnoreCase | RegexOptions.CultureInvariant` (`[0-9]` is ASCII only; `\s` is any Unicode white space, so a no-break space works). Groups: `L` the leading band token, `I` the integer digits, `S` the decimal separator, `R` the decimals, `T` the trailing token. Then:

1. **Query band.** FM is implied by a separator `S` (a kHz value is an integer, so `101.7` is FM notation), by `L` or `T` = `fm`, or by `T` = `mhz`; kHz is implied by `L` or `T` = `am`, or by `T` = `khz` (case-insensitive). If FM and kHz are both implied (`AM 101.7`, `101.5 kHz`, `FM 1593 kHz`), `t` is **not** a frequency query. If neither is, the band is Any (bare digits such as `1017`, which people use for either).
2. **Query digits.** `R'` = `R` without its last character when `R` has two characters and the last is `0` (`50` → `5`, `00` → `0`, `05` and `0` and `5` unchanged). `D = I + R'`. The catalog writes FM with exactly one decimal (§1), so `101.50` and `101.5` find the same stations while `101.0` stays `1010` and keeps its precision (it finds 101.0, not every 101.x).
3. **Entry keys** (computed once in the index, D70). `F` = the ASCII digits of `FrequencyFm` in order. `BandOf(FrequencyFm)` is `Fm` when `decimal.TryParse(FrequencyFm, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v)` succeeds and `64 ≤ v ≤ 108`; `Kilohertz` when it succeeds, `FrequencyFm` has no `.` and `v ≥ 150`; `None` otherwise (for example `""`, `Shortwave`, `108.5`, `149`, `1593.0`, or a value with a sign, white space or a thousands separator). No field of the JSON says AM or FM; the value does, with the thresholds §5.4 labels by, so the band a token selects is the one the user sees on the row.
4. **Match.** A frequency match when `F != ""`, `F.StartsWith(D, StringComparison.Ordinal)`, and the query band is Any or equals `BandOf(FrequencyFm)`. An entry whose band is `None` therefore matches only band-Any queries.

The tiers, the total order and the text tiers 0–2 are unchanged: `t` still text-matches through `q` as before (`FM 101.5` also finds a name containing `fm 101.5`). Per query the frequency parse allocates a fixed amount that does not depend on the catalog size: the `Trim` result (none when there is nothing to trim), the regex `Match` with its groups, and `D`, about 1 KB in all (core review of D77/D79), besides `q = Fold(text)` itself. Per entry it is one ordinal `StartsWith` and one enum compare over precomputed keys, so the no-allocation-per-entry rule holds.

Examples that are tests (catalog of entries whose names contain no digits: FM `101.0`, FM `101.5`, FM `101.7`, kHz `1017`, FM `89.0`, kHz `1593`, an empty `FrequencyFm`, `Shortwave`):

| Query | Frequency matches | Why |
|---|---|---|
| `1015`, `101.5`, `101,5`, `101.5 FM`, `101.5fm`, `101.5 MHz`, `101,5\u00A0fm` | FM 101.5 | as before D79 |
| `FM 101.5`, `fm101.5`, `FM 101,5 MHz`, `FM 1015` | FM 101.5 | leading band token |
| `101.50`, `101,50 MHz` | FM 101.5 | trailing zero dropped |
| `101.0`, `101.00`, `1010` | FM 101.0 | `101.00` → `1010`; `101.0` keeps its decimal |
| `89.0`, `89.00`, `890` | FM 89.0 | |
| `1017` | FM 101.7 and kHz 1017 | bare digits: band Any |
| `101.7`, `FM 1017`, `1017 MHz` | FM 101.7 only | FM implied |
| `AM 1017`, `1017 AM`, `1017 kHz`, `am1017` | kHz 1017 only | kHz implied |
| `101` | FM 101.0, FM 101.5, FM 101.7, kHz 1017 | prefix, band Any |
| `101.`, `FM 101` | FM 101.0, FM 101.5, FM 101.7 | FM implied |
| `1593`, `1593 kHz`, `AM 1593`, `159` | kHz 1593 | |
| `AM 101.7`, `101.5 kHz`, `FM 1593 kHz`, `MHz 101.5`, `kHz 1593`, `UKW 101.5`, `101.5 FMX`, `1`, `12345`, `101.555`, `١٠١٫٥`, `１０１.５` | none (not frequency queries; text match only) | conflicting bands; `mhz`/`khz` only trail; tokens outside the set; shape |

The empty and `Shortwave` entries match no row. The results within a row follow the §3.3 total order (tier 3, then votes).

Not adopted by D79 (recorded there): German transliteration (`koeln` for Köln), other band words (`UKW`, `MW`, `OM`, `PO`), and separator-insensitive matching of frequencies written inside names (`90.3` for `NDR 90,3`).

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

**Result.** `TotalCount` = number of matches; `Items` = the first `min(cap, TotalCount)` of the total order. `cap < 1` → `ArgumentOutOfRangeException`; `catalog` or `filters` null → `ArgumentNullException`. Those are the only exceptions: no `text` content throws, including an unpaired surrogate (D77). The scan is O(n) over precomputed keys with a bounded top-`cap` selection; no allocation per non-matching entry.

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

**`ResolveLocation`** (D60, D74, D80). The value is trimmed first; the first matching row applies. It throws only `ArgumentNullException` for a null argument:

| `DIALSHIFT_CATALOG_PATH` | Result |
|---|---|
| unset, empty or whitespace | `(Path.Combine(baseDirectory, FileName), AppFolder, null)` |
| not `Path.IsPathFullyQualified` (relative) | `(null, Override, "DIALSHIFT_CATALOG_PATH must be an absolute path.")`: the load is Unavailable; **no fallback** to the app folder, so a wrong override is visible |
| fully qualified, but `Path.GetFullPath` throws `ArgumentException`, `NotSupportedException` or `PathTooLongException` (for example an embedded NUL character) | `(null, Override, "DIALSHIFT_CATALOG_PATH is not a valid path.")`: Unavailable, never an exception while the app is composed (D80) |
| fully qualified | `(Path.GetFullPath(value), Override, null)`; any file name (tests use fixtures) |

Production passes `Environment.GetEnvironmentVariable` and `AppContext.BaseDirectory` (never the current directory).

**Load** (once, inside `Task.Run`; every exception caught; D74, D80). Each Unavailable carries the reason below as `CatalogLoadResult.Message`, exactly as written (with its final period):

1. `location.Problem` → Unavailable(Problem).
2. The path is a directory → `the path is a directory, not a file.`; it does not exist → `the file does not exist.`. The file is opened read-only (`FileShare.Read`); a stream that cannot seek (a device or a pipe, which has no length) → `the path is not a regular file.`; `Length > MaxFileBytes` → `the file is <n> bytes, more than the 33554432 bytes allowed.`. An `IOException` or `UnauthorizedAccessException` at any step → `the file could not be read.`.
3. **Parse in one pass from memory (D80).** The whole size-checked file is read into one byte array; a leading UTF-8 byte order mark (`EF BB BF`) is skipped explicitly, because parsing bytes, unlike parsing a stream, does not skip it; then `JsonSerializer.Deserialize` over the bytes into **private** DTOs (all members nullable) with `new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }` and otherwise default options (unknown keys skipped; comments, trailing commas and quoted numbers rejected). The `stations` array is read by a private converter that builds **at most `MaxEntries` station objects** and only counts the elements after them (`Utf8JsonReader.Skip`), so a hostile 32 MiB file of `{}` elements allocates at most 10,000 DTOs; it obtains the element converter once from the options and reuses it for every element. A `null` element is kept as null (skipped in step 6). A `JsonException` (a syntax error, a type mismatch on a known key, `stations` that is not an array) → `the file is not valid catalog JSON.`; a `null` root → `the file holds no JSON object.`.
4. `schema_version` missing → `schema_version is missing (expected 1).`; ≠ `SupportedSchemaVersion` → `schema_version 2 is not supported (expected 1).`. `stations` missing or `null` → `the file has no stations array.`.
5. More than `MaxEntries` elements (the converter's count) → `the file lists <n> stations, more than the 10000 supported.`. This is checked **before** mapping, so such a file logs only the one `catalog.unavailable`, never `catalog.entries_skipped`.
6. Map each station per the "App tolerates" column of §2.1: trim every string, `null` → `""`; skip a null element, an empty `name` or `country`, and a `stream_url` that fails `SettingsStore.ValidUrl` or exceeds 2,048 characters; `logo` that fails `SettingsStore.ValidUrl` → `""`; `bitrate` ≤ 0 → null; `votes` < 0 → null. Skipped > 0 → one `catalog.entries_skipped` warning.
7. No usable entry → `the file has no usable station entry.`.
8. `generated_utc` via `DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)`, else null.
9. `new StationCatalogIndex(entries)` (folding is part of the load budget) → Loaded, `Message = null`.

Any other exception (for example from `StationCatalogIndex`) → `the catalog could not be loaded.`. The time that `catalog.loaded` reports runs from the start of the load task, before step 1, to the built index: it is the "load" of CAT-16 (§8, D69).

**Log events** (`IAppLog`, lower-case dotted, D74, D80; the redactor shortens the home prefix in paths):

| Event | Level | When | Message (exact shape) |
|---|---|---|---|
| `catalog.loaded` | Info | Loaded | `Loaded 8274 stations (generated 2026-09-25T12:00:00Z) from <path> [app folder] in 33 ms.` The bracket is the literal text `[app folder]` or `[DIALSHIFT_CATALOG_PATH]`; `(generated unknown)` when `generated_utc` is missing or unparsable |
| `catalog.unavailable` | Warn | every Unavailable, exactly once per process | `Station catalog unavailable: <reason> (<path>).`, where `<reason>` is the step's reason **without its final period** and `<path>` is the resolved path, or the literal `DIALSHIFT_CATALOG_PATH` when there is none (step 1). Example: `Station catalog unavailable: the file does not exist (/Applications/DialShift.app/Contents/Resources/app/app-catalog.json).` Carries the exception when there is one. An exception thrown by the log itself is swallowed, so a failing log never turns Unavailable into a fault |
| `catalog.entries_skipped` | Warn | step 6 skipped ≥ 1 | `Skipped 3 of 8277 catalog entries without a name, country or valid stream URL.` |

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

Rules (D66, D80):

- **Requests.** Only `SettingsStore.ValidUrl` URLs are requested; any other URL, or a token already cancelled at the call, returns `null` at once without a request. Every request carries `User-Agent: DialShift/<version>`, the App assembly version as `Version.ToString(3)` (`0` when there is none), because some logo hosts, Wikimedia among them, refuse requests without one (D80). The loader owns its `HttpClient`, with `HttpClient.Timeout` infinite, and disposes the handler with it.
- **Bounds.** At most `MaxConcurrentDownloads` downloads run at once. Each attempt, the request and its body, is cancelled after `Timeout`, counted from the moment it gets a download slot. A non-success status, or a `Content-Length` over `MaxBytes`, fails without reading the body; a body that grows past `MaxBytes` is abandoned at the first byte over. Decoding uses `Bitmap.DecodeToWidth(stream, DecodeWidth)`; network and decoding work run on the thread pool, never on the calling thread.
- **One download per URL (D80).** Concurrent `LoadAsync` calls for the same URL share one download. A caller's cancellation ends only that caller's wait (it gets `null`). When every caller of a download has cancelled before it finishes, the download itself is cancelled and its result is **not cached**, so a stale search neither fills the download queue nor poisons the cache; the next call for that URL starts a new download.
- **Cache.** Results, failures included (as `null`: an HTTP error, a timeout, a network failure, a body over `MaxBytes`, bytes that do not decode), are cached per URL, least recently used first out past `CacheCapacity` URLs, for the process, so a dead logo is fetched once. A cache hit returns a completed task. Evicted bitmaps are not disposed, because a view may still show them.
- **Dispose** cancels every download in flight and clears the cache; a later `LoadAsync` returns `null`.
- No log line per logo (dead logos are normal). DI passes a `SocketsHttpHandler { ConnectTimeout = Timeout }`; the tests pass a handler of their own.

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
- **Smoke:** one new `--smoke-test` check, `Catalog loads from the app folder`, right after the launch check: the app's own `ICatalogProvider` returns Loaded with at least one station, within 10 s, and `ResolveLocation(Environment.GetEnvironmentVariable, AppContext.BaseDirectory)` gives `CatalogLocationSource.AppFolder`. **A set `DIALSHIFT_CATALOG_PATH` fails the check even when that file loads (D80)**, because an override would not prove the packaged file. The detail names the state, the count, `generated_utc` (or `unknown`) and the path; on failure also the source and the Unavailable reason. The smoke total rises by one on each OS (the bundle smoke: 33 checks at `3a10dae`), and the docs lane updates every count that cites it.
- **`scripts/verify-mac-app.sh`:** `Contents/Resources/app/app-catalog.json` is a regular file (not a symlink), parses as JSON with the integer `schema_version` 1 (a JSON `true` or `"1"` fails) and a non-empty `stations` array, using a parser that accepts `null` (`/usr/bin/python3 -c 'import json,sys; …'`; `plutil` rejects `null`). Runs for the `.app`, and for both `ditto` and `unzip` extractions with `--zip`. **The script now requires `/usr/bin/python3` (D80)** and checks for it with its other tools. On a Mac without the Xcode Command Line Tools that path is only a stub that offers to install them, so the script fails there; its header says so. The `macos-latest` runners and the dev box have the tools; the users' Macs never run this script.
- **`scripts/verify-win-package.ps1`:** `app-catalog.json` is a file next to `DialShift.exe`, `Get-Content -Raw -Encoding utf8 | ConvertFrom-Json` succeeds, `schema_version` is an integer (`[int]` or `[long]`) equal to 1 (`-eq` alone would accept `"1"` and `$true`), and `stations` is an array with at least one station.

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

`""` when `FrequencyFm` is empty. Otherwise by `StationCatalogQuery.BandOf(FrequencyFm)` (§3.3, D79): `Fm` → `"101.5 FM"`; `Kilohertz` → `"1593 kHz"` (medium wave, as the Wikipedia lists give it); `None` → the raw value. The thresholds are the Phase 0 ones (64–108; an integer ≥ 150); calling `BandOf` instead of parsing again keeps the label and the band a search token selects identical. Display only; the stored data is unchanged.

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
| 1 | data (`data-engineer`) | `data/build/**` (new `app_catalog.py`, `build_all.py`), `data/output/app-catalog.json`, `data/output/dialshift-radio-catalog.xlsx` (regenerated; the README tab and the collection tab's "Description / Genre" column change, §2.5, D78), `data/README.md`, `.claude/skills/radio-catalog-pipeline/SKILL.md`, `.claude/rules/data-catalog-only.md`; `data/countries/**`, `data/collections/**` only for a validation-driven YAML fix | the country CSVs in `data/canonical/` (byte-identical: no `--refresh`; `collection-*.csv` may move with the live radio-browser lookup, committed together with the JSON built from it), any C# |
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
| CAT-04 | `Catalog`: "CAT-04 …" for missing, empty, not JSON, truncated, `schema_version` 2 / `"1"` / missing, `stations` missing / null, every entry invalid, 10,001 entries (and no `catalog.entries_skipped`, §4.2 step 5), a directory, an unreadable file (SKIP on Windows): Unavailable with the §4.2 reason as `Message`, exactly one `catalog.unavailable` (`RecordingAppLog`) in the §4.2 shape, no exception; a fixture with a UTF-8 BOM loads (D80); `catalog.loaded` names `[DIALSHIFT_CATALOG_PATH]` for a fixture and says `(generated unknown)` without `generated_utc`; a station whose `name` contains U+FFFE (raw or as `\ufffe`) still loads (D77), while an escaped lone surrogate (`\ud800`) is a JSON error that makes the file Unavailable (`not valid catalog JSON`, measured: `System.Text.Json` rejects it before `Fold` sees it); the default location in the test process loads the real file; `UiViewModels`: "CAT-04 …" a provider that never completes leaves the dialog responsive and manual Save working; the smoke catalog check | yes | the Windows smoke catalog check |
| CAT-05 | `Catalog`: "CAT-05 …" `ResolveLocation` for unset / empty / whitespace / relative / absolute values and a fully qualified value with an embedded NUL (`is not a valid path.`, no exception, D80), a fixture loaded through the override, no fallback for a relative value | yes | `windows-latest` (drive-letter and UNC rules of `IsPathFullyQualified`) |
| CAT-06 | `Catalog`: "CAT-06 …" the §3.3 `Fold` examples, name / name_local / city matching, genre and notes not searched, whitespace collapse, Greek with tonos and final sigma, German umlauts and ß, French accents; lone surrogates (D77): the §3.3 surrogate and U+FFFE `Fold` examples, `Search(StationCatalogIndex.Empty, "\uD800", CatalogFilters.None)` and the same with `"\uFFFE"` return an empty result instead of throwing, and an index over a `Name` with a lone surrogate or a U+FFFE builds and matches a query containing the same character | yes | `windows-latest` (the OS normalization data behind `string.Normalize`) |
| CAT-07 | `Catalog`: "CAT-07 …" every row of the §3.3 frequency example table (D79) as its own named case, over the table's catalog; in particular the D79 changes: `FM 101.5` finds FM 101.5 (leading token), `101.50` finds FM 101.5 (trailing zero), `101.7` finds FM 101.7 but not kHz 1017 while `1017` finds both, `AM 1017` / `1017 kHz` find only kHz 1017; `101.` no longer finds kHz 1017; conflicting bands (`AM 101.7`, `101.5 kHz`) and `UKW 101.5` are not frequency queries; `BandOf` over `101.5`, `89.0`, `108.0`, `64`, `1017`, `150`, `8500` (`Fm` for the first four, `Kilohertz` for the last three), `108.5` / `149` / `1593.0` / `87,5` / `1,017` / ` 101.5` / `+101.5` / `Shortwave` / `""` (None), and null throws; empty `FrequencyFm` and band `None` entries never match a banded query; a frequency query ANDs with a filter; tier 3 ranks below name matches. `UiViewModels`: "CAT-07 …" `UiText.FrequencyText` ends in ` FM` exactly when `BandOf` is `Fm` and in ` kHz` exactly when it is `Kilohertz`, over the same inputs. `koeln` does not find Köln (pinned as the D79 non-goal) | yes | DoD only |
| CAT-08 | `Catalog`: "CAT-08 …" each filter alone, all five ANDed with text, `null` = All, ordinal equality, `AvailableValues` distinct / non-empty / ordered / country labels; `UiViewModels`: "CAT-08 …" options = All + catalog values, a filter change re-searches, Clear resets | yes | DoD only |
| CAT-09 | `Catalog`: "CAT-09 …" tier order, votes desc (null = 0), every tie-break, same result for a shuffled catalog, cap 50 with the true total, `cap < 1` throws; `UiViewModels`: `TotalCountText` for 0, 1, 50-of-50 and 50-of-214 | yes | DoD only |
| CAT-10 | `UiViewModels`: "CAT-10 …" select fills Name/Tag/Url (with the 100/160 truncation), `SelectedEntry`, detail texts (notes full, votes, frequency, language, location); Save stores `Notes` only when the URL is unchanged; `HeadlessUi`: pick by keyboard fills the real text boxes (found by automation name) and the detail pane shows the notes; `HeadlessUi` (Skia is initialized there, which decoding needs): "CAT-10 …" the logo loader (§4.3, D80) with a test handler: a non-http(s) URL is never requested, a PNG decodes to `DecodeWidth`, a failure is cached (one request for two calls), concurrent calls for one URL make one request, when every caller cancels the request is cancelled and the next call requests again, a body over `MaxBytes` gives `null`, every request carries `User-Agent: DialShift/…` | yes | DoD only |
| CAT-11 | `HeadlessUi` + `UiViewModels`: every existing HS-02 check green (the focus check amended per D68), plus "CAT-11 …" Edit mode shows no catalog panel, name field focused, BHV-52 messages unchanged | yes | `windows-latest` headless run |
| CAT-12 | `HeadlessUi`: "CAT-12 …" focus on `Search stations` in Add mode; type, Down, Enter fills; Enter with no highlight saves (validation); Esc closes with nothing added; the Tab order of §5.5 | yes | `windows-latest` headless; native keyboard focus in NC-01 |
| CAT-13 | `UiViewModels`: "CAT-13 …" loading text, unavailable text with search disabled and manual add working end to end, no-match text; design review | yes | none |
| CAT-14 | `HeadlessUi`: "CAT-14 …" every new automation name, no clipped text in the dialog at 620 wide and with the window at 780×650 (the existing clipping helper), `SearchText` returns before any search runs (results unchanged synchronously); design review of the screenshots; the typing half of CAT-16 | yes | Windows native visual pass (Segoe UI metrics), NC-01 |
| CAT-15 | `Catalog`: "CAT-15 …" a golden pre-brief `settings.json` string loads and saves byte-identical (null `Notes` writes nothing); a set `Notes` round-trips; `Settings.Version` is 1 in memory and on disk; an old file loads with `Notes == null` and no `.unreadable-*`; the `"Notes": 123` hazard pinned | yes | DoD only |
| CAT-16 | `CatalogPerf` (perf lane): load and search medians at the real count and at 10,000 synthetic entries, asserted against D69's budgets. **"Load" is D69's:** one `GetCatalogAsync` on a fresh `CatalogProvider` over the file (§4.2 steps 1–9, the time `catalog.loaded` reports), the median of 7 loads in one Release process after one warm-up load. The **first load in a fresh process**, the only load the app performs, is printed next to it and not gated (D69): it measured 40–68 ms (integration lane) and 60–62 ms (spec lane) against the 50 ms budget, while the warm median measured 33 ms (§1) | yes (Apple Silicon) | none required; Windows hardware is not measured (reported, not gated) |
| CAT-17 | `qa-auditor` grep: no station names, URLs, frequencies or countries as C# literals outside test fixtures; `UiViewModels`: "CAT-17 …" `CatalogStatusText` shows `generated_utc`; `data/README` documents the refresh | yes | none |
| CAT-18 | docs review (`docs-engineer`, then `qa-auditor`): README, `data/README`, this matrix, D59+, the XLSX README tab, `THIRD-PARTY-NOTICES` catalog sources (D76) | yes | none |

Machine independence (as for the timezone rows): the harness already runs under the invariant culture; catalog tests never depend on the host culture, zone, network or the real catalog's contents (fixtures are inline strings written to `TempDirectory`), except CAT-01/02/04's explicit checks of the checked-in file.

## 9. Evidence gate while CI is unavailable

The private repository's GitHub Actions jobs are refused for billing (D58), and pushing to `origin` is out of scope for this brief. So (D75):

- **Per-phase gate:** on this Mac, `export PATH="$HOME/.dotnet:$PATH"; dotnet build DialShift.slnx -c Release -warnaserror` and `dotnet run --project DialShift.Tests -c Release`, both pasted as real output, plus the lane's own commands (§8).
- **Status values:** a CAT row is `GREEN` when all of its evidence exists; a row whose macOS evidence is complete and whose only gap is the column "Windows evidence still needed" is `WINDOWS-PENDING` with that gap named; rows that need a person or real hardware stay `NATIVE-PENDING` with their §9 native check. Rows marked "DoD only" or "none" can be `GREEN` on local evidence.
- **Phase 5 closes** when every CAT row is `GREEN`, `WINDOWS-PENDING` or `NATIVE-PENDING` with evidence, and the final report lists every Windows gap for the next CI run or Windows machine.

Phase 3 evidence so far (integration lane, `d05180b`, `3a10dae`, merged at `d489465`): the real file loads 8,274 stations with a warm median of 33.1 ms and a first cold load of 40–68 ms (§1, CAT-16); the bundle smoke passes 33 checks, including the catalog check. The `Catalog` suite's CAT-02..05 checks are still to be written by the test lane, so those rows stay `TODO` (matrix §11).

Phase 0 baseline at `78b122e` (this Mac, 2026-09-25): `dotnet build DialShift.slnx -c Release -warnaserror` → `Build succeeded. 0 Warning(s) 0 Error(s)`; `dotnet run --project DialShift.Tests -c Release` → `1771 passed, 5 skipped; 24/24 suites green.`
