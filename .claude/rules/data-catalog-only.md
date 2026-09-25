---
paths: data/**
---
# Radio catalog data rules

- Station data lives ONLY in YAML: `data/countries/<name>.yaml` and `data/collections/<name>.yaml`. Never put station data in code.
- One generic pipeline: `data/build/*.py` is schema-driven, never station-specific. Adding a country = drop in one YAML file; do not write per-country code. Country labels come from each YAML's `name`.
- Generated artifacts are regenerable — never hand-edit `canonical/*.csv`, `output/*.xlsx`, `output/app-catalog.json`. `raw/` is a disposable cache (gitignored).
- App-import station model: `{Name, URL (direct MP3/AAC/HLS stream), Tag/Description}`. URLs must pass `SettingsStore.ValidUrl` (http/https only, no file:/javascript: schemes) and be at most 2,048 characters; the app catalog exports only Working rows that do (contract: `docs/catalog-contracts.md` §2).
- Rebuild after every YAML change: `data/.venv/bin/python data/build/build_all.py --refresh`. The same run writes and validates `output/app-catalog.json`; a validation failure exits non-zero and leaves the previous JSON in place.
- If the build warns about duplicates, or the app-catalog validation fails on real data, fix the YAML — not the script.
