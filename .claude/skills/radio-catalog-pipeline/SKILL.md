---
name: radio-catalog-pipeline
description: Use when adding, editing, or fixing radio stations, collections, or countries in the DialShift data/ catalog (YAML → CSV → app-catalog JSON + XLSX pipeline).
---

# Radio catalog pipeline

1. **Edit the YAML source of truth only:**
   - Countries: `data/countries/<name>.yaml` (fields per `data/README.md` schema — station entries carry name, url, genre, city, language, notes, logo). City spellings are unified by the YAML's `city_aliases` only (keys in normalized form: lowercase, no accents or punctuation; the build rejects any other key; see `data/README.md`); the Python holds no alias list.
   - Collections: `data/collections/<name>.yaml` (`code`, `name`, `description`, `stations:` with name/match/genre/url/language/notes/logo).
   - Stream URLs must be direct MP3/AAC/HLS (`http`/`https` only) and must pass `SettingsStore.ValidUrl` semantics and the app's 2,048-character limit — test that the stream actually plays before adding it.
2. **Rebuild:** `data/.venv/bin/python data/build/build_all.py --refresh` (venv at `data/.venv`; recreate per `data/README.md` if missing). Without `--refresh` the cached `data/raw/` is reused and the country CSVs are reproduced byte-for-byte; collections still query radio-browser live.
3. **Verify:**
   - `data/.venv/bin/python data/build/app_catalog.py --self-test` passes (stdlib only, no network).
   - The run printed `app-catalog: working=… url_excluded=… duplicates_removed=… exported=… -> data/output/app-catalog.json` and exited 0. A non-zero exit is a failed app-catalog validation: every problem is printed and the previous JSON and XLSX are left untouched.
   - `canonical/<country>-stations.csv` regenerated; `output/dialshift-radio-catalog.xlsx` updated (check the "Import Ready" tab + Summary counts); `output/app-catalog.json` regenerated (contract: `docs/catalog-contracts.md` §2).
4. **Dedupe safety:** if the build warns about duplicate names/URLs, or the app-catalog validation reports a Working row missing from the export, fix the YAML — never the script.
5. **Never hand-edit** `canonical/*.csv`, `output/*.xlsx`, `output/app-catalog.json`, or `raw/` (gitignored).
6. **Git:** only commit when the operator asks; commit the regenerated CSVs, XLSX and `app-catalog.json` together (a collection CSV that moved with the live lookup goes in only with the JSON built from it); commits go on the feature branch, pushed ONLY to the `private` remote.
