"""Shared helpers for the DialShift radio data builders.

Everything here is stdlib-only; the XLSX writer lives in build_all.py (needs openpyxl).
"""
import json
import re
import ssl
import time
import unicodedata
import urllib.parse
import urllib.request
from pathlib import Path

DATA_DIR = Path(__file__).resolve().parent.parent
RAW_DIR = DATA_DIR / "raw"

_CTX = ssl._create_unverified_context()  # public data; some hosts have broken chains

# ---------------------------------------------------------------- Greek transliteration
_GREEK_MAP = str.maketrans({
    'ά': 'α', 'έ': 'ε', 'ή': 'η', 'ί': 'ι', 'ό': 'ο', 'ύ': 'υ', 'ώ': 'ω', 'ϊ': 'ι', 'ϋ': 'υ', 'ΐ': 'ι', 'ΰ': 'υ',
    'Ά': 'α', 'Έ': 'ε', 'Ή': 'η', 'Ί': 'ι', 'Ό': 'ο', 'Ύ': 'υ', 'Ώ': 'ω',
    'α': 'a', 'β': 'v', 'γ': 'g', 'δ': 'd', 'ε': 'e', 'ζ': 'z', 'η': 'i', 'θ': 'th', 'ι': 'i', 'κ': 'k', 'λ': 'l',
    'μ': 'm', 'ν': 'n', 'ξ': 'x', 'ο': 'o', 'π': 'p', 'ρ': 'r', 'σ': 's', 'ς': 's', 'τ': 't', 'υ': 'y', 'φ': 'f',
    'χ': 'ch', 'ψ': 'ps', 'ω': 'o', 'Α': 'a', 'Β': 'v', 'Γ': 'g', 'Δ': 'd', 'Ε': 'e', 'Ζ': 'z', 'Η': 'i', 'Θ': 'th',
    'Ι': 'i', 'Κ': 'k', 'Λ': 'l', 'Μ': 'm', 'Ν': 'n', 'Ξ': 'x', 'Ο': 'o', 'Π': 'p', 'Ρ': 'r', 'Σ': 's', 'Τ': 't',
    'Υ': 'y', 'Φ': 'f', 'Χ': 'ch', 'Ψ': 'ps', 'Ω': 'o',
})

_ACCENTS = str.maketrans({'á': 'a', 'à': 'a', 'â': 'a', 'ä': 'a', 'ã': 'a', 'å': 'a',
                          'é': 'e', 'è': 'e', 'ê': 'e', 'ë': 'e',
                          'í': 'i', 'ì': 'i', 'î': 'i', 'ï': 'i',
                          'ó': 'o', 'ò': 'o', 'ô': 'o', 'ö': 'o', 'õ': 'o',
                          'ú': 'u', 'ù': 'u', 'û': 'u', 'ü': 'u',
                          'ç': 'c', 'ñ': 'n', 'ß': 'ss', 'ý': 'y'})

def norm(s):
    """Normalize a station name for matching: lowercase, transliterate Greek,
    strip accents/umlauts, drop frequencies and 'FM' suffixes, keep [a-z0-9 ]."""
    s = str(s or '').lower().translate(_GREEK_MAP).translate(_ACCENTS)
    s = s.replace('’', "'").replace('΄', "'").replace('æ', 'ae').replace('œ', 'oe')
    s = re.sub(r'\b\d+[.,]\d+\b', ' ', s)      # 100.3 / 100,3
    s = re.sub(r'\b\d+\s?fm\b', ' ', s)
    s = re.sub(r'fm\s*\d+', ' ', s)
    s = re.sub(r'[^\w\s]', ' ', s)
    return re.sub(r'\s+', ' ', s).strip()

def norm_freq(s):
    """Like norm() but KEEPS frequencies — for stations whose identity IS the
    frequency (104.6 RTL, Radio Gong 96.3). Avoids key collisions like
    norm('radio gong 96.3') == norm('radio gong 106.9') == 'radio gong'."""
    s = str(s or '').lower().translate(_GREEK_MAP).translate(_ACCENTS)
    s = s.replace('’', "'").replace('΄', "'").replace('æ', 'ae').replace('œ', 'oe')
    s = re.sub(r'[^\w\s.]', ' ', s)           # keep digits and dots
    return re.sub(r'\s+', ' ', s).strip()

def url_norm(u):
    """Normalize a stream URL for dedupe: strip query/fragment, ignore http vs https."""
    u = (u or '').split('?')[0].split(';')[0].strip()
    return u.replace('https://', 'http://')

def clean_name(n):
    """Strip Wikipedia link-text artifacts like  Name "LinkedName")  or  Name (LinkedName)."""
    n = re.sub(r'\s+"[^"]*"\)?$', '', n)   # 'Third Programme "Third Programme (ERT)")' -> 'Third Programme'
    n = re.sub(r'\s*\([^)]*\)\s*$', '', n)  # trailing '(Greece)' etc.
    return n.strip()

# ---------------------------------------------------------------- city alias cleanup
def norm_city(city, aliases):
    """The canonical city: aliases[norm(city)] if present, else the city trimmed and title-cased.
    aliases is the country YAML's `city_aliases` mapping (build_stations.load_country; empty for a
    collection). A key is compared verbatim with norm(city); city_aliases() guarantees every key is
    already in norm() form, so each one can match."""
    if not city:
        return ''
    c = city.strip().strip('.').title()
    return aliases.get(norm(city), c)

def city_aliases(value, source):
    """A YAML `city_aliases` block checked as a mapping of non-empty strings to non-empty strings
    whose keys are already in norm() form (missing or empty = {}). A bad block is a hard error
    naming source: an unquoted key like `no:` (YAML reads it as a boolean) or a key with capitals,
    accents or punctuation would otherwise never match and fail silently. So is a chain, a city
    whose own norm() is another key mapping elsewhere (`thueringen: Thüringen` beside
    `thuringen: Thuringia`): norm_city runs again on the aliased city in the final dedupe, so the
    rows would carry one city and be deduped under another."""
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise ValueError(f'{source}: city_aliases must be a mapping of alias -> city')
    bad = [f'{k!r}: {v!r}' for k, v in value.items()
           if not (isinstance(k, str) and k.strip() and isinstance(v, str) and v.strip())]
    if bad:
        raise ValueError(f'{source}: city_aliases entries must be non-empty strings (quote them): '
                         + ', '.join(bad))
    unnormalized = [f'{k!r} (write it as {norm(k)!r})' if norm(k) else f'{k!r} (nothing left once normalized)'
                    for k in value if norm(k) != k]
    if unnormalized:
        raise ValueError(f'{source}: city_aliases keys must be in normalized form (lowercase, no '
                         'accents or punctuation) or they never match: ' + ', '.join(unnormalized))
    chains = [f'{k!r}: {v!r} (but {norm(v)!r}: {value[norm(v)]!r})' for k, v in value.items()
              if value.get(norm(v), v) != v]
    if chains:
        raise ValueError(f'{source}: city_aliases must not chain: a city whose normalized form is itself '
                         'a key must be that key\'s city too (map both keys to one city): ' + ', '.join(chains))
    return dict(value)

# ---------------------------------------------------------------- languages (data/languages.yaml)
def language_key(s):
    """The lookup key of a language token: NFC, str.lower(), every whitespace run -> one space, trimmed."""
    return ' '.join(unicodedata.normalize('NFC', str(s)).lower().split())

def _key_problem(what, k):
    """Why k cannot be a key of the language table, or None (a key must be a string in key form)."""
    if not isinstance(k, str):
        return f'{what} {k!r} is not a string (quote it: YAML reads an unquoted no, yes, on or off as a boolean)'
    if not language_key(k):
        return f'{what} {k!r} is empty'
    if k != language_key(k):
        return f'{what} {k!r} is not in key form and would never match (write it as {language_key(k)!r})'
    return None

def language_replace(value, source):
    """A country YAML's `language_replace` block: a radio-browser language value (the whole value, in
    language_key form) -> the language the canonical CSV gets instead (missing or empty = {}). A key not in
    key form, or an empty value, is a hard error naming source: it would never apply and fail silently."""
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise ValueError(f'{source}: language_replace must be a mapping of radio-browser language -> language')
    problems = [_key_problem('language_replace key', k) for k in value]
    problems += [f'language_replace {k!r} must map to a non-empty string' for k, v in value.items()
                 if not (isinstance(v, str) and v.strip())]
    problems = [p for p in problems if p]
    if problems:
        raise ValueError(f'{source}: ' + '; '.join(problems))
    return dict(value)

def language_table(value, source):
    """data/languages.yaml checked as one lookup: each canonical name's key -> (name,), each alias key ->
    its names, each drop key -> (). A bad table is a hard ValueError naming source and every problem:
    a key never matching, or an alias to a name that is not listed, would otherwise fail silently."""
    if not isinstance(value, dict):
        raise ValueError(f'{source}: must be a mapping with a non-empty languages list (and optional aliases '
                         'and drop)')
    problems = [f'unknown top-level key {k!r} (expected languages, aliases, drop)'
                for k in value if k not in ('languages', 'aliases', 'drop')]
    names, aliases, drop = value.get('languages'), value.get('aliases') or {}, value.get('drop') or []
    if not isinstance(names, list) or not names:
        problems.append('languages must be a non-empty list of canonical names')
        names = []
    if not isinstance(aliases, dict):
        problems.append('aliases must be a mapping of key -> canonical name or list of names')
        aliases = {}
    if not isinstance(drop, list):
        problems.append('drop must be a list of keys')
        drop = []
    table, listed = {}, set()
    for name in names:
        if not (isinstance(name, str) and name and name == ' '.join(name.split())
                and name == unicodedata.normalize('NFC', name)) or ',' in name or ';' in name:
            problems.append(f'canonical name {name!r} must be a non-empty NFC string, trimmed with single '
                            'spaces, without , or ;')
            continue
        key = language_key(name)
        if key in table:
            problems.append(f'canonical names {table[key][0]!r} and {name!r} have the same key {key!r}')
            continue
        table[key] = (name,)
        listed.add(name)
    for k, target in aliases.items():
        problem = _key_problem('alias key', k)
        if problem:
            problems.append(problem)
            continue
        targets = [target] if isinstance(target, str) else target
        if not isinstance(targets, list) or not targets:
            problems.append(f'alias {k!r} must map to a canonical name or a non-empty list of them')
        elif k in table:
            problems.append(f'alias key {k!r} is the key of a canonical name, which already matches itself '
                            'in any case')
        else:
            unlisted = [t for t in targets if not isinstance(t, str) or t not in listed]
            if unlisted:
                problems.append(f'alias {k!r} maps to {unlisted!r}, not listed under languages')
            else:
                table[k] = tuple(targets)
    for k in drop:
        problem = _key_problem('drop key', k)
        if problem:
            problems.append(problem)
        elif k in aliases:
            problems.append(f'key {k!r} is both an alias and a drop')
        elif table.get(k) == ():
            problems.append(f'drop key {k!r} is listed twice')
        elif k in table:
            problems.append(f'drop key {k!r} is the key of a canonical name')
        else:
            table[k] = ()
    if problems:
        raise ValueError(f'{source}: ' + '; '.join(problems))
    return table

# ---------------------------------------------------------------- row helpers
# The provenance label of a radio-browser extra's notes: build_stations writes f'{RB_TAGS_LABEL} {tags}'
# (radio-browser's raw comma-joined tag list) and app_catalog formats that note for the app.
RB_TAGS_LABEL = 'tags:'

def row_score(r):
    """Which of two duplicate rows to keep: prefer a frequency, a curated source, then more votes."""
    return (bool(r.get('frequency_fm')), str(r.get('source', '')).startswith('curated'),
            r.get('votes') or 0)

def app_tag(row):
    """The 'Description / genre' text the DialShift Add-station dialog wants: 'type · genre'.
    The genre is left out when it is 'Other' or repeats the type; empty parts are dropped, so the
    tag never starts or ends with the separator or doubles it."""
    t, g = (str(row.get(k) or '').strip() for k in ('type', 'genre'))
    parts = [t, '' if g == 'Other' else g]
    return ' · '.join(dict.fromkeys(p for p in parts if p))

# ---------------------------------------------------------------- classification
def classify(desc, tags, country='GR'):
    """Return (type, genre) from description + tag keywords. Country-tuned."""
    d = ((desc or '') + ' ' + (tags or '')).lower()
    if any(k in d for k in ['sport', 'αθλητ']):
        return 'Sports', 'Sports'
    if any(k in d for k in ['news', 'talk', 'info ', 'information', 'ειδήσ', 'ενημέρωσ', 'nachrichten',
                            'aktuelle', 'nachricht']):
        if any(k in d for k in ['laiko', 'λαϊκ', 'rebetiko', 'greek music', 'greek pop', 'schlager',
                                'volksmusik', 'chanson', 'music']):
            return 'News & Talk', 'News & Talk + Music'
        return 'News & Talk', 'News & Talk'
    if any(k in d for k in ['orthodox', 'religious', 'εκκλησ', 'church', 'christ', 'katholisch', 'gospel',
                            'kirche', 'catholique']):
        return 'Religious', 'Religious'
    if any(k in d for k in ['children', 'kids', 'παιδ', 'kinder']):
        return 'Music', 'Children'
    if any(k in d for k in ['classical', 'κλασικ', 'klassik', 'classique']):
        return 'Music', 'Classical'
    if any(k in d for k in ['world music', 'worldmusic']):
        return 'Music', 'World music'
    if any(k in d for k in ['jazz', 'soul', 'funk']):
        return 'Music', 'Jazz / Soul'
    if any(k in d for k in ['rock', 'metal', 'ροκ', 'alternative']):
        return 'Music', 'Rock / Alternative'
    if any(k in d for k in ['electronic', 'dance', 'house', 'techno', 'ηλεκτρον', 'electro']):
        return 'Music', 'Electronic / Dance'
    if any(k in d for k in ['hip hop', 'hip-hop', 'rap', 'urban', 'rnb', 'r&b']):
        return 'Music', 'Hip-hop / Urban'
    if any(k in d for k in ['laiko', 'λαϊκ', 'rebetiko', 'ρεμπέτ']):
        return 'Music', 'Greek laïkó'
    if any(k in d for k in ['entekhno', 'έντεχν', 'chanson', 'francaise']):
        return 'Music', 'Éntekhno / Chanson'
    if any(k in d for k in ['schlager', 'volksmusik', 'volkstümlich', 'volkstumlich', 'oktoberfest']):
        return 'Music', 'Schlager / Folk'
    if any(k in d for k in ['cretan', 'pontic', 'traditional', 'folk', 'κρητ', 'ποντ', 'δημοτ', 'παραδοσ']):
        return 'Music', 'Traditional / Folk'
    if any(k in d for k in ['greek pop', 'ελληνικ']):
        return 'Music', 'Greek pop'
    if any(k in d for k in ['greek music', 'greek']):
        return 'Music', 'Greek music'
    if any(k in d for k in ['oldies', '80s', '90s', 'nostalgie', 'old']):
        return 'Music', 'Oldies / 80s-90s'
    if any(k in d for k in ['top40', 'hits', 'mainstream']):
        return 'Music', 'Pop / Hits'
    if any(k in d for k in ['pop', 'pop music']):
        return 'Music', 'Pop'
    if any(k in d for k in ['music', 'μουσικ', 'musik', 'musique']):
        return 'Music', 'Mixed music'
    return 'Other', 'Other'

# ---------------------------------------------------------------- fetching
def fetch_json(url, tries=3):
    for a in range(tries):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'DialShiftRadioCatalog/1.0'})
            return json.loads(urllib.request.urlopen(req, timeout=30, context=_CTX).read())
        except Exception:
            time.sleep(2)
    raise RuntimeError(f'fetch failed after {tries} tries: {url}')

def fetch_text(url, tries=3):
    for a in range(tries):
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)'})
            return urllib.request.urlopen(req, timeout=30, context=_CTX).read().decode('utf-8', 'ignore')
        except Exception:
            time.sleep(2)
    raise RuntimeError(f'fetch failed after {tries} tries: {url}')

def fetch_radio_browser(country_code, country, force=False):
    """Download ALL stations for a country from radio-browser.info (mirror fallback).
    country is the full English name from the country YAML's `name`: radio-browser's
    /stations/bycountry/ endpoint matches the NAME as a substring, so the ISO code would not
    work ('DE' hits "Russian FeDEration")."""
    out = RAW_DIR / country_code / 'radio-browser.json'
    if out.exists() and not force:
        return json.loads(out.read_text(encoding='utf-8'))
    path = urllib.parse.quote(country)
    mirrors = fetch_json('http://all.api.radio-browser.info/json/servers')
    stations, base = None, None
    for m in mirrors:
        try:
            stations = fetch_json(f"https://{m['name']}/json/stations/bycountry/{path}"
                                  f"?limit=1&hidebroken=false")
            base = m['name']
            break
        except Exception:
            continue
    if base is None:
        raise RuntimeError('no radio-browser mirror reachable')
    all_rows = []
    offset = 0
    while True:
        batch = fetch_json(f"https://{base}/json/stations/bycountry/{path}"
                           f"?limit=1000&offset={offset}&hidebroken=false")
        all_rows.extend(batch)
        if len(batch) < 1000:
            break
        offset += 1000
        time.sleep(0.5)
    # sanity: keep only rows that actually belong to the country (guards against
    # substring quirks in the API)
    keep = [s for s in all_rows if (s.get('countrycode') or '').upper() == country_code.upper()
            or country.lower() in (s.get('country') or '').lower()]
    if keep:
        all_rows = keep
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(all_rows, ensure_ascii=False), encoding='utf-8')
    return all_rows

def dedupe_rb(entries):
    """Group radio-browser entries by (norm name, stream host); keep best per group."""
    groups = {}
    for s in entries:
        url = s.get('url_resolved') or s.get('url') or ''
        if not url.startswith(('http://', 'https://')):
            continue
        host = re.sub(r'^https?://', '', url).split('/')[0]
        key = (norm(s['name']), host)
        groups.setdefault(key, []).append(s)
    out = []
    for grp in groups.values():
        grp.sort(key=lambda s: (-(s.get('lastcheckok') or 0), -(s.get('votes') or 0)))
        out.append(grp[0])
    return out
