---
name: radio-catalog-pipeline
description: Use when adding, editing, or fixing radio stations, collections, or countries in the DialShift data/ catalog (YAML → CSV → XLSX pipeline).
---

# Radio catalog pipeline

1. **Edit the YAML source of truth only:**
   - Countries: `data/countries/<name>.yaml` (fields per `data/README.md` schema — station entries carry name, url, genre, city, language, notes, logo).
   - Collections: `data/collections/<name>.yaml` (`code`, `name`, `description`, `stations:` with name/match/genre/url/language/notes/logo).
   - Stream URLs must be direct MP3/AAC/HLS (`http`/`https` only) and must pass `SettingsStore.ValidUrl` semantics — test that the stream actually plays before adding it.
2. **Rebuild:** `data/.venv/bin/python data/build/build_all.py --refresh` (venv at `data/.venv`; recreate per `data/README.md` if missing).
3. **Verify:** `canonical/<country>-stations.csv` regenerated; `output/dialshift-radio-catalog.xlsx` updated (check the "Import Ready" tab + Summary counts).
4. **Dedupe safety:** if the build warns about duplicate names/URLs, fix the YAML — never the script.
5. **Never hand-edit** `canonical/*.csv`, `output/*.xlsx`, or `raw/` (gitignored).
6. **Git:** only commit when the operator asks; commits go on the feature branch, pushed ONLY to the `private` remote.
