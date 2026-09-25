"""Shared helpers for the DialShift radio data builders.

Everything here is stdlib-only; the XLSX writer lives in build_all.py (needs openpyxl).
"""
import json
import re
import ssl
import time
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
    collection). A key is compared verbatim with norm(city), so only a key already in norm() form
    (lowercase, no accents or punctuation) can match."""
    if not city:
        return ''
    c = city.strip().strip('.').title()
    return aliases.get(norm(city), c)

def city_aliases(value, source):
    """A YAML `city_aliases` block checked as a mapping of non-empty strings to non-empty strings
    (missing or empty = {}). A bad block is a hard error naming source: YAML reads an unquoted
    key like `no:` as a boolean, which would otherwise never match and fail silently."""
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise ValueError(f'{source}: city_aliases must be a mapping of alias -> city')
    bad = [f'{k!r}: {v!r}' for k, v in value.items()
           if not (isinstance(k, str) and k.strip() and isinstance(v, str) and v.strip())]
    if bad:
        raise ValueError(f'{source}: city_aliases entries must be non-empty strings (quote them): '
                         + ', '.join(bad))
    return dict(value)

# ---------------------------------------------------------------- row helpers
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
