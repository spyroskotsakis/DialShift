---
name: radio-catalog-pipeline
description: Use when adding, editing, or fixing radio stations, collections, or countries in the DialShift data/ catalog (YAML → CSV → app-catalog JSON + XLSX pipeline).
---

# Radio catalog pipeline

1. **Edit the YAML source of truth only:**
   - Countries: `data/countries/<name>.yaml` (fields per `data/README.md` schema — station entries carry name, url, genre, city, language, notes, logo). City spellings are unified by the YAML's `city_aliases` only (keys in normalized form: lowercase, no accents or punctuation; the build rejects any other key; see `data/README.md`); the Python holds no alias list. For one spelling per place in the app's City filter, map every spelling of the place (typos, `ue`/`ü`, hyphen or accent variants) to one city, the one most rows already have; if that city's own normalized form is a key, it must map to the same city (the build stops on a chain). Do not alias a spelling that may be another place, a region to its capital, or a curated entry's city to anything else (its pinned URL would be dropped).
   - Collections: `data/collections/<name>.yaml` (`code`, `name`, `description`, optional `language_default`, `stations:` with name/match/genre/url/language/notes/logo).
   - Languages: `data/languages.yaml` is the app catalog's language table and the only place for language facts (D84): `languages` (canonical English names), `aliases` (key in lower case with single spaces → one name or a list; spellings, typos, native names, dialects → their language) and `drop` (non-languages such as `instrumental`). A country's CSV-level `language_default` and `language_replace` (a whole radio-browser value → the CSV language, e.g. `ancient greek: Greek`) live in its own YAML. The Python holds no language list.
   - Frequency band words: `data/frequency-bands.yaml` (`band_words`, e.g. `Shortwave`) lists the only words a station's `freq` may be instead of an FM value with a `.` (64–108) or a kHz integer (≥ 150). Keep `freq` a number; add a word here only for a real band (short label, at most 16 characters).
   - Stream URLs must be direct MP3/AAC/HLS (`http`/`https` only) and must pass `SettingsStore.ValidUrl` semantics and the app's 2,048-character limit — test that the stream actually plays before adding it.
2. **Rebuild:** `data/.venv/bin/python data/build/build_all.py --refresh` (venv at `data/.venv`; recreate per `data/README.md` if missing). Without `--refresh` the cached `data/raw/` is reused and the country CSVs are reproduced byte-for-byte; collections still query radio-browser live.
3. **Verify:**
   - `data/.venv/bin/python data/build/app_catalog.py --self-test` passes (stdlib only, no network).
   - The run printed `app-catalog: working=… url_excluded=… duplicates_removed=… exported=… -> data/output/app-catalog.json` and `app-catalog: languages=… unknown=0`, and exited 0. A non-zero exit is a bad `data/languages.yaml` or `data/frequency-bands.yaml` (stopped before anything is written) or a failed app-catalog validation (for example a `frequency_fm` that is neither FM, kHz nor a band word: fix the YAML's `freq`): every problem is printed and the previous JSON and XLSX are left untouched.
   - `unknown=<n>` above 0 is not a failure, but each `app-catalog: unknown language "…"` line names a token to add to `data/languages.yaml` (as a canonical name, an alias or a drop); rebuild until `unknown=0`.
   - After a `city_aliases` change, the per-country row counts: an alias can merge rows that were one station under two spellings (the final dedupe keeps the richer one), which lowers `working` and `exported` by the same number. Say which station merged.
   - `canonical/<country>-stations.csv` regenerated; `output/dialshift-radio-catalog.xlsx` updated (check the "Import Ready" tab + Summary counts); `output/app-catalog.json` regenerated (contract: `docs/catalog-contracts.md` §2).
4. **Dedupe safety:** if the build warns about duplicate names/URLs, or the app-catalog validation reports a Working row missing from the export, fix the YAML — never the script.
5. **Never hand-edit** `canonical/*.csv`, `output/*.xlsx`, `output/app-catalog.json`, or `raw/` (gitignored).
6. **Git:** only commit when the operator asks; commit the regenerated CSVs, XLSX and `app-catalog.json` together (a collection CSV that moved with the live lookup goes in only with the JSON built from it); commits go on the feature branch, pushed ONLY to the `private` remote.
