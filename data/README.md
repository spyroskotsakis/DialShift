# DialShift Radio Data Section

A clean, scalable station catalog for the DialShift app: **Greece, France, Germany**, a
curated **United States** tech-and-famous-stations list, and a **United Kingdom** national
list, more countries by dropping in one YAML file. Everything regenerates from data files —
nothing in here is hand-maintained twice, and nothing in the Python is station data.

## What you get

| artifact | where | what it is |
|---|---|---|
| **App catalog** | `output/app-catalog.json` | the station list the app's Add-station search reads; generated, validated, checked in |
| **Multi-tab XLSX** | `output/dialshift-radio-catalog.xlsx` | the human-readable deliverable. Opens nicely in macOS Numbers |
| **Per-country CSVs** | `canonical/<country>-stations.csv` | clean, one row per station |
| **Country data files** | `countries/<name>.yaml` | THE source of truth for curated station facts |
| **Collection data files** | `collections/<name>.yaml` | curated genre folders of internet radio (Ambient & Chill) |
| **Language table** | `languages.yaml` | the app catalog's language names, aliases and dropped non-languages (the only place for language facts) |
| **Frequency band words** | `frequency-bands.yaml` | the words a station's frequency may be instead of an FM or kHz value (`Shortwave`) |
| **Build scripts** | `build/` | one generic pipeline + the XLSX writer |
| **Raw caches** | `raw/<CC>/` | downloaded sources; disposable, auto-regenerated, **not in git** |

## The XLSX tabs

- **README** — how to use the workbook + how to add stations to the app (in-app search first,
  manual copy as the fallback).
- **Import Ready** — every station with a working stream, all countries, most popular first.
  This is the tab to copy from when you enter a station by hand.
- **Greece / France / Germany / United Kingdom / United States** — full lists, including
  stations without a stream. The US and UK tabs are curated national lists (not the whole
  dial); their stations without a public stream say so in Notes.
- **Focus tabs — Munich, Paris, Toulouse, Aude** — local shortlists: the FM landscape of each
  place plus the networks based there (Munich: Bayern 1-3, BR24, Antenne Bayern, Gong 96.3,
  Charivari, egoFM…; Paris: the national networks + community stations like Libertaire, Courtoisie,
  Chante France; Toulouse: Sud Radio, Toulouse FM, Canal Sud, Ràdio Occitània…; Aude: Grand Sud FM,
  Pyrénées FM, RCF Pays d'Aude, Ici Occitanie…). Stations with no public stream still appear
  (marked *No stream found*) so you see the whole local dial.
- **Summary** — counts by country/type and top cities.
- **Collections** — curated genre folders of internet radio (e.g. **Ambient & Chill**: the
  SomaFM ambient family around the app's default stations + the best ambient/downtempo/chillout
  streams worldwide). Each collection is one YAML in `collections/` and becomes one tab.
- **Logos** — every tab has a **Logo URL** column; the focus tabs (Munich, Paris, Toulouse,
  Aude) and collections also show the actual logo image (from radio-browser favicons or
  pinned `logo:` URLs in the YAMLs). Missing logos = the source has none.

## Adding a station to the DialShift app

**In the app (the normal way).** *Stations → + Add station* opens the *Add a frequency* dialog with
a search box over the built-in catalog (`output/app-catalog.json`, below): type a station name, a
city or a frequency (`1015` or `101.5` for FM 101.5), narrow with the Country, City, Type, Genre
and Language filters, and pick a result (click it, or Down and Enter). It fills the three fields (and keeps the station's notes); edit them if you
like, then Save.

**By hand (the fallback)** — for a station that is not in the catalog, or when the app says
*Catalog unavailable*: copy three columns of the XLSX **Import Ready** tab into the fields under
*Or enter stream details manually*.

| app field | take it from column |
|---|---|
| Station name | `Station Name` |
| Description / genre | `Description / Genre` |
| Stream URL | `Stream URL` (must be a direct audio URL — MP3, AAC or HLS) |

A webpage URL will not play; `Stream URL` is always a direct stream in this catalog.

## The app catalog (`output/app-catalog.json`)

The one file the app reads. `build/build_all.py` writes it on every run, from the same rows it
writes to `canonical/*.csv` (after the CSVs, before the XLSX); `build/app_catalog.py` holds the
export, the validation and the writer. It is **generated, never hand-edited, and checked in**
like the XLSX, so a fresh `dotnet build` needs no Python. The exact contract is
`docs/catalog-contracts.md` §2 (decisions D59, D69, D71, D84).

- **Shape:** `{"schema_version":1,"generated_utc":"yyyy-MM-ddTHH:mm:ssZ","stations":[…]}`, UTF-8,
  one station per line (readable diffs). Each station has exactly these 18 keys, in this order:
  `name · name_local · country · country_label · city · region · frequency_fm · type · genre ·
  language · internet_only · stream_url · codec · bitrate · votes · notes · logo · tag`.
  `country` is the YAML `code` and `country_label` its `name`; collections are
  `Internet` / `Internet (collections)`. `tag` is the Description/Genre text (`app_tag()` in
  `build/common.py`, the same text as the XLSX), so the app never recomputes it.
- **Inclusion:** `stream_status == Working` and a stream URL the app accepts (http/https, a host,
  no whitespace, at most 2,048 characters). Deduped per country with the pipeline's final key
  (name, city, stream URL). Ordered by country, votes (highest first), name, stream URL.
- **Normalization:** strings trimmed; the placeholders `—` (city) and `(unlisted)` (region) become
  `""`; `internet_only` is `true` only for `Yes`; `bitrate` is a positive integer or `null`;
  `votes` an integer ≥ 0 or `null`; Wikipedia `_emphasis_` markers are dropped from notes, and a
  note that is only a source label (`tags:`, `curated:`, …) followed by nothing or punctuation, or
  that has no letter or digit at all, becomes `""`; a radio-browser tag note (`tags: music,variety`)
  is shown readably as `Tags: music, variety` (split on `,` and `;`, each tag trimmed with single
  spaces and cut of the characters that are neither letters nor digits at both ends, so `### top 40
  ###` and `#dj` become `top 40` and `dj` while `hip-hop`, `r&b/urban`, `80's` keep their inner
  punctuation; a `+` right after the last letter (`dab+`) and a bracket or quote that pairs with one
  inside (`halle (saale)`) stay; a tag with no letter or digit left is dropped, repeats are dropped
  ignoring case, first spelling kept); every other note is kept as it is. The CSVs and the XLSX
  keep the pipeline's raw note; logos that are not http(s) become `""`.
- **Language (D84):** a list of single language names joined with `", "` (`English, German, Low
  German`), or `""`. The raw value is split on `,` and `;` only (never on `-`, `/` or `.`), and each
  token is looked up in `languages.yaml` (below) in any case: a canonical name stays itself, an alias
  becomes its name or names, a drop key disappears; names are kept once, first seen first. Only the
  JSON is normalized: the CSVs and the XLSX keep the raw value.
- **Validation (hard failure, same run):** schema version, the 18 keys and their types, non-empty
  name and country, valid stream URL, no duplicate `(name, country, stream_url)`, the count equals
  the Working rows that pass the URL rule, 1–10,000 entries, every `language` a clean list
  (non-empty names, trimmed, no `,` or `;`, none twice, none an alias or drop key), no note
  still in the raw `tags:` form, and every `frequency_fm` empty, an FM value with a `.` (64–108),
  a kHz integer (150 to 30,000, the top of shortwave) or one of the band words of
  `frequency-bands.yaml`, so free text never reaches the app's frequency column. On any problem
  the run prints every problem, exits non-zero and leaves the previous JSON (and the XLSX)
  untouched. If it fails on real data, fix the YAML, not the script. Every run logs two lines, for
  example
  `app-catalog: working=8277 url_excluded=7 duplicates_removed=0 exported=8270 -> data/output/app-catalog.json`
  and `app-catalog: languages=42 unknown=0`.
- **Language table (`languages.yaml`):** `languages` (canonical names: a language's usual English
  name), `aliases` (key → one name or a list: spellings, typos, native names, and dialects or
  varieties mapped to their language, e.g. `deutsch fränkisch: German`; Low German, Sorbian,
  Breton, Occitan and the other recognized regional languages keep their own name) and `drop`
  (tokens that are not a language: `instrumental`, `multilingual`, …). Keys are written lower case
  with single spaces; a key in another form, an unquoted `no:`/`yes:`, an alias to a name not
  listed, or a key both aliased and dropped stops the run before anything is written, naming every
  problem. A token the table does not know is **not** a failure: it is exported in capwords form
  and reported, one line each, as
  `app-catalog: unknown language "<token>" in <n> entries, exported as "<Token>"; add it to data/languages.yaml`.
  Add it under `languages`, `aliases` or `drop` and rebuild, so the run says `unknown=0` again.
- **Frequency band words (`frequency-bands.yaml`):** `band_words`, the words a frequency may be
  instead of a number (today only `Shortwave`, the Voice of Greece). Each is a short label (a
  letter, trimmed, at most 16 characters, listed once), compared exactly; a bad list stops the run
  before anything is written. A station whose frequency fails the rule above stops the run naming
  it: shorten the `freq` in its country YAML, or add the word here if it really is a band.
- **Self-test:** `.venv/bin/python build/app_catalog.py --self-test` (stdlib only, inline fixtures,
  no network).
- **In the app:** the build copies the file next to the app (the output and publish folders, and
  `Contents/Resources/app` inside the macOS bundle); the app finds it through
  `AppContext.BaseDirectory` and shows the catalog date (`generated_utc`). If the file is missing or
  unreadable, the dialog says *Catalog unavailable* and manual entry still works.
- **Development:** `DIALSHIFT_CATALOG_PATH=<absolute path>` makes the app read another file, for
  example this repo's `data/output/app-catalog.json` right after a pipeline run, without
  rebuilding. A relative path is refused (the catalog is then unavailable, no fallback).

## Canonical schema (19 frozen columns)

`country · name · name_local · city · region · frequency_fm · type · genre · language ·
political_leaning · internet_only · stream_url · codec · bitrate · stream_status · votes ·
notes · source · timezone`

- `type` — Music · News & Talk · Political · Sports · Religious · Municipal · Military ·
  Public · Other
- `political_leaning` — Left / Center-Left / Center / Center-Right / Right / State / Municipal /
  **None** (not applicable/unknown). Only filled where well documented.
- `internet_only` — Yes (web-only) · No (terrestrial) · Unknown (not in an official FM directory)
- `stream_status` — Working / Down / No stream found (from radio-browser.info checks)
- `language` — as the source gives it (radio-browser's comma-joined list, or the YAML value),
  kept as provenance; only the app catalog normalizes it (above)
- `timezone` — the station's IANA timezone, an empty string for internet collections. Every
  row gets one: a curated entry's own `timezone`, else its canonical city's
  `city_timezones` value, else the country's `timezone_default` (all three live in the
  country YAML; the app-catalog export validates every id).

## How it works (single source of truth, no double-maintained data)

1. **`countries/<name>.yaml`** holds the only hand-maintained data: curated station facts
   (name, city, type, genre, political leaning, optional pinned stream URL), national-programme
   consolidation rules (the ERT network), city aliases, the language defaults, and an optional
   Wikipedia list URL. **`languages.yaml`** holds the app catalog's language table and
   **`frequency-bands.yaml`** its frequency band words. Station, language and band data never live
   in Python code.
2. **`build/build_stations.py`** is THE pipeline for every country (same logic, zero per-country code):
   fetch → Wikipedia FM list (optional) → radio-browser stream pool → national consolidation →
   curated overlay → unmatched extras → dedupe → canonical rows.
3. **`build/build_all.py`** runs the pipeline for all `countries/*.yaml` and `collections/*.yaml`,
   writes the canonical CSVs, the validated app catalog (`build/app_catalog.py`) and the
   multi-tab XLSX.
4. **`raw/<CC>/`** is only a cache of downloaded sources (Wikipedia page, radio-browser JSON).
   It is regenerated automatically when missing, so it never needs to be committed or edited —
   no dead data accumulates.

### Adding a new country

1. Copy a YAML, fill in: `code` (ISO 3166-1 alpha-2), `name`, `language_default`, `city_aliases`,
   `curated` entries. `name` is the country's English name as radio-browser.info spells it: the
   build queries radio-browser by that name and uses it as the country's tab and filter label.
   `timezone_default` is the IANA timezone every row gets unless a city overrides it, and the
   optional `city_timezones` maps a **canonical city** (the spelling rows already show) to its
   IANA timezone — use it for overseas territories (`Guadeloupe: America/Guadeloupe`) and
   multi-zone countries (see `usa.yaml`); the build stops on an id that is not a valid IANA
   timezone.
   `city_aliases` maps a city spelling from the sources (radio-browser's `state`, a Wikipedia
   prefecture) to the city to show, e.g. `munchen: Munich`. It is the only alias list: the build
   reads it from each YAML (`build_stations.load_country`) and has none in code. A key is
   compared with the city in the same normalized form as station names (lowercase, accents
   stripped, Greek transliterated, punctuation turned into spaces), so write keys that way:
   `frankfurt am main`, not `Frankfurt-am-Main`. A key with capitals, accents or punctuation could
   never match, so the build stops on one and names the file, the key and the form to write.
   Only alias a spelling that really means that city: a region key such as
   `auvergne rhone alpes: Lyon` would move every station in the region to Lyon. The block is
   optional. Quote a key YAML reads as a boolean or a number
   (`'no': …`): the build stops on a block that is not all non-empty strings.
   Use it too for **one spelling per place** in the app's City filter: map every spelling of a
   place (typos, `ue`/`ü`, hyphen or accent variants: `thueringen`, `thuringen`, `thunringia` →
   `Thuringia`) to the spelling most of its rows already have. A region's names in two languages
   and its unambiguous abbreviations are one group too: each German state has one spelling
   (`bayern`, `bay`, `beieren` → `Bavaria`; `nrw`, `nordrhein westfalen` → `North Rhine-Westphalia`),
   the one with the most rows (a tie goes to the spelling more rows already had exactly). A value
   that wraps one unambiguous place in stream details, a region, a postal code or a country
   (`bayern munchen aac`, `magdeburg sachsen anhalt`, `vernon 27200`, `chania greece`) maps to
   that place. A value that names several places or only a country (`Deutschland (Germany)`) stays
   as it is: an alias cannot map to an empty city. Map a group to **one** city, and
   when the city's own normalized form is a key, that key must give the same city (`thuringen`,
   the form of `Thüringen`, maps to `Thuringia` too): the final dedupe normalizes the aliased city
   again, so the build stops on such a chain and names both keys. Leave a place alone when the
   spellings may be two places (`Korinthia`, the regional unit, and `Korinthos`, its town) or the
   target is itself ambiguous, and never alias a city named by a `curated` entry to something
   else: its pinned URL only applies in its own city. An alias can merge two rows that were the
   same station under two spellings (the final dedupe then keeps the richer one); check the row
   counts the build prints.
   `language_default` is the CSV language of a station whose source gives none. The optional
   `language_replace` maps a whole radio-browser language value, written lower case with single
   spaces, to the language the CSV gets instead (`greece.yaml`: `ancient greek: Greek`); the build
   stops on a key in another form.
2. Optional: `wiki.url` for a Wikipedia FM list, and `focus_areas` for local shortlist tabs
   (a focus area is a city — `{city: Paris, label: Paris}` — or a region —
   `{city: Carcassonne, region: Aude, label: Aude}`). Curated entries opt in with `focus: <label>`;
   terrestrial rows in a focus city are added automatically.
   `curated_only: true` (see `usa.yaml`) ships ONLY the `curated` entries: no Wikipedia rows and
   no radio-browser extras — for a country whose whole national directory would be unmanageable
   (the US's ~30,000-station list would dwarf the app catalog's budget). Curated entries still
   resolve their streams and logos from radio-browser by their `match` keys, and pinned `url`s
   work as usual.
3. Run the build. Done — no Python changes.

### Adding a collection (genre folder)

Drop a YAML in `data/collections/` with `code`, `name`, `description` (the tab's README text),
and a `stations:` list — each entry: `name`, `match` (radio-browser search keys), `genre`
(e.g. `SomaFM · Ambient / downtempo`; the tab's Description / Genre column and the app's
Description/Genre tag are the same `app_tag()` text, `<type> · <genre>` with an optional `type`
that defaults to `Music`), optional pinned `url`,
`language` (else the collection's `language_default`), `notes`. Unpinned entries resolve their
stream from radio-browser at build time.
Collections become their own tab + `canonical/collection-<code>.csv`.

## Refreshing the data

```bash
cd data
uv venv .venv                          # once (needs `uv` installed)
uv pip install --python .venv/bin/python openpyxl pyyaml pillow
.venv/bin/python build/build_all.py --refresh     # re-downloads sources and rebuilds
```

Refreshing is manual (D65): run with `--refresh`, check the `app-catalog:` lines (a refresh can
bring new language spellings: add every reported unknown language to `languages.yaml` and rerun
until `unknown=0`), then commit
the regenerated `canonical/*.csv`, `output/dialshift-radio-catalog.xlsx` and
`output/app-catalog.json` together. The next app build ships the new catalog; no app code changes.

`--refresh` re-downloads Wikipedia + radio-browser for the countries; without it the cached
`raw/` data is reused and the country CSVs come out byte-identical. Collections always look their
stations up on radio-browser live, so even without `--refresh` their CSV can change (usually the
votes); commit such a change only together with the JSON built from it.

## Health check (`build/check_stations.py`)

One pass over the **single consolidated file the app ships, `output/app-catalog.json`**
(all countries + collections in one list — nothing is checked per country), validating the
data and verifying, with real HTTP probes, that every stream URL serves audio and every logo
URL serves an image. This is the automated "is the whole library still healthy" sweep — no
manual per-record checking.

```bash
.venv/bin/python build/check_stations.py [catalog.json] \
    [--workers 25] [--timeout 8] [--limit N] [--no-streams] [--no-logos] \
    [--audit-rb] [--prune] [--strict] [--output report.json] [--broken-csv broken.csv]
```

**Data validation (errors fail the run, exit 1):** `schema_version == 1`; every station has a
non-empty trimmed name, an http(s) `stream_url` ≤ 2,048 characters, a valid `frequency_fm`
(empty, an FM value with a `.` in 64–108, a kHz integer in 150–30,000, or a
`frequency-bands.yaml` word — the app catalog's rule 9), and no duplicate of
(country, name, stream). **Warnings (reported, not fatal):** an empty language (the D84 drop of
non-languages like "Various Languages"/"Multilingual" — correct data), and the same name +
same stream in **different cities**, which is kept on purpose: local stations that share one
stream (relay networks, e.g. the Greek Star FM family or the Ecclesia network) are distinct
stations, never auto-merged. True duplicates (same name + same stream + same city, or one row
without a city) are errors.

**Stream verdicts:** `OK` (2xx + audio content type + bytes; HLS playlists count),
`FAIL` (an HTTP error, or a 2xx answer that is not audio — usually a stale landing page on a
rotten stream URL), `ERROR` (network/timeout). **Logo verdicts:** `OK` (2xx + `image/*`),
`FAIL` (HTTP error), `NONIMAGE` (2xx but not an image), `ERROR`, `SKIP` (no logo).

**Exit codes:** 0 = validation passed (broken streams/logos are listed but not fatal);
1 = validation errors, or `--strict` with any broken stream/logo; 2 = usage.

**Reading the results:** the console prints the verdict counts, the validation problems and the
first broken rows; `--output report.json` writes the full machine-readable report (verdicts,
every broken stream/logo with its URL and reason, validation errors + warnings, the
radio-browser gap audit, and the prune outcome); `--broken-csv` writes the broken streams and
logos as CSV.

**Pruning dead streams (`--prune`):** after probing, rewrite the catalog WITHOUT the stations
whose stream verdict is `FAIL` (a definite non-audio answer: an HTTP error or a non-audio
content type). `ERROR` rows (timeout/network trouble) are kept — a transient failure must not
drop a station. The file's shape (`schema_version`, `generated_utc`, `stations`) is unchanged,
so the app reads the pruned file as a normal catalog; the write is atomic (temp file + rename).
Data-validation errors block pruning (exit 1), and `--prune` refuses `--limit` (a limited run
would truncate the catalog). The pruned `app-catalog.json` is the file the app ships — this is
the publish-hygiene step between builds: run the check with `--prune` and commit the pruned
JSON. The next `build_all.py` run regenerates the full catalog from `canonical/` again, so
prune again after every rebuild.

**Caveats (why a reported failure may be environmental, not data):**

- Streams can be temporarily down or slow; a `TimeoutError`/`URLError` may pass on a retry.
  The probe uses browser-like headers + a Range request (some hosts answer empty to plain
  requests); it never reads beyond the first bytes.
- Some hosts block bots by IP/User-Agent or geofence (e.g. a 403 from a station site) — the
  logo/stream may still work inside the app.
- An `application/ogg` / `audio/ogg` stream counts as OK (OPUS); the app engines play it.

**Gap audit (`--audit-rb`):** compares the consolidated catalog against the cached
`raw/<code>/radio-browser.json` dumps and lists radio-browser stations that are working
(`lastcheckok=1`) and missing from the catalog, votes descending (missing with votes ≥ 2 are
the meaningful ones). The pipeline already imports radio-browser extras; this catches what its
filters suppress.

## Data sources

- **radio-browser.info** — open community directory: stream URLs, codec/bitrate, tags,
  language, votes, last-check status. The backbone for streams.
- **Wikipedia "List of radio stations in X"** — FM landscape: frequencies, cities, descriptions.
- **Curated facts (YAML)** — for major stations only: correct local names, genres, political
  leaning, and pinned official stream URLs (ERT, live24.gr-hosted Greek majors, German ARD
  dispatcher streams). Pinned URLs were verified with curl at build time; identity facts are
  well-documented (Wikipedia, media).

## Maintenance rules

- Never edit `canonical/`, `output/` (the XLSX and `app-catalog.json`), or `raw/` by hand — they
  are generated; edits get overwritten.
- Curated facts go ONLY in `countries/*.yaml` (and `collections/*.yaml`); language names,
  aliases and drops ONLY in `languages.yaml`.
- Streams rot: prefer letting curated entries resolve their stream dynamically from
  radio-browser (omit `url` in the YAML); pin a `url` only for official streams you have verified.
- Same-name stations in different cities are kept separate (matching is by name + state).

## Future: schedules / programs

Planned (see `docs/radio-data-plan.md`): per-station program grids joinable by station name,
for searching shows of a specific station. Data shape sketched in the plan; not built yet.

Station names and streams belong to their respective owners; DialShift is unaffiliated.
