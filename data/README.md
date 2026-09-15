# DialShift Radio Data Section

A clean, scalable station catalog for the DialShift app: **Greece, France, Germany** today,
more countries by dropping in one YAML file. Everything regenerates from data files —
nothing in here is hand-maintained twice, and nothing in the Python is station data.

## What you get

| artifact | where | what it is |
|---|---|---|
| **Multi-tab XLSX** | `output/dialshift-radio-catalog.xlsx` | the deliverable. Opens nicely in macOS Numbers |
| **Per-country CSVs** | `canonical/<country>-stations.csv` | clean, one row per station |
| **Country data files** | `countries/<name>.yaml` | THE source of truth for curated station facts |
| **Collection data files** | `collections/<name>.yaml` | curated genre folders of internet radio (Ambient & Chill) |
| **Build scripts** | `build/` | one generic pipeline + the XLSX writer |
| **Raw caches** | `raw/<CC>/` | downloaded sources; disposable, auto-regenerated, **not in git** |

## The XLSX tabs

- **README** — how to use the workbook + how to import stations into the app.
- **Import Ready** — every station with a working stream, all countries, most popular first.
  This is the tab you pick stations from.
- **Greece / France / Germany** — full lists, including stations without a stream.
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

## Adding a station to the DialShift app

The app's *Add a frequency* dialog needs exactly three things:

| app field | take it from column |
|---|---|
| Station name | `Station Name` |
| Description / genre | `Description / Genre` |
| Stream URL | `Stream URL` (must be a direct audio URL — MP3, AAC or HLS) |

Copy from the **Import Ready** tab. A webpage URL will not play; `Stream URL` is always
a direct stream in this catalog.

## Canonical schema (18 frozen columns)

`country · name · name_local · city · region · frequency_fm · type · genre · language ·
political_leaning · internet_only · stream_url · codec · bitrate · stream_status · votes ·
notes · source`

- `type` — Music · News & Talk · Political · Sports · Religious · Municipal · Public · Other
- `political_leaning` — Left / Center-Left / Center / Center-Right / Right / State / Municipal /
  **None** (not applicable/unknown). Only filled where well documented.
- `internet_only` — Yes (web-only) · No (terrestrial) · Unknown (not in an official FM directory)
- `stream_status` — Working / Down / No stream found (from radio-browser.info checks)

## How it works (single source of truth, no double-maintained data)

1. **`countries/<name>.yaml`** holds the only hand-maintained data: curated station facts
   (name, city, type, genre, political leaning, optional pinned stream URL), national-programme
   consolidation rules (the ERT network), city aliases, and an optional Wikipedia list URL.
   Station data never lives in Python code.
2. **`build/build_stations.py`** is THE pipeline for every country (same logic, zero per-country code):
   fetch → Wikipedia FM list (optional) → radio-browser stream pool → national consolidation →
   curated overlay → unmatched extras → dedupe → canonical rows.
3. **`build/build_all.py`** runs the pipeline for all `countries/*.yaml`, writes the canonical
   CSVs and the multi-tab XLSX.
4. **`raw/<CC>/`** is only a cache of downloaded sources (Wikipedia page, radio-browser JSON).
   It is regenerated automatically when missing, so it never needs to be committed or edited —
   no dead data accumulates.

### Adding a new country

1. Copy a YAML, fill in: `code`, `name`, `language_default`, `city_aliases`, `curated` entries.
2. Optional: `wiki.url` for a Wikipedia FM list, and `focus_areas` for local shortlist tabs
   (a focus area is a city — `{city: Paris, label: Paris}` — or a region —
   `{city: Carcassonne, region: Aude, label: Aude}`). Curated entries opt in with `focus: <label>`;
   terrestrial rows in a focus city are added automatically.
3. Run the build. Done — no Python changes.

### Adding a collection (genre folder)

Drop a YAML in `data/collections/` with `code`, `name`, `description` (the tab's README text),
and a `stations:` list — each entry: `name`, `match` (radio-browser search keys), `genre`
(the app's Description/Genre tag, e.g. `SomaFM · Ambient / downtempo`), optional pinned `url`,
`language`, `notes`. Unpinned entries resolve their stream from radio-browser at build time.
Collections become their own tab + `canonical/collection-<code>.csv`.

## Refreshing the data

```bash
cd data
uv venv .venv                          # once (needs `uv` installed)
uv pip install --python .venv/bin/python openpyxl pyyaml
.venv/bin/python build/build_all.py --refresh     # re-downloads sources and rebuilds
```

`--refresh` re-downloads Wikipedia + radio-browser; without it the cached raw data is reused.

## Data sources

- **radio-browser.info** — open community directory: stream URLs, codec/bitrate, tags,
  language, votes, last-check status. The backbone for streams.
- **Wikipedia "List of radio stations in X"** — FM landscape: frequencies, cities, descriptions.
- **Curated facts (YAML)** — for major stations only: correct local names, genres, political
  leaning, and pinned official stream URLs (ERT, live24.gr-hosted Greek majors, German ARD
  dispatcher streams). Pinned URLs were verified with curl at build time; identity facts are
  well-documented (Wikipedia, media).

## Maintenance rules

- Never edit `canonical/`, `output/`, or `raw/` by hand — they are generated; edits get overwritten.
- Curated facts go ONLY in `countries/*.yaml`.
- Streams rot: prefer letting curated entries resolve their stream dynamically from
  radio-browser (omit `url` in the YAML); pin a `url` only for official streams you have verified.
- Same-name stations in different cities are kept separate (matching is by name + state).

## Future: schedules / programs

Planned (see `docs/radio-data-plan.md`): per-station program grids joinable by station name,
for searching shows of a specific station. Data shape sketched in the plan; not built yet.

Station names and streams belong to their respective owners; DialShift is unaffiliated.
