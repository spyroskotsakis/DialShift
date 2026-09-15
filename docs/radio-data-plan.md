# Radio Data Section — Plan

> Status: **implemented** (Sept 2026) · Owner: DialShift · Station catalog per country → import-ready XLSX
> The final architecture is YAML-driven (see "What changed vs. this plan" at the bottom).

## Goal

A clean, scalable data section in this repo that:

1. Catalogs radio stations **per country** (Greece, France, Germany today, more later).
2. For each station: name, city, region, frequency, type, music genre, language,
   political leaning (where it matters), internet-only flag, and a **direct stream URL** that DialShift can play.
3. Produces a **multi-tab XLSX** (opens nicely in macOS Numbers) + per-country CSVs,
   via a single build script, so anyone can refresh the catalog later.
4. Leaves room for a future **schedules/programs** section (search shows of a specific station).

## Directory structure (final)

```
data/
├── README.md                     ← how this section works (user-facing)
├── countries/                    ← DATA ONLY — the single source of truth, one YAML per country
│   ├── greece.yaml               ← curated stations, ERT national programme rules, city aliases
│   ├── france.yaml
│   └── germany.yaml              ← + `focus:` block (Munich) for the Munich tab
├── raw/<CC>/                     ← disposable caches (Wikipedia md, radio-browser JSON);
│                                  auto-regenerated, gitignored, never hand-maintained
├── build/
│   ├── common.py                 ← shared helpers: normalization, classification, fetching
│   ├── build_stations.py         ← THE ONE generic pipeline for every country (no per-country code)
│   └── build_all.py              ← runs all countries → canonical CSVs + multi-tab XLSX
├── canonical/                    ← generated, checked-in clean CSVs (one per country)
└── output/
    └── dialshift-radio-catalog.xlsx   ← THE deliverable (multi-tab)
```

**Rule:** adding a country = add one YAML file. Adding a focus city (like Munich) =
add a `focus:` block in the country YAML. No Python changes, ever.

## Canonical schema (one CSV row per station, same columns everywhere)

| column | example | notes |
|---|---|---|
| `country` | DE | ISO2 code |
| `name` | Antenne Bayern | canonical Latin name |
| `name_local` | Antenne Bayern | native-script name when different |
| `city` | Ismaning (Munich) | broadcast city; focus cities annotated |
| `region` | Bavaria | région / Bundesland / prefecture |
| `frequency_fm` | 101.5 | blank for internet-only |
| `type` | Music | Music · News & Talk · Political · Sports · Religious · Municipal · Public · Mixed · Other |
| `genre` | Pop / Schlager | music style or program focus |
| `language` | German | or English / Multilingual |
| `political_leaning` | None | Left / Center-Left / Center / Center-Right / Right / State / Municipal / None — only filled where well documented |
| `internet_only` | No | Yes / No |
| `stream_url` | https://… | direct audio URL — the field DialShift needs |
| `codec` | MP3 | from radio-browser |
| `bitrate` | 128 | from radio-browser |
| `stream_status` | Working | Working / Down / No stream found |
| `votes` | 4210 | radio-browser popularity signal |
| `notes` | Bavaria's biggest private station | wiki description or curated note |
| `source` | wiki+radio-browser | where the row came from |

The 18 columns are frozen across countries — no per-country special columns.

## XLSX tab design (Numbers-compatible: plain openpyxl, freeze panes, auto-filter, column widths)

| tab | content |
|---|---|
| `README` | what the workbook is, how tabs work, how to import into DialShift |
| `Summary` | counts per country/type, top cities, Munich overview |
| `Import Ready` | all countries, only `stream_status = Working`, sorted country → votes — the tab you pick stations from |
| `Greece` / `France` / `Germany` | full per-country rows (including stations with no stream, so you see the whole FM landscape) |
| `Munich` | Germany rows for Munich area + Munich-based networks — your local shortlist |

## Data sources & the build pipeline

The SAME pipeline (`build/build_stations.py`) runs for every country, driven by its YAML:

1. **radio-browser.info** (`/stations/bycountry/<CountryName>`) — open community directory:
   stream URLs, codec/bitrate, tags, language, votes, last-check status. The backbone.
   (Gotcha: the endpoint substring-matches country names — always pass the full name, never the ISO code.)
2. **Wikipedia "List of radio stations in X"** (optional per country, `wiki.url` in YAML) —
   FM landscape: frequencies, cities, descriptions. Used by Greece.
3. **Curated facts (YAML)** — for major stations only: local names, city, type, genre, language,
   political leaning, and optionally a **pinned stream URL** (official ERT / live24.gr / ARD dispatcher
   streams, all curl-verified). Entries without a pinned URL resolve the highest-voted working
   stream from radio-browser at build time, so links don't rot in the data files.
4. **National-programme consolidation** (`national_programmes` in YAML) — ERT relays collapse
   into one row per programme with the official pinned stream.
5. **Merge & dedupe** — name normalization (transliteration, frequency-preserving variants),
   curated overlay, extras filtering, and a final dedupe on (name + city + stream URL).
   Same-name stations in different cities stay separate (matching is by name + state).

## Focus areas (Munich, Paris, Toulouse, Aude)

- Each country YAML can declare `focus_areas` — city-based (`{city: Paris, label: Paris}`)
  or region-based (`{city: Carcassonne, region: Aude, label: Aude}`) — each becomes a tab.
- Curated entries opt in with `focus: <label>` (or `focus: true` for the first area);
  terrestrial rows in a focus city are included automatically.
- Focus tabs include reference rows (`no_auto_stream: true`) for local stations whose
  stream isn't in radio-browser (e.g. Munich DAB+ 11C locals, Paris community radios),
  so the tab reflects the whole local dial.
- Munich FM presets (per city directories): BR24 90.0, Bayern 1 91.3, Bayern 3 97.3,
  egoFM 100.8, Antenne Bayern 101.3, Rock Antenne 94.5, Arabella 105.2, 2DAY 89.0,
  Charivari 95.5, Gong 96.3, Energy 93.3, TOP FM 106.4, community on 92.4.

## Greece special notes (implemented)

- ERT national programmes (Proto/Deytero/Trito/ERA Sport/Kosmos/Zeppelin/Voice of Greece/
  102FM/958FM) are consolidated to one row each with pinned official `radiostreaming.ert.gr` streams.
- ERT local stations (ERT Chania, ERT Heraklion, …) get one row each (city + main FM freq).
- Rebroadcast chains (Sfera/Derti/Kiss/Melodia relays) are kept as rows with a relay note.
- Live24-hosted streams (Skai, Real FM, Derti, Sfera, Best, Red, Menta, Sport FM, DeeJay …)
  come from radio-browser entries that already point at `*.live24.gr` hosts.

## Future: schedules / programs section (not built now)

```
data/schedules/
├── README.md
├── raw/<country>/<station-slug>.json     ← scraped program grids
└── canonical/<country>/<station-slug>.csv ← show, days, start, end, hosts
```

- One file per station, slug from canonical name (`antenne-bayern`).
- A `schedules_fetch.py` later; the station catalog's `name` + `stream_url` are the join keys.
- Search use-case: "what's on Real FM at 20:00 Tuesday" → query the canonical CSVs.

## Validation checklist (runs on every build)

1. Every row has non-empty `name`, `country`, `type`.
2. `stream_url` is http(s) and unique per (name, city) where possible.
3. Known curated stations exist in the output (spot-check list per country).
4. XLSX opens: read back with openpyxl; tabs match the design; no exotic features that break Numbers.
5. Munich tab non-empty; Greece contains the user's requested majors list.

## Collections (genre folders)

- `data/collections/*.yaml` — curated internet-radio folders with the SAME schema but
  `country='Internet'`; each becomes its own XLSX tab + `canonical/collection-<code>.csv`.
- `Ambient & Chill` (implemented): the SomaFM ambient family around the app defaults
  (Groove Salad / Drone Zone / Secret Agent) + best ambient/downtempo/chillout worldwide;
  every pinned URL verified with an audio response; genre doubles as the app Tag
  (`SomaFM · Ambient / downtempo`).

## Out of scope (explicitly)

- No streaming licenses/legal checks — all URLs come from public directories or official sites.
- No automatic import into DialShift settings (yet) — the `Import Ready` tab is the handoff.
- No end-to-end play-testing of every stream — status comes from radio-browser checks + spot curls.

## What changed vs. the original plan

The original plan sketched one Python builder per country. During implementation the design
improved, per the "no hardcoded data in logic" and "same logic for all countries" requirements:

- **Station data moved out of Python into `countries/*.yaml`** (curated facts, national
  programme rules, aliases, focus blocks). Nothing station-specific remains in code.
- **One generic pipeline** (`build_stations.py`) replaced the per-country builders.
- **Dynamic stream resolution**: curated entries without a pinned `url` take the
  highest-voted working radio-browser stream at build time, so stale links can't accumulate.
- **Frequency-preserving name matching** (`norm_freq`) avoids collisions like
  `norm('Radio Gong 96.3') == norm('Radio Gong 106.9')`.
- **Final dedupe** on (normalized name + city + stream URL) guarantees no duplicate rows.
