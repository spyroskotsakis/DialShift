---
paths: data/**
---
# Radio catalog data rules

- Station data lives ONLY in YAML: `data/countries/<name>.yaml` and `data/collections/<name>.yaml`. Never put station data in code.
- Language facts live ONLY in YAML too: the app catalog's language table (canonical names, aliases, dropped non-languages) in `data/languages.yaml`; a country's or collection's `language_default` and `language_replace` in its own YAML. No language name, spelling or mapping in Python or C# (D84, `docs/catalog-contracts.md` §2.6). An `unknown language` line from the build means: add that token to `data/languages.yaml`.
- City spellings live ONLY in each country YAML's `city_aliases` (normalized keys, one city per group of spellings, no chains; never a region to its capital or a curated entry's city to another). The app catalog formats radio-browser tag notes (`Tags: a, b`) in the export only; the CSVs keep the raw `tags:` note.
- One generic pipeline: `data/build/*.py` is schema-driven, never station-specific. Adding a country = drop in one YAML file; do not write per-country code. Country labels come from each YAML's `name`.
- Generated artifacts are regenerable — never hand-edit `canonical/*.csv`, `output/*.xlsx`, `output/app-catalog.json`. `raw/` is a disposable cache (gitignored).
- App-import station model: `{Name, URL (direct MP3/AAC/HLS stream), Tag/Description}`. URLs must pass `SettingsStore.ValidUrl` (http/https only, no file:/javascript: schemes) and be at most 2,048 characters; the app catalog exports only Working rows that do (contract: `docs/catalog-contracts.md` §2).
- Rebuild after every YAML change: `data/.venv/bin/python data/build/build_all.py --refresh`. The same run writes and validates `output/app-catalog.json`; a validation failure exits non-zero and leaves the previous JSON in place.
- If the build warns about duplicates, or the app-catalog validation fails on real data, fix the YAML — not the script.
