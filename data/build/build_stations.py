#!/usr/bin/env python3
"""The single, generic station-catalog pipeline for ALL countries.

Every country is described by a YAML file in ../countries/<name>.yaml —
station facts (curated identities, national programmes, city aliases) live
ONLY in that data file, never in code. This pipeline does not change when a
country is added: just drop a new YAML and build_all.py picks it up.

Pipeline (same for every country):
  1. (optional) parse the Wikipedia FM list  ->  FM landscape rows
  2. fetch + dedupe radio-browser dump       ->  stream URL pool
  3. national programmes consolidation (e.g. ERT relays -> one row each)
  4. curated identities overlaid (pinned URLs, types, leanings, genres)
  5. unmatched radio-browser extras (internet-only / unlisted)
  6. sort + return canonical rows
"""
import json
import re
import ssl
import sys
import urllib.parse
import urllib.request
from pathlib import Path

import yaml

from common import (DATA_DIR, classify, clean_name, dedupe_rb, fetch_radio_browser,
                    fetch_text, norm, norm_city, norm_freq, row_score, url_norm)

COUNTRIES_DIR = DATA_DIR / 'countries'
COLLECTIONS_DIR = DATA_DIR / 'collections'
RAW_DIR = DATA_DIR / 'raw'
SSL_CTX = ssl._create_unverified_context()

# ---------------------------------------------------------------- country config

def load_country(path):
    with open(path, encoding='utf-8') as f:
        cfg = yaml.safe_load(f)
    # focus areas: 'focus_areas:' list (city- or region-based), or the older
    # 'focus_cities:' / single 'focus:' block (kept working for compatibility)
    cfg['focus_areas'] = cfg.get('focus_areas') or cfg.get('focus_cities') \
        or ([cfg['focus']] if cfg.get('focus') else [])
    cfg['focus_area_labels'] = {a['label'] for a in cfg['focus_areas']}
    # two lookup forms per match key: frequency-preserving first (exact
    # identities like '104.6 rtl'), then normalized (broad matching)
    cfg['curated_by_match_freq'] = {}
    cfg['curated_by_match'] = {}
    for entry in cfg.get('curated', []):
        for m in entry.get('match', []):
            cfg['curated_by_match_freq'].setdefault(norm_freq(m), entry)
            cfg['curated_by_match'].setdefault(norm(m), entry)
    return cfg

def all_countries():
    return [load_country(p) for p in sorted(COUNTRIES_DIR.glob('*.yaml'))]

# ---------------------------------------------------------------- wikipedia FM list

def parse_wiki_tables(text):
    """Parse the standard 'List of radio stations in X' page structure:
    tables with header 'Frequency | Name | On air since | Description'
    and 'Name | On air since | Description | Coverage | Location'."""
    lines = text.splitlines()

    def strip_md(c):
        c = re.sub(r'\[([^\]]+)\]\([^)]*\)', r'\1', c)
        c = re.sub(r'<.*?>', '', c).replace('&nbsp;', ' ')
        return c.replace('[', '').replace(']', '').strip()

    region, pref = '', ''
    rows = []
    i, n = 0, len(lines)
    while i < n:
        l = lines[i]
        if l.startswith('## '):
            region, pref, i = l[3:].strip(), '', i + 1
            continue
        if l.startswith('### '):
            pref, i = l[4:].strip(), i + 1
            continue
        if l.startswith('|'):
            tbl = []
            while i < n and lines[i].startswith('|'):
                tbl.append(lines[i]); i += 1
            if len(tbl) >= 2:
                header = [strip_md(c) for c in tbl[0].split('|')[1:-1]]
                for row in tbl[2:]:
                    cells = [strip_md(c) for c in row.split('|')[1:-1]]
                    if not any(cells):
                        continue
                    if header and header[0] == 'Frequency' and len(cells) >= 3:
                        freq = re.sub(r'[^\d.]', '', cells[0] or '')
                        rows.append(dict(region=region, prefecture=pref, freq=freq,
                                         name=cells[1], since=cells[2] or '',
                                         desc=cells[3] if len(cells) > 3 else ''))
                    elif header and header[0] == 'Name' and 'Coverage' in header and len(cells) >= 3:
                        loc = cells[4] if len(cells) > 4 else (cells[3] if len(cells) > 3 else '')
                        rows.append(dict(region='Internet', prefecture='Internet-only', freq='',
                                         name=cells[0], since=cells[1] or '',
                                         desc=cells[2] or '', city=loc))
        else:
            i += 1
    return rows

def wiki_rows_for(cfg):
    w = cfg.get('wiki') or {}
    if not w.get('url'):
        return []
    cache = RAW_DIR / cfg['code'] / w.get('cache', 'wiki.md')
    if not cache.exists():
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_text(fetch_text(w['url']), encoding='utf-8')
    return parse_wiki_tables(cache.read_text(encoding='utf-8'))

def wiki_city(cfg, w):
    if w['region'] == 'Internet':
        return w.get('city') or cfg.get('name')
    p = (w.get('prefecture') or '').strip()
    prefix = cfg.get('prefecture_prefix', 'Prefecture of')
    if p.startswith(prefix):
        return p[len(prefix):].strip()
    if p:
        return p
    r = w['region'].replace('Radio stations in', '').strip()
    return cfg.get('region_city_map', {}).get(w['region'], cfg.get('region_city_map', {}).get(r, r))

def wiki_region(cfg, w):
    if w['region'] == 'Internet':
        return 'Internet-only'
    r = w['region'].replace('Radio stations in', '').strip()
    return r or cfg.get('name')

# ---------------------------------------------------------------- curated / national

def match_national(cfg, name, desc):
    """Return the national-programme entry if the wiki row is a relay of it."""
    nl = name.lower()
    dl = (desc or '').lower()
    for p in cfg.get('national_programmes', []):
        guard = p.get('desc_must_contain')
        if guard and guard not in dl:
            continue
        if any(m in nl for m in p.get('match_names', [])):
            return p
    return None

# ---------------------------------------------------------------- the one pipeline

def build_country(cfg, force_refresh=False):
    code = cfg['code']
    rb = dedupe_rb(fetch_radio_browser(code, force=force_refresh))

    rb_by_norm = {}
    for s in rb:
        n = norm(s['name'])
        if len(n) >= 3 and (n not in rb_by_norm or (s.get('votes') or 0) > (rb_by_norm[n].get('votes') or 0)):
            rb_by_norm[n] = s

    def find_stream(wname_n):
        if wname_n in rb_by_norm:
            return rb_by_norm[wname_n]
        if len(wname_n) >= 8:
            for n2, s in rb_by_norm.items():
                if wname_n in n2 or n2 in wname_n:
                    return s
        return None

    rows, seen = [], set()
    def add(row):
        key = (row['name'].lower(), row['stream_url'])
        if key in seen:
            return
        seen.add(key)
        rows.append(row)

    lang_default = cfg.get('language_default', '')

    def make_row(wname, entry, city, region, freq, desc, stream, internet_only, source, force_pin=False):
        t, g = classify(desc, (stream or {}).get('tags', ''), code)
        entry = entry or {}
        ccity = norm_city(entry.get('city', city), code)
        if entry.get('no_auto_stream'):            # identity only, no public stream
            stream = None
        pinned = entry.get('url')
        # A pinned URL only applies to the station's own city, to genuine
        # rebroadcasts, or when the entry says it relays everywhere (relay_pin).
        if pinned and not force_pin and entry.get('city') and ccity != entry['city'] \
                and 'rebroadcast' not in (desc or '').lower():
            pinned = None
        url = pinned or (stream or {}).get('url_resolved', '')
        ok = 1 if pinned else (stream or {}).get('lastcheckok', 0)
        focus_areas = cfg.get('focus_areas', [])
        row = dict(country=code,
                   name=entry.get('name', wname), name_local=entry.get('name_local', ''),
                   city=ccity, region=region,
                   frequency_fm=str(entry.get('freq', freq) or ''),
                   type=entry.get('type', t), genre=entry.get('genre', g),
                   language=entry.get('language', lang_default),
                   political_leaning=entry.get('leaning', 'None'),
                   internet_only=internet_only,
                   stream_url=url,
                   codec='' if pinned else (stream or {}).get('codec', ''),
                   bitrate='' if pinned else (stream or {}).get('bitrate', ''),
                   stream_status='Working' if ok else ('Down' if stream else 'No stream found'),
                   votes=0 if pinned else (stream or {}).get('votes', 0),
                   logo=entry.get('logo') or (stream or {}).get('favicon', ''),
                   notes=entry.get('notes', desc), source=source)
        # focus membership: explicit entry flag (true = first focus area, or a
        # label/city/region name), or a terrestrial row in a focus city/region
        fc = entry.get('focus')
        if fc is True and focus_areas:
            row['focus_area'] = focus_areas[0]['label']
        elif isinstance(fc, str):
            for a in focus_areas:
                if fc in (a.get('label'), a.get('city'), a.get('region')):
                    row['focus_area'] = a['label']
                    break
        elif internet_only == 'No':
            for a in focus_areas:
                if a.get('city') and ccity == a['city']:
                    row['focus_area'] = a['label']
                    break
                if a.get('region') and region == a['region']:
                    row['focus_area'] = a['label']
                    break
        return row

    def curated_for(name_n):
        """Look up a curated entry: frequency-preserving match first, then normalized."""
        return (cfg['curated_by_match_freq'].get(norm_freq(name_n))
                or cfg['curated_by_match'].get(norm(name_n)))

    wiki_rows = wiki_rows_for(cfg)
    national_seen = set()
    covered_entries = set()     # curated entries already applied via the wiki loop

    for w in wiki_rows:
        wname = clean_name(w['name'])
        n = norm(wname)
        if len(n) < 3:
            continue
        desc = w['desc'] or ''
        stream = find_stream(n)
        city = wiki_city(cfg, w)
        region = wiki_region(cfg, w)

        # national programmes: consolidate relays into one row each
        nat = match_national(cfg, wname, desc)
        if nat:
            if nat['id'] not in national_seen:
                national_seen.add(nat['id'])
                add(dict(country=code, name=nat.get('name', wname), name_local=nat.get('name_local', ''),
                         city=norm_city(nat.get('city', city), code), region='National',
                         frequency_fm=str(nat.get('freq', w['freq']) or ''),
                         type=nat.get('type', 'Public'), genre=nat.get('genre', ''),
                         language=nat.get('language', lang_default),
                         political_leaning=nat.get('leaning', 'State'), internet_only='No',
                         stream_url=nat.get('url') or '',
                         codec='', bitrate='',
                         stream_status='Working' if nat.get('url') else 'No stream found',
                         votes=0, notes=nat.get('notes', desc), source='curated'))
            continue

        # curated identity for a known station
        entry = curated_for(n)
        if entry:
            covered_entries.add(id(entry))
            relay_pin = bool(entry.get('relay_pin'))
            add(make_row(wname, entry, city, region, w['freq'], desc, stream,
                         'Yes' if w['region'] == 'Internet' else 'No',
                         'curated' if entry.get('url') else ('curated+radio-browser' if stream else 'curated'),
                         force_pin=relay_pin))
            continue

        # generic station from the wiki list
        add(make_row(wname, None, city, region, w['freq'], desc, stream,
                     'Yes' if w['region'] == 'Internet' else 'No',
                     'wiki+radio-browser' if stream else 'wiki-only'))

    # radio-browser extras (internet-only or unlisted)
    wiki_norms = [norm(clean_name(w['name'])) for w in wiki_rows]
    wiki_norms = [x for x in wiki_norms if len(x) >= 3]
    pinned_urls = {p.get('url') for p in cfg.get('national_programmes', []) if p.get('url')}
    pinned_urls |= {e.get('url') for e in cfg.get('curated', []) if e.get('url')}
    pinned_urls = {url_norm(u) for u in pinned_urls}
    # group extras by (normalized name, state) so same-name stations in
    # different cities survive, while curated entries can pick the BEST stream
    by_norm = {}
    for s in rb:
        n = norm(s['name'])
        if len(n) < 3 or s.get('lastcheckok') != 1:
            continue
        by_norm.setdefault((n, s.get('state') or ''), []).append(s)
    extras = 0

    # Pass 1: curated identities for stations missing from the wiki list.
    # Each curated entry yields ONE row with the highest-voted matching stream.
    for entry in cfg.get('curated', []):
        if not entry.get('city'):
            continue
        if id(entry) in covered_entries:        # already covered by a wiki row
            continue
        cand_groups = []
        for (n, _state), group in by_norm.items():
            if curated_for(n) is entry:
                cand_groups.append(group)
        if cand_groups:
            s = max((max(g, key=lambda x: (x.get('votes') or 0)) for g in cand_groups),
                    key=lambda x: (x.get('votes') or 0))
        elif entry.get('url') or entry.get('no_auto_stream'):
            s = None                                    # pinned/identity-only row
        else:
            continue
        add(make_row(s['name'] if s else entry.get('name') or entry.get('match', [''])[0].title(),
                     entry, entry['city'], entry.get('region', cfg.get('name')), '',
                     f"curated: {entry.get('notes', '')}", s, 'No',
                     'curated+radio-browser' if s else 'curated',
                     force_pin=bool(entry.get('relay_pin'))))
        extras += 1
        for (n, _state) in list(by_norm.keys()):
            if curated_for(n) is entry:
                del by_norm[(n, _state)]

    # Pass 2: everything else
    for (n, _state), group in by_norm.items():
        s = max(group, key=lambda x: (x.get('votes') or 0))
        url = (s.get('url_resolved') or '').split('?ver=')[0]
        if (s.get('votes') or 0) < 2 and not s.get('state'):
            continue
        if url_norm(url) in pinned_urls:             # duplicates a pinned stream
            continue
        if any(n == wn or (min(len(n), len(wn)) >= 5 and (n in wn or wn in n)) for wn in wiki_norms):
            continue
        tags = s.get('tags', '') or ''
        t, g = classify('', tags, code)
        lang = (s.get('language') or '').title() or lang_default
        if lang == 'Ancient Greek':
            lang = lang_default or 'Greek'
        add(dict(country=code, name=s['name'], name_local='',
                 city=norm_city(s.get('state') or '', code) or '—',
                 region=s.get('state') or '(unlisted)',
                 frequency_fm='', type=t, genre=g, language=lang,
                 political_leaning='None', internet_only='Unknown',
                 stream_url=url,
                 codec=s.get('codec') or '', bitrate=s.get('bitrate') or '',
                 stream_status='Working', votes=s.get('votes') or 0,
                 logo=s.get('favicon') or '',
                 notes=f"tags: {tags}", source='radio-browser'))
        extras += 1

    # final safety dedupe: same normalized name + city + stream = same station
    # (keeps the richest row: with frequency, curated, then most votes)
    # 'stokokkino' vs 'Sto Kokkino' merge: spaces are ignored in the name key
    deduped, best = {}, {}
    for r in rows:
        key = (norm(r['name']).replace(' ', ''), norm_city(r['city'], code), url_norm(r['stream_url']))
        prev = best.get(key)
        if prev is None or row_score(r) > row_score(prev):
            best[key] = r
    rows = list(best.values())
    rows.sort(key=lambda r: (r['city'] == '—', r['city'], r['name'].lower()))
    return rows, {'extras': extras}


# ---------------------------------------------------------------- collections
# Genre collections are the SAME schema as countries but with country='Internet':
# hand-curated lists of internet-radio stations (e.g. the Ambient & Chill family
# around DialShift's SomaFM defaults). Streams resolve dynamically from
# radio-browser by name unless the YAML pins a verified URL.

def all_collections():
    if not COLLECTIONS_DIR.is_dir():
        return []
    return [load_country(p) for p in sorted(COLLECTIONS_DIR.glob('*.yaml'))]


def rb_search(name, limit=8):
    """Search radio-browser by name (any country), best votes first."""
    q = urllib.parse.quote(name)
    req = urllib.request.Request(
        f'https://de1.api.radio-browser.info/json/stations/search'
        f'?name={q}&hidebroken=true&order=votes&reverse=true&limit={limit}',
        headers={'User-Agent': 'DialShift/1.0'})
    with urllib.request.urlopen(req, timeout=25, context=SSL_CTX) as r:
        return json.load(r)


def build_collection(cfg):
    rows = []
    cache = {}
    for e in cfg.get('stations', []):
        pinned = e.get('url')
        s = None
        # search radio-browser even for pinned entries: favicon comes from there
        # (unless the YAML pins a logo) and unpinned entries need the stream
        keys = e.get('match') or [e.get('name', '')]
        for key in keys:
            if key not in cache:
                try:
                    cache[key] = rb_search(key)
                except Exception:
                    cache[key] = []
            for h in cache[key]:
                if any(norm(k) in norm(h['name']) for k in keys):
                    s = h
                    break
            if s:
                break
        url = pinned or (s or {}).get('url_resolved', '')
        rows.append(dict(country='Internet', name=e.get('name') or (e.get('match') or [''])[0],
                         name_local='', city='—', region='Internet radio',
                         frequency_fm='', type=e.get('type', 'Music'),
                         genre=e.get('genre', ''), language=e.get('language', 'Instrumental'),
                         political_leaning='None', internet_only='Yes',
                         stream_url=url, codec=(s or {}).get('codec', ''),
                         bitrate=(s or {}).get('bitrate', 0),
                         stream_status='Working' if url else 'No stream found',
                         votes=(s or {}).get('votes') or 0,
                         logo=e.get('logo') or (s or {}).get('favicon', ''),
                         notes=e.get('notes', ''), source='curated', focus_area=''))
    rows.sort(key=lambda r: r['name'].lower())
    return rows


if __name__ == '__main__':
    for cfg in all_countries():
        rows, stats = build_country(cfg, force_refresh='--refresh' in sys.argv)
        print(f"{cfg['code']} {cfg['name']}: {len(rows)} rows, {stats['extras']} extras")
    for cfg in all_collections():
        rows = build_collection(cfg)
        print(f"[collection] {cfg['name']}: {len(rows)} rows, "
              f"{sum(1 for r in rows if r['stream_status'] == 'Working')} working")
