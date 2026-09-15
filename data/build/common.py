"""Shared helpers for the DialShift radio data builders.

Everything here is stdlib-only; the XLSX writer lives in build_all.py (needs openpyxl).
"""
import json
import re
import ssl
import time
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

def clean_name(n):
    """Strip Wikipedia link-text artifacts like  Name "LinkedName")  or  Name (LinkedName)."""
    n = re.sub(r'\s+"[^"]*"\)?$', '', n)   # 'Third Programme "Third Programme (ERT)")' -> 'Third Programme'
    n = re.sub(r'\s*\([^)]*\)\s*$', '', n)  # trailing '(Greece)' etc.
    return n.strip()

# ---------------------------------------------------------------- city alias cleanup
CITY_ALIASES = {
    'GR': {'in athens': 'Athens', 'athens greece': 'Athens', 'attiki': 'Athens', 'attica': 'Athens',
           'attica, athens': 'Athens', 'patra': 'Patras', 'larisa': 'Larissa', 'heraclion': 'Heraklion',
           'heraklion crete': 'Heraklion', 'kreta': 'Crete', 'thessaloniki-notia macedonia': 'Thessaloniki',
           'piraeus': 'Piraeus', 'piraias': 'Piraeus', 'chalkida': 'Chalkida'},
    'DE': {'munchen': 'Munich', 'muenchen': 'Munich', 'ismaning': 'Ismaning (Munich)',
           'unterfohring': 'Unterföhring (Munich)', 'koln': 'Cologne', 'koeln': 'Cologne',
           'frankfurt am main': 'Frankfurt', 'dusseldorf': 'Düsseldorf', 'nuernberg': 'Nuremberg',
           'stuttgart': 'Stuttgart'},
    'FR': {'ile-de-france': 'Paris', 'île-de-france': 'Paris', 'auvergne-rhone-alpes': 'Lyon',
           'provence-alpes-cote dazur': 'Marseille'},
}

def norm_city(city, country):
    if not city:
        return ''
    c = city.strip().strip('.').title()
    return CITY_ALIASES.get(country, {}).get(norm(city), c)

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

# radio-browser's /stations/bycountry/ endpoint matches the country NAME as a
# substring (so ISO code 'DE' hits "Russian FeDEration"!). Always use the full name.
COUNTRY_NAMES = {'GR': 'Greece', 'FR': 'France', 'DE': 'Germany'}

def fetch_radio_browser(country_code, force=False):
    """Download ALL stations for a country from radio-browser.info (mirror fallback)."""
    out = RAW_DIR / country_code / 'radio-browser.json'
    if out.exists() and not force:
        return json.loads(out.read_text(encoding='utf-8'))
    country = COUNTRY_NAMES.get(country_code, country_code)
    mirrors = fetch_json('http://all.api.radio-browser.info/json/servers')
    stations, base = None, None
    for m in mirrors:
        try:
            stations = fetch_json(f"https://{m['name']}/json/stations/bycountry/{country}"
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
        batch = fetch_json(f"https://{base}/json/stations/bycountry/{country}"
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
