---
paths: data/**
---
# Radio catalog data rules

- Station data lives ONLY in YAML: `data/countries/<name>.yaml` and `data/collections/<name>.yaml`. Never put station data in code.
- One generic pipeline: `data/build/*.py` is schema-driven, never station-specific. Adding a country = drop in one YAML file; do not write per-country code.
- Generated artifacts are regenerable — never hand-edit `canonical/*.csv`, `output/*.xlsx`. `raw/` is a disposable cache (gitignored).
- App-import station model: `{Name, URL (direct MP3/AAC/HLS stream), Tag/Description}`. URLs must pass `SettingsStore.ValidUrl` (http/https only, no file:/javascript: schemes).
- Rebuild after every YAML change: `data/.venv/bin/python data/build/build_all.py --refresh`
- If the build warns about duplicates, fix the YAML — not the script.
