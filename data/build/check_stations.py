#!/usr/bin/env python3
"""DialShift consolidated catalog health check.

Validates the ONE consolidated file the app ships — data/output/app-catalog.json
(all countries + collections in a single stations list) — and verifies, with real
HTTP probes, that every station's stream URL serves audio and every logo URL serves
an image. Nothing is checked per country: one list, one pass.

Usage:
  data/.venv/bin/python data/build/check_stations.py [catalog.json] [options]

Options:
  --workers N        concurrent probes (default 25)
  --timeout S        per-probe timeout in seconds (default 8)
  --limit N          check only the first N stations (for testing)
  --no-streams       skip stream probing (data validation + logos only)
  --no-logos         skip logo probing
  --audit-rb         gap audit: radio-browser stations (working, votes >= 2) missing
                     from the consolidated catalog, using the cached raw/*.json
  --prune            after probing, rewrite <catalog> WITHOUT the stations whose
                     stream verdict is FAIL (definitely not serving audio: HTTP
                     errors or a non-audio answer). ERROR (timeout/network) and
                     RATE_LIMITED (HTTP 429) rows are KEPT — a transient failure
                     must not drop a station.
                     The file's shape (schema_version, generated_utc, stations)
                     is unchanged, so the app reads it as a normal catalog.
                     Data-validation errors block pruning (exit 1).
  --strict           exit 1 when any stream FAIL/ERROR or logo FAIL (default: streams
                     and logos are reported, not fatal). RATE_LIMITED is retryable,
                     never a FAIL.
  --output PATH      write the full report as JSON
  --broken-csv PATH  write the broken streams + logos list as CSV

Exit codes: 0 all checks passed (with --strict: no broken stream/logo),
1 data validation errors (or --strict findings), 2 usage error.
"""
import argparse
import concurrent.futures
import csv
import json
import os
import re
import ssl
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

from common import norm, url_norm

DATA_DIR = Path(__file__).resolve().parent.parent
DEFAULT_CATALOG = DATA_DIR / 'output' / 'app-catalog.json'
SCHEMA_VERSION = 1
FM_RANGE = (64, 108)
KHZ_MIN, KHZ_MAX = 150, 30_000
BAND_WORDS_FILE = DATA_DIR / 'frequency-bands.yaml'
MAX_URL_LEN = 2048

_CTX = ssl._create_unverified_context()  # public data; some hosts have broken chains
_UA = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36'
_STREAM_TYPES = ('audio/', 'application/octet-stream', 'application/vnd.apple.mpegurl',
                 'audio/x-mpegurl', 'audio/mpegurl', 'video/mp2t')
# Verdicts that never mean "broken": OK, plus RATE_LIMITED (HTTP 429 on any URL, or 403 on a
# logo probe) — a rate-limit / anti-bot answer is retryable, not proof the resource is dead.
NOT_BROKEN_STREAM = ('OK', 'RATE_LIMITED')
NOT_BROKEN_LOGO = ('OK', 'SKIP', 'RATE_LIMITED')
REPO_ROOT = DATA_DIR.parent                # report paths relative to the repo, not a build machine path


def repo_relative(path):
    """path as a repo-relative string, so a report never embeds a build machine's absolute path."""
    try:
        return str(Path(path).resolve().relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


def band_words():
    text = BAND_WORDS_FILE.read_text(encoding='utf-8')
    return [w.strip() for w in re.findall(r'^[ \t]*- (.+)$', text, re.M)]


def valid_frequency(value, words):
    """Rule 9 of the catalog contracts: '', FM with '.', kHz int, or a band word."""
    if value == '' or value in words:
        return True
    if '.' in value:
        try:
            return FM_RANGE[0] <= float(value) <= FM_RANGE[1]
        except ValueError:
            return False
    if value.isdigit():
        return KHZ_MIN <= int(value) <= KHZ_MAX
    return False


def validate_station(s, i, words, seen):
    """Contract checks for one station; returns (errors, warnings)."""
    problems, warnings = [], []
    name = s.get('name')
    if not isinstance(name, str) or not name.strip():
        problems.append('name is empty')
    elif name != name.strip():
        problems.append(f'name has leading/trailing spaces: {name!r}')
    url = s.get('stream_url')
    if not isinstance(url, str) or not url.strip():
        problems.append('stream_url is empty')
    elif not url.lower().startswith(('http://', 'https://')):
        problems.append(f'stream_url scheme is not http(s): {url!r}')
    elif len(url) > MAX_URL_LEN:
        problems.append(f'stream_url is {len(url)} characters (max {MAX_URL_LEN})')
    freq = s.get('frequency_fm')
    if not valid_frequency(str(freq or ''), words):
        problems.append(f'frequency_fm {freq!r} is not an FM value, a kHz integer or a band word')
    if not str(s.get('language') or '').strip():
        # The pipeline drops non-languages such as "Various Languages"/"Multilingual" by design
        # (D84, languages.yaml drop list), so an empty language is correct data, not an error.
        warnings.append('language is empty (a dropped non-language, e.g. "Various Languages")')
    tz = str(s.get('timezone') or '')
    if tz:
        try:
            ZoneInfo(tz)
        except Exception:
            problems.append(f'timezone {tz!r} is not an IANA timezone')
    elif s.get('country') != 'Internet':
        warnings.append('timezone is empty (no country default, no city override)')
    key = (s.get('country'), norm(name).replace(' ', ''), url_norm(url))
    if key in seen:
        prev_i, prev_city = seen[key]
        c = str(s.get('city') or '').strip().lower()
        pc = str(prev_city or '').strip().lower()
        if c and c == pc:
            problems.append(f'duplicate of station {prev_i} (same country + name + stream + city)')
        elif not c or not pc:
            problems.append(f'duplicate of station {prev_i} (same country + name + stream; one row has no city)')
        else:
            # Same name + same stream but DIFFERENT cities: never merged automatically —
            # local stations that share one stream (relay networks) are distinct stations.
            warnings.append(f'same name and stream as station {prev_i} but a different city '
                            f'({prev_city!r} vs {s.get("city")!r}) — possibly a relay or a distinct '
                            f'local station; kept unless verified duplicate')
    else:
        seen[key] = (i, s.get('city'))
    return problems, warnings


def probe(url, timeout, logo=False):
    """One GET with a Range header; returns (kind, detail).

    A 429 (any URL) or a 403 on a logo probe is a rate-limit / anti-bot response, not proof
    the resource is dead: it is reported as RATE_LIMITED, a retryable "unknown" verdict that
    is never counted as a broken stream or logo."""
    if not url:
        return 'FAIL', 'no url'
    req = urllib.request.Request(url, headers={'User-Agent': _UA, 'Icy-MetaData': '1',
                                               'Range': 'bytes=0-4095'})
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=_CTX) as r:
            ct = (r.headers.get('Content-Type') or '').lower()
            r.read(4096)
            return 'OK', ct
    except urllib.error.HTTPError as e:
        if e.code == 429 or (logo and e.code == 403):
            return 'RATE_LIMITED', f'http {e.code}'
        return 'FAIL', f'http {e.code}'
    except Exception as e:
        return 'ERROR', type(e).__name__


def check_stream(url, timeout):
    kind, detail = probe(url, timeout)
    if kind == 'OK':
        if detail.startswith('audio/') or detail.startswith('application/ogg') \
                or detail in ('application/octet-stream', 'video/mp2t'):
            return 'OK', detail
        if 'mpegurl' in detail:
            return 'OK', detail  # HLS playlist — the app plays it
        if detail.startswith('image/'):
            return 'FAIL', f'serves an image, not audio ({detail})'
        return 'FAIL', f'not audio: {detail or "no content-type"}'
    return kind, detail


def check_logo(url, timeout):
    if not url:
        return 'SKIP', 'no logo'
    kind, detail = probe(url, timeout, logo=True)
    if kind != 'OK':
        return kind, detail
    if detail.startswith('image/'):
        return 'OK', detail
    return 'NONIMAGE', f'not an image: {detail or "no content-type"}'


def verdict_counts(results):
    counts = {}
    for kind, _ in results.values():
        counts[kind] = counts.get(kind, 0) + 1
    return counts


def audit_rb(catalog):
    """Gap audit: cached radio-browser rows (working, votes >= 2) missing from the catalog."""
    names = {norm(s['name']).replace(' ', '') for s in catalog}
    urls = {url_norm(s['stream_url']) for s in catalog}
    report = {}
    for code in ('DE', 'FR', 'GR'):
        raw = DATA_DIR / 'raw' / code / 'radio-browser.json'
        if not raw.exists():
            continue
        rows = json.loads(raw.read_text(encoding='utf-8'))
        working = [s for s in rows if s.get('lastcheckok') == 1]
        missing = []
        for s in working:
            n = norm(s.get('name', '')).replace(' ', '')
            if not n or len(n) < 3 or n in names:
                continue
            if any(min(len(n), len(wn)) >= 5 and (n in wn or wn in n) for wn in names):
                continue
            url = (s.get('url_resolved') or s.get('url') or '').split('?ver=')[0]
            if url_norm(url) in urls:
                continue
            missing.append({'name': s.get('name'), 'state': s.get('state'),
                            'votes': s.get('votes') or 0, 'url': url})
        missing.sort(key=lambda s: -s['votes'])
        report[code] = missing
    return report


def main():
    ap = argparse.ArgumentParser(description='DialShift consolidated catalog health check')
    ap.add_argument('catalog', nargs='?', default=str(DEFAULT_CATALOG))
    ap.add_argument('--workers', type=int, default=25)
    ap.add_argument('--timeout', type=float, default=8.0)
    ap.add_argument('--limit', type=int, default=0, help='check only the first N stations')
    ap.add_argument('--no-streams', action='store_true')
    ap.add_argument('--no-logos', action='store_true')
    ap.add_argument('--audit-rb', action='store_true')
    ap.add_argument('--prune', action='store_true')
    ap.add_argument('--strict', action='store_true')
    ap.add_argument('--output', type=Path)
    ap.add_argument('--broken-csv', type=Path)
    args = ap.parse_args()

    path = Path(args.catalog)
    if not path.exists():
        print(f'catalog not found: {path}', file=sys.stderr)
        return 2
    doc = json.loads(path.read_text(encoding='utf-8'))
    stations = doc.get('stations')
    if doc.get('schema_version') != SCHEMA_VERSION:
        print(f"schema_version is {doc.get('schema_version')!r}, not {SCHEMA_VERSION}", file=sys.stderr)
        return 2
    if not isinstance(stations, list) or not stations:
        print('stations is empty or missing', file=sys.stderr)
        return 2
    if args.limit:
        stations = stations[:args.limit]
    print(f'Loaded {len(stations)} stations from {path.name} '
          f'(generated {doc.get("generated_utc")})')

    if args.prune and args.no_streams:
        print('--prune needs the stream probes; do not combine it with --no-streams',
              file=sys.stderr)
        return 2
    if args.prune and args.limit:
        print('--prune refuses --limit: a limited run would truncate the catalog '
              'to the first N stations.', file=sys.stderr)
        return 2

    # --- 1. data validation -------------------------------------------------
    words = band_words()
    seen, errors, warnings = {}, [], []
    for i, s in enumerate(stations):
        problems, warns = validate_station(s, i, words, seen)
        for p in problems:
            errors.append({'index': i, 'name': s.get('name'), 'country': s.get('country'),
                           'problem': p})
        for w in warns:
            warnings.append({'index': i, 'name': s.get('name'), 'country': s.get('country'),
                             'problem': w})
    print(f'Data validation: {"PASS" if not errors else "FAIL"} '
          f'({len(errors)} error(s), {len(warnings)} warning(s) on {len(stations)} stations)')
    for e in errors[:20]:
        print(f'  [data] {e["name"]!r} ({e["country"]}): {e["problem"]}')
    if len(errors) > 20:
        print(f'  … and {len(errors) - 20} more errors')
    for w in warnings[:10]:
        print(f'  [warn] {w["name"]!r} ({w["country"]}): {w["problem"]}')
    if len(warnings) > 10:
        print(f'  … and {len(warnings) - 10} more warnings')

    # --- 2. stream + logo probes (concurrent) ------------------------------
    stream_results, logo_results = {}, {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        if not args.no_streams:
            futures = {pool.submit(check_stream, s.get('stream_url'), args.timeout): i
                       for i, s in enumerate(stations)}
            for f in concurrent.futures.as_completed(futures):
                stream_results[futures[f]] = f.result()
        if not args.no_logos:
            futures = {pool.submit(check_logo, s.get('logo'), args.timeout): i
                       for i, s in enumerate(stations)}
            for f in concurrent.futures.as_completed(futures):
                logo_results[futures[f]] = f.result()

    for label, results in (('streams', stream_results), ('logos', logo_results)):
        if not results:
            continue
        counts = verdict_counts(results)
        print(f'{label.capitalize()}: {counts}')
        rate = counts.get('RATE_LIMITED', 0)
        if rate:
            print(f'  rate-limited / retryable (HTTP 429; 403 on logos): {rate} '
                  f'— not counted as broken')

    broken_streams = [(stations[i], k, d) for i, (k, d) in stream_results.items()
                      if k not in NOT_BROKEN_STREAM]
    broken_logos = [(stations[i], k, d) for i, (k, d) in logo_results.items()
                    if k not in NOT_BROKEN_LOGO]
    rate_streams = [(stations[i], k, d) for i, (k, d) in stream_results.items() if k == 'RATE_LIMITED']
    rate_logos = [(stations[i], k, d) for i, (k, d) in logo_results.items() if k == 'RATE_LIMITED']
    if rate_streams or rate_logos:
        print(f'\nRate-limited / retryable (not broken): {len(rate_streams)} stream(s), '
              f'{len(rate_logos)} logo(s) — re-run to retry')
    if broken_streams:
        print(f'\nBroken streams ({len(broken_streams)}):')
        for s, k, d in broken_streams[:30]:
            print(f'  [{k}] {s["name"]!r} ({s["country"]}) {d} — {s["stream_url"][:70]}')
        if len(broken_streams) > 30:
            print(f'  … and {len(broken_streams) - 30} more')
    if broken_logos:
        print(f'\nBroken logos ({len(broken_logos)}):')
        for s, k, d in broken_logos[:30]:
            print(f'  [{k}] {s["name"]!r} ({s["country"]}) {d} — {s["logo"][:70]}')
        if len(broken_logos) > 30:
            print(f'  … and {len(broken_logos) - 30} more')

    # --- 3. prune (rewrite the catalog without FAIL-verdict streams) ---------
    pruned = 0
    if args.prune:
        if errors:
            print('Pruning skipped: the catalog has data-validation errors.', file=sys.stderr)
        elif not stream_results:
            print('Pruning skipped: no stream verdicts.', file=sys.stderr)
        else:
            # FAIL only: an ERROR (timeout/network) or RATE_LIMITED (429/logo 403) is not proof the
            # stream is dead, and a station must not be dropped because one probe was unlucky.
            keep = [s for i, s in enumerate(stations)
                    if stream_results.get(i, ('OK', ''))[0] in ('OK', 'ERROR', 'RATE_LIMITED')]
            pruned = len(stations) - len(keep)
            out = {'schema_version': doc['schema_version'], 'generated_utc': doc['generated_utc'],
                   'stations': keep}
            tmp = path.with_suffix('.json.pruning')
            tmp.write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding='utf-8')
            os.replace(tmp, path)
            print(f'Pruned {pruned} broken stream(s) from {path.name}: '
                  f'{len(stations)} -> {len(keep)} stations (shape unchanged).')

    # --- 4. radio-browser gap audit ----------------------------------------
    audit = {}
    if args.audit_rb:
        audit = audit_rb(stations)
        for code, missing in audit.items():
            shown = [m for m in missing if m['votes'] >= 2]
            print(f'\nrb gap audit {code}: {len(missing)} candidate miss(es), '
                  f'{len(shown)} with votes >= 2')
            for m in shown[:20]:
                print(f'  votes={m["votes"]:>4} {m["name"][:40]:<42} {m["state"]} — {m["url"][:60]}')

    # --- 5. report -----------------------------------------------------------
    report = {
        'checked_at': datetime.now(timezone.utc).isoformat(),
        'catalog': repo_relative(path), 'generated_utc': doc.get('generated_utc'),
        'stations_checked': len(stations),
        'validation_errors': errors,
        'validation_warnings': warnings,
        'pruned': {'removed': pruned, 'kept': len(stations) - pruned} if args.prune else None,
        'streams': {k: v for k, v in
                    [('verdicts', verdict_counts(stream_results) if stream_results else {}),
                     ('broken', [{'name': s['name'], 'country': s['country'], 'verdict': k,
                                  'detail': d, 'url': s['stream_url']}
                                 for s, k, d in broken_streams])]},
        'logos': {'verdicts': verdict_counts(logo_results) if logo_results else {},
                  'broken': [{'name': s['name'], 'country': s['country'], 'verdict': k,
                              'detail': d, 'url': s['logo']} for s, k, d in broken_logos]},
        # RATE_LIMITED (429 anywhere, 403 on a logo): retryable, excluded from the broken lists above
        'rate_limited': {'streams': [{'name': s['name'], 'country': s['country'], 'detail': d,
                                      'url': s['stream_url']} for s, k, d in rate_streams],
                         'logos': [{'name': s['name'], 'country': s['country'], 'detail': d,
                                    'url': s['logo']} for s, k, d in rate_logos]},
        'rb_gap_audit': audit,
    }
    if args.output:
        args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
        print(f'\nReport written to {args.output}')
    if args.broken_csv:
        with open(args.broken_csv, 'w', newline='', encoding='utf-8') as f:
            w = csv.writer(f)
            w.writerow(['kind', 'name', 'country', 'verdict', 'detail', 'url'])
            for s, k, d in broken_streams:
                w.writerow(['stream', s['name'], s['country'], k, d, s['stream_url']])
            for s, k, d in broken_logos:
                w.writerow(['logo', s['name'], s['country'], k, d, s['logo']])
        print(f'Broken list written to {args.broken_csv}')

    if errors:
        return 1
    if args.strict and (broken_streams or broken_logos):
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
