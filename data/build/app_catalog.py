#!/usr/bin/env python3
"""The app catalog: data/output/app-catalog.json, the station list behind the Add-station picker.

Contract: docs/catalog-contracts.md §2 (decisions D59, D69, D71, D84). Pure export, validation and writer;
build_all.py calls build_app_catalog, validate_app_catalog and write_app_catalog with the same rows it
writes to data/canonical/, after the CSVs and before the XLSX. Station facts come only from those rows
(YAML + sources) and language facts only from data/languages.yaml (the table build_all.py passes in);
nothing here names a station, a URL, a country or a language.

Stdlib only, so the fixture checks run anywhere:
    data/.venv/bin/python data/build/app_catalog.py --self-test
"""
import json
import os
import re
import string
import sys
import tempfile
import unicodedata
from pathlib import Path
from urllib.parse import urlsplit

from common import (RB_TAGS_LABEL, app_tag, city_aliases, language_key, language_table, norm, norm_city,
                    row_score, url_norm)

SCHEMA_VERSION = 1
MAX_ENTRIES = 10_000
MAX_URL_LENGTH = 2_048
KEYS = ('name', 'name_local', 'country', 'country_label', 'city', 'region', 'frequency_fm', 'type', 'genre',
        'language', 'internet_only', 'stream_url', 'codec', 'bitrate', 'votes', 'notes', 'logo', 'tag')
LANGUAGE_SEPARATOR = ', '

# Collections (build_stations.build_collection) carry this country instead of a YAML code.
COLLECTION_COUNTRY = 'Internet'
COLLECTION_LABEL = 'Internet (collections)'
# The canonical CSVs' "unknown" markers; the app gets "" so a marker never becomes a filter value.
PLACEHOLDERS = {'city': '—', 'region': '(unlisted)'}

_STRING_KEYS = tuple(k for k in KEYS if k not in ('internet_only', 'bitrate', 'votes'))
_GENERATED_UTC = re.compile(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z')
_INTEGER = re.compile(r'-?[0-9]+')
# A note that is only a source label ("tags:", "curated:", ...) with nothing but whitespace or
# punctuation after it: the pipeline's provenance prefix of an empty note, never shown in the app.
_BARE_LABEL = re.compile(r'[^\W_][\w+\-]*:[\W_]*')
_EMPHASIS = re.compile(r'(?<!\w)_([^_\n]+?)_(?!\w)')        # Wikipedia _emphasis_ markers
_LIST = re.compile('[,;]')                  # radio-browser's list syntax (languages, tags); not - / or .
TAGS_NOTE_LABEL = 'Tags:'                   # the app's label of a formatted radio-browser tag note
TAG_SEPARATOR = ', '


def _text(value):
    return '' if value is None else str(value).strip()


def _integer(value):
    """An int from an int or a string of digits; anything else is None."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    s = _text(value)
    return int(s) if _INTEGER.fullmatch(s) else None


def _http_url(url):
    """(scheme, hostname) of an http(s) URL with a host, else None."""
    try:
        parts = urlsplit(url)
        parts.port                                   # raises ValueError on an invalid port
    except ValueError:
        return None
    if parts.scheme not in ('http', 'https') or not parts.hostname:
        return None
    return parts.scheme, parts.hostname


def valid_stream_url(url: str) -> bool:
    """The app's URL rule: SettingsStore.ValidUrl (absolute, http/https, a host) plus BHV-52's
    2,048-character limit, counted in UTF-16 code units as the app's string length is."""
    url = _text(url)
    if not url or len(url.encode('utf-16-le')) // 2 > MAX_URL_LENGTH:
        return False
    if any(c.isspace() or unicodedata.category(c) == 'Cc' for c in url):
        return False
    return _http_url(url) is not None


def _tags_note(tags):
    """A radio-browser tag list (the text after RB_TAGS_LABEL) as the app shows it: "Tags: a, b". Split on
    , and ; (the list syntax, as for languages); each tag's whitespace runs collapsed and trimmed; a tag
    without a letter or digit (a bare "#") dropped; repeats dropped case-insensitively (NFC, casefold),
    keeping the first spelling. "" when no tag is left."""
    kept = {}
    for tag in _LIST.split(tags):
        tag = ' '.join(tag.split())
        if any(c.isalnum() for c in tag):
            kept.setdefault(unicodedata.normalize('NFC', tag).casefold(), tag)
    return f'{TAGS_NOTE_LABEL} {TAG_SEPARATOR.join(kept.values())}' if kept else ''


def _notes(value):
    """Trimmed notes without Wikipedia _emphasis_ markers; "" when nothing readable is left: a bare
    source label (optionally followed by punctuation only) or no letter or digit at all. A radio-browser
    tag note ("tags: music,variety") is formatted by _tags_note; every other note is kept as it is."""
    s = _EMPHASIS.sub(r'\1', _text(value))
    if _BARE_LABEL.fullmatch(s) or not any(c.isalnum() for c in s):
        return ''
    if s.startswith(RB_TAGS_LABEL):
        return _tags_note(s[len(RB_TAGS_LABEL):])
    return s


def unknown_language_name(token: str) -> str:
    """The exported name of a token the language table does not know: its string.capwords form (NFC)."""
    return string.capwords(unicodedata.normalize('NFC', token))


def normalize_language(raw: str, table: dict[str, tuple[str, ...]]) -> tuple[str, list[str]]:
    """§2.6: the exported language value, and the unknown tokens in the order met (as written, whitespace
    collapsed). Splits on , and ; only; each token becomes the table's names for its language_key (a
    canonical name, an alias's names, nothing for a drop key) or, when unknown, its capwords form. Names
    are kept once, first seen first, and joined with LANGUAGE_SEPARATOR."""
    names, unknown = [], []
    for part in _LIST.split(_text(raw)):
        part = ' '.join(part.split())
        if not part:
            continue
        found = table.get(language_key(part))
        if found is None:
            unknown.append(part)
            found = (unknown_language_name(part),)
        names += found
    return LANGUAGE_SEPARATOR.join(dict.fromkeys(names)), unknown


def _entry(row, country_label, language):
    """One canonical row as an app-catalog station (keys in KEYS order, §2.1 normalization), with its
    language already normalized (normalize_language)."""
    s = {k: _text(row.get(k)) for k in ('name', 'name_local', 'country', 'city', 'region', 'frequency_fm',
                                         'type', 'genre', 'stream_url', 'codec', 'logo')}
    for key, marker in PLACEHOLDERS.items():
        if s[key] == marker:
            s[key] = ''
    bitrate, votes = _integer(row.get('bitrate')), _integer(row.get('votes'))
    return {
        'name': s['name'], 'name_local': s['name_local'], 'country': s['country'],
        'country_label': country_label, 'city': s['city'], 'region': s['region'],
        'frequency_fm': s['frequency_fm'], 'type': s['type'], 'genre': s['genre'], 'language': language,
        'internet_only': _text(row.get('internet_only')) == 'Yes', 'stream_url': s['stream_url'],
        'codec': s['codec'], 'bitrate': bitrate if bitrate is not None and bitrate > 0 else None,
        'votes': votes if votes is not None and votes >= 0 else None, 'notes': _notes(row.get('notes')),
        'logo': s['logo'] if _http_url(s['logo']) else '',
        'tag': app_tag({'type': s['type'], 'genre': s['genre']}),
    }


def _working_url(row):
    """The stripped stream URL of a Working row, else ''."""
    return _text(row.get('stream_url')) if _text(row.get('stream_status')) == 'Working' else ''


def _order(entry):
    return entry['country'], -(entry['votes'] or 0), entry['name'], entry['stream_url']


def build_app_catalog(sources: list[tuple[dict, list[dict]]], generated_utc: str,
                      languages: dict[str, tuple[str, ...]]) -> tuple[dict, dict]:
    """sources: (yaml cfg, canonical rows) per country and per collection, in build order; languages: the table
    of common.language_table. Returns (document, stats) with stats keys working, url_excluded, duplicates_removed,
    exported, languages (distinct names exported) and unknown_languages ({key: (first spelling, entry count)})."""
    working = url_excluded = duplicates = 0
    kept = {}                                       # dedupe key -> (row, country_label); first wins ties
    for cfg, rows in sources:
        for row in rows:
            url = _working_url(row)
            if not url:
                continue
            working += 1
            if not valid_stream_url(url):
                url_excluded += 1
                continue
            country = _text(row.get('country'))
            label = COLLECTION_LABEL if country == COLLECTION_COUNTRY else _text(cfg.get('name'))
            # the pipeline's final dedupe key (build_stations.build_country), per country, with the
            # source YAML's city_aliases
            key = (country, norm(row.get('name')).replace(' ', ''),
                   norm_city(_text(row.get('city')), cfg.get('city_aliases') or {}), url_norm(url))
            prev = kept.get(key)
            if prev is not None:
                duplicates += 1
                if _score(row) <= _score(prev[0]):
                    continue
            kept[key] = (row, label)
    # the language after the dedupe: its key, row_score and the order never read it (§2.2)
    stations, unknown = [], {}                      # unknown: key -> (first spelling, entry count)
    for row, label in kept.values():
        language, tokens = normalize_language(row.get('language'), languages)
        for key, token in {language_key(t): t for t in reversed(tokens)}.items():   # once per entry, first spelling
            first, count = unknown.get(key, (token, 0))
            unknown[key] = (first, count + 1)
        stations.append(_entry(row, label, language))
    stations.sort(key=_order)
    names = {n for e in stations if e['language'] for n in e['language'].split(LANGUAGE_SEPARATOR)}
    doc = {'schema_version': SCHEMA_VERSION, 'generated_utc': generated_utc, 'stations': stations}
    return doc, {'working': working, 'url_excluded': url_excluded, 'duplicates_removed': duplicates,
                 'exported': len(stations), 'languages': len(names), 'unknown_languages': unknown}


def _score(row):
    return row_score({**row, 'votes': _integer(row.get('votes')) or 0})


def _language_problem(value, mapped):
    """Rule 7 of §2.3 for one language value, or None when it holds: "" or names joined with
    LANGUAGE_SEPARATOR, each non-empty, trimmed with single spaces, free of , and ;, not repeated, and not a
    key the table maps elsewhere (mapped: the alias and drop keys), so every mapping was applied."""
    if value == '':
        return None
    names = value.split(LANGUAGE_SEPARATOR)
    for name in names:
        if not name or name != ' '.join(name.split()) or ',' in name or ';' in name:
            return f'name {name!r} is empty, untrimmed or has doubled spaces, or contains , or ;'
        if language_key(name) in mapped:
            return f'name {name!r} is an alias or drop key of the language table (the mapping was not applied)'
    if len(set(names)) != len(names):
        return 'a name appears twice'
    return None


def _mapped_keys(languages):
    """The alias and drop keys of a language_table lookup: every key but the canonical names' own."""
    return {k for k, v in languages.items() if not (len(v) == 1 and language_key(v[0]) == k)}


def _entry_problems(i, e, mapped):
    """Rules 2, 3 and 7 of §2.3 for one entry, plus the §2.1 guarantees of its values."""
    if not isinstance(e, dict):
        return [f'stations[{i}]: not an object']
    where = f"stations[{i}] ({e.get('name')!r}, {e.get('country')!r})"
    if tuple(e) != KEYS:
        return [f'{where}: keys {list(e)} are not exactly {list(KEYS)} in order']
    out = []
    for k in _STRING_KEYS:
        if not isinstance(e[k], str):
            out.append(f'{where}: {k} is not a string')
        elif e[k] != e[k].strip():
            out.append(f'{where}: {k} is not trimmed')
    if not isinstance(e['internet_only'], bool):
        out.append(f'{where}: internet_only is not a boolean')
    for k, low in (('bitrate', 1), ('votes', 0)):
        v = e[k]
        if v is not None and (isinstance(v, bool) or not isinstance(v, int) or v < low):
            out.append(f'{where}: {k} is not null or an integer >= {low}')
    if out:
        return out
    if not e['name']:
        out.append(f'{where}: empty name')
    if not e['country']:
        out.append(f'{where}: empty country')
    if not valid_stream_url(e['stream_url']):
        out.append(f"{where}: stream_url fails the URL rule: {e['stream_url'][:120]!r}")
    for key, marker in PLACEHOLDERS.items():
        if e[key] == marker:
            out.append(f'{where}: {key} is the placeholder {marker!r}')
    if e['logo'] and not _http_url(e['logo']):
        out.append(f'{where}: logo is not an http(s) URL')
    if e['notes'].startswith(RB_TAGS_LABEL):
        out.append(f'{where}: notes is a raw radio-browser tag list (not formatted as {TAGS_NOTE_LABEL!r})')
    if not e['tag']:
        out.append(f'{where}: empty tag')
    language = _language_problem(e['language'], mapped)
    if language:
        out.append(f"{where}: language {e['language']!r}: {language}")
    return out


def validate_app_catalog(doc: dict, source_rows: list[dict], languages: dict[str, tuple[str, ...]]) -> list[str]:
    """Every problem found (empty list = valid)."""
    problems, mapped = [], _mapped_keys(languages)
    version = doc.get('schema_version')
    if isinstance(version, bool) or version != SCHEMA_VERSION:
        problems.append(f'schema_version is {version!r}, not {SCHEMA_VERSION}')
    generated = doc.get('generated_utc')
    if not isinstance(generated, str) or not _GENERATED_UTC.fullmatch(generated):
        problems.append(f'generated_utc {generated!r} is not yyyy-MM-ddTHH:mm:ssZ')
    stations = doc.get('stations')
    if not isinstance(stations, list):
        return problems + ['stations is not a list']
    if not 1 <= len(stations) <= MAX_ENTRIES:
        problems.append(f'{len(stations)} stations, outside 1..{MAX_ENTRIES}')
    triples = []
    for i, e in enumerate(stations):
        found = _entry_problems(i, e, mapped)
        problems += found
        if not found:
            triples.append((e['country'], e['name'], e['stream_url']))
    seen = set()
    for t in triples:
        if t in seen:
            problems.append(f'duplicate (name, country, stream_url): {(t[1], t[0], t[2])!r}')
        seen.add(t)
    expected = {(_text(r.get('country')), _text(r.get('name')), _working_url(r))
                for r in source_rows if valid_stream_url(_working_url(r))}
    if len(stations) != len(expected):
        problems.append(f'{len(stations)} stations exported, but the canonical rows hold {len(expected)} '
                        f'Working (country, name, stream_url) that pass the URL rule')
    problems += [f'Working row missing from the export (a dedupe dropped it; fix the YAML): {t!r}'
                 for t in sorted(expected - seen)]
    problems += [f'exported station not among the Working canonical rows: {t!r}' for t in sorted(seen - expected)]
    return problems


def _serialize(doc):
    header = (f'{{"schema_version":{json.dumps(doc["schema_version"])},'
              f'"generated_utc":{json.dumps(doc["generated_utc"], ensure_ascii=False)},"stations":[')
    lines = [json.dumps(e, ensure_ascii=False, separators=(',', ':')) for e in doc['stations']]
    body = ',\n'.join(lines)
    return (header + '\n' + (body + '\n' if lines else '') + ']}\n').encode('utf-8')


def write_app_catalog(doc: dict, path: Path) -> None:
    """Serializes per §2.1 to a temp file next to path, then os.replace."""
    path = Path(path)
    data = _serialize(doc)
    fd, tmp = tempfile.mkstemp(prefix=f'.{path.name}.', suffix='.tmp', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        if json.loads(Path(tmp).read_bytes().decode('utf-8')) != doc:
            raise ValueError(f'{tmp}: the written catalog does not parse back to the document')
        os.chmod(tmp, 0o644)                         # mkstemp creates 0600; the catalog is a normal file
        os.replace(tmp, path)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise


# ------------------------------------------------------------------------------------------ self-test
# Inline fixtures only: fictional country codes, names and example.test URLs (no station facts). The
# language table is an inline stand-in for data/languages.yaml (no YAML read, so the test stays stdlib-only);
# its names are the contract's examples (docs/catalog-contracts.md §2.6).
_UTC = '2026-01-02T03:04:05Z'
_LANGUAGE_FIXTURE = {
    'languages': ['English', 'French', 'German', 'Greek', 'Low German', 'Luxembourgish', 'Serbo-Croatian'],
    'aliases': {'deutsch': 'German', 'deutch': 'German', 'gernan': 'German', 'français': 'French',
                'ελληνικά': 'Greek', 'american english': 'English', 'british english': 'English',
                'deutsch fränkisch': 'German', 'swiss german': 'German',
                'français - lëtzebuergesch': ['French', 'Luxembourgish'], 'english/ french': ['English', 'French']},
    'drop': ['instrumental', 'multilingual'],
}


def _row(**over):
    base = dict(country='XA', name='Fixture One', name_local='', city='Fixton', region='North',
                frequency_fm='', type='Music', genre='Pop', language='German', political_leaning='None',
                internet_only='No', stream_url='https://a.example.test/one', codec='MP3', bitrate=128,
                stream_status='Working', votes=10, logo='', notes='', source='radio-browser')
    base.update(over)
    return base


def self_test() -> int:
    failures, checks = [], 0

    def check(label, ok):
        nonlocal checks
        checks += 1
        if not ok:
            failures.append(label)

    langs = language_table(_LANGUAGE_FIXTURE, 'languages-fixture.yaml')
    xa, xb, coll ={'code': 'XA', 'name': 'Fixtureland'}, {'code': 'XB', 'name': 'Otherland'}, \
        {'code': 'FIX', 'name': 'Fixture Collection'}
    long_ok = 'https://a.example.test/' + 'x' * (2_048 - len('https://a.example.test/'))   # the contract's limit
    xa_rows = [
        _row(),
        _row(name='Down Station', stream_url='https://a.example.test/down', stream_status='Down'),
        _row(name='No Stream', stream_url='', stream_status='Working'),
        _row(name='Ftp Station', stream_url='ftp://a.example.test/ftp'),
        _row(name='Too Long', stream_url=long_ok + 'y'),
        _row(name='Just Fits', stream_url=long_ok, votes=0),
        _row(name='Hostless', stream_url='http:///only-a-path'),
        _row(name='Spaced Url', stream_url='https://a.example.test/a b'),
        _row(name='Control Url', stream_url='https://a.example.test/\x07'),
        _row(name='Bad Port', stream_url='https://a.example.test:99999/x'),
        # the same station twice (spacing, case and http/https differ): the one with a frequency stays
        _row(name='Fixture Two', stream_url='http://a.example.test/two', votes=50),
        _row(name='fixture  two', stream_url='https://a.example.test/two', votes=5, frequency_fm='99.9'),
        # normalization
        _row(name='  Padded Name  ', name_local=' Lokal ', city='—', region='(unlisted)', internet_only='Unknown',
             bitrate='', votes='', notes='tags: ', logo='null', stream_url=' https://a.example.test/pad ',
             type='Other', genre='Other'),
        _row(name='Numbers', internet_only='Yes', bitrate='0', votes=-3, notes='relays of _Some Name_ here',
             logo='ftp://a.example.test/l.png', stream_url='https://a.example.test/n', type='News & Talk',
             genre='News & Talk + Music'),
        _row(name='Strings', bitrate='192', votes='7', notes='tags: jazz,soul', logo='https://a.example.test/l.png',
             stream_url='https://a.example.test/s', genre=''),
        _row(name='Untyped', type='', genre='Ambient', stream_url='https://a.example.test/u'),
        _row(name='Bare Label', notes='curated: ', stream_url='https://a.example.test/b'),
        # order: votes desc, then name by code point (upper case before lower, accents last)
        _row(name='alpha', votes=3, stream_url='https://a.example.test/o1'),
        _row(name='Zeta', votes=3, stream_url='https://a.example.test/o2'),
        _row(name='Éclair', votes=3, stream_url='https://a.example.test/o3'),
        _row(name='Zeta', votes=3, stream_url='https://a.example.test/o0'),
    ]
    xb_rows = [_row(country='XB', name='Fixture One')]          # same station in another country: kept
    coll_rows = [_row(country=COLLECTION_COUNTRY, name='Fixture Chill', city='—', region='Internet radio',
                      internet_only='Yes', stream_url='https://c.example.test/chill', genre='Ambient')]
    sources = [(xa, xa_rows), (xb, xb_rows), (coll, coll_rows)]
    source_rows = [r for _, rows in sources for r in rows]
    doc, stats = build_app_catalog(sources, _UTC, langs)
    st = doc['stations']
    by_name = {}
    for e in st:
        by_name.setdefault(e['name'], []).append(e)

    check('stats: working counts Working rows with a URL',
          stats['working'] == sum(1 for r in source_rows if _working_url(r)))
    check('stats: ftp, too long, host-less, whitespace, control and bad-port URLs excluded',
          stats['url_excluded'] == 6)
    check('stats: one duplicate removed', stats['duplicates_removed'] == 1)
    check('stats: exported == len(stations)', stats['exported'] == len(st))
    for excluded in ('Down Station', 'No Stream', 'Ftp Station', 'Too Long', 'Hostless', 'Spaced Url',
                     'Control Url', 'Bad Port'):
        check(f'excluded: {excluded}', excluded not in by_name)
    check('a 2,048-character URL is included', 'Just Fits' in by_name)
    check('URL rule: 2,049 characters fails', not valid_stream_url(long_ok + 'y'))
    check('URL rule: 2,048 characters passes', valid_stream_url(long_ok))
    check('URL rule: https:// alone fails', not valid_stream_url('https://'))
    check('URL rule: javascript: fails', not valid_stream_url('javascript:alert(1)'))
    check('URL rule: upper-case scheme passes (urlsplit lower-cases it)', valid_stream_url('HTTP://a.example.test/'))
    two = [e for e in st if norm(e['name']).replace(' ', '') == 'fixturetwo']
    check('dedupe: one row left, the one with a frequency', len(two) == 1 and two[0]['frequency_fm'] == '99.9')
    check('dedupe is per country', sorted(e['country'] for e in by_name['Fixture One']) == ['XA', 'XB'])

    # city aliases: any country's YAML `city_aliases` block, no code per country
    aliases = city_aliases({'fixton north': 'Fixton'}, 'fixture.yaml')
    check('norm_city: the alias of the normalized city', norm_city(' FIXTON-north. ', aliases) == 'Fixton')
    check('norm_city: no alias -> trimmed, title case', norm_city(' old town. ', aliases) == 'Old Town'
          and norm_city('', aliases) == '')
    alias_rows = [_row(country='XC', name='Alias One', city='Fixton North', stream_url='https://x.example.test/a'),
                  _row(country='XC', name='Alias One', city='Fixton', stream_url='https://x.example.test/a')]
    _, with_block = build_app_catalog([({'code': 'XC', 'name': 'Aliasland', 'city_aliases': aliases}, alias_rows)],
                                      _UTC, langs)
    _, no_block = build_app_catalog([({'code': 'XC', 'name': 'Aliasland'}, alias_rows)], _UTC, langs)
    check('dedupe key uses the YAML city_aliases', with_block['duplicates_removed'] == 1
          and no_block['duplicates_removed'] == 0)
    check('city_aliases: missing block -> {}', city_aliases(None, 'fixture.yaml') == {})

    def rejected(block):
        try:
            city_aliases(block, 'fixture.yaml')
        except ValueError as e:
            return 'fixture.yaml' in str(e)
        return False

    check('city_aliases: a list, a boolean key (unquoted no:), an empty or null city are hard errors',
          all(rejected(b) for b in (['a'], {False: 'Fixton'}, {'a': ''}, {'a': None}, {' ': 'Fixton'})))

    def unnormalized_error(key):
        try:
            city_aliases({'fixton north': 'Fixton', key: 'Fixton'}, 'fixture.yaml')
        except ValueError as e:
            return str(e)
        return ''

    for key, want in (('fixton-north', "'fixton north'"), ('Fixton', "'fixton'"), ('fíxton', "'fixton'"),
                      ('fixton, north', "'fixton north'"), (' fixton', "'fixton'"),
                      ('fixton  north', "'fixton north'"), ('!!', 'nothing left once normalized')):
        msg = unnormalized_error(key)
        check(f'city_aliases: key {key!r} not in norm() form is a hard error naming file, key and {want}',
              'fixture.yaml' in msg and repr(key) in msg and want in msg and "'fixton north' (" not in msg)
    def chain_error(block):
        try:
            city_aliases(block, 'fixture.yaml')
        except ValueError as e:
            return str(e)
        return ''

    chained = chain_error({'fixtoen': 'Fixtön', 'fixton': 'Fixtonia'})    # norm('Fixtön') is the key 'fixton'
    check('city_aliases: a chain (a city whose norm() is a key mapping elsewhere) is a hard error naming both keys',
          'fixture.yaml' in chained and 'chain' in chained and "'fixtoen'" in chained and "'fixton'" in chained)
    group = city_aliases({'fixtoen': 'Fixtön', 'fixton': 'Fixtön', 'fixtonn': 'Fixtön'}, 'fixture.yaml')
    check('city_aliases: a group of spellings mapped to one city passes, the city\'s own key included',
          group['fixton'] == 'Fixtön')
    check('norm_city: every spelling of a group gives one city, and that city maps to itself (no chain)',
          {norm_city(c, group) for c in ('Fixtoen', 'FIXTÖN', 'Fixton,', 'fixtonn', 'Fixtön')} == {'Fixtön'})
    group_rows = [_row(country='XD', name='Group One', city=c, stream_url='https://x.example.test/g')
                  for c in ('Fixtoen', 'Fixtön')]
    _, grouped = build_app_catalog([({'code': 'XD', 'name': 'Groupland', 'city_aliases': group}, group_rows)],
                                   _UTC, langs)
    check('dedupe key: one station under two spellings of an aliased city is one duplicate',
          grouped['duplicates_removed'] == 1)
    check('city_aliases: keys already in norm() form pass (Greek transliterated form too)',
          city_aliases({'frankfurt am main': 'Frankfurt', 'thessaloniki': 'Thessaloniki', 'in athens': 'Athens'},
                       'fixture.yaml') == {'frankfurt am main': 'Frankfurt', 'thessaloniki': 'Thessaloniki',
                                           'in athens': 'Athens'})
    check('country_label from the YAML name', all(e['country_label'] == 'Fixtureland'
                                                  for e in st if e['country'] == 'XA'))
    chill = by_name['Fixture Chill'][0]
    check('collection: Internet / Internet (collections)',
          chill['country'] == COLLECTION_COUNTRY and chill['country_label'] == COLLECTION_LABEL)
    check('every entry has exactly KEYS in order', all(tuple(e) == KEYS for e in st))
    check('tag equals app_tag', all(e['tag'] == app_tag(r) for r in source_rows for e in by_name.get(r['name'], ())
                                    if e['stream_url'] == r['stream_url']))
    pad = by_name['Padded Name'][0]
    check('strings trimmed', pad['name_local'] == 'Lokal' and pad['stream_url'] == 'https://a.example.test/pad')
    check('placeholders -> ""', pad['city'] == '' and pad['region'] == '' and chill['city'] == '')
    check('internet_only Unknown/No -> false, Yes -> true', pad['internet_only'] is False
          and by_name['Fixture One'][0]['internet_only'] is False and by_name['Numbers'][0]['internet_only'] is True)
    num, strs = by_name['Numbers'][0], by_name['Strings'][0]
    check('bitrate: "" and 0 -> null, "192" -> 192, 128 -> 128', pad['bitrate'] is None and num['bitrate'] is None
          and strs['bitrate'] == 192 and by_name['Fixture One'][0]['bitrate'] == 128)
    check('votes: "" and negative -> null, "7" -> 7', pad['votes'] is None and num['votes'] is None
          and strs['votes'] == 7)
    check('notes: bare "tags:" -> "", _text_ -> text, a tag list formatted', pad['notes'] == ''
          and num['notes'] == 'relays of Some Name here' and strs['notes'] == 'Tags: jazz, soul')
    check('notes: a bare "curated:" label -> ""', by_name['Bare Label'][0]['notes'] == '')
    for note, want in (('tags:', ''), ('curated:', ''), (' curated:  ', ''), ('wiki:', ''), ('source:\t', ''),
                       ('curated+radio-browser:', ''), ('tags: ..', ''), ('curated: —', ''), ('tags: , ;', ''),
                       ('Πηγή:', ''), ('—', ''), (' .. ', ''), (None, ''), ('_curated_:', ''),
                       ('tags: 80s', 'Tags: 80s'), ('curated: pinned stream', 'curated: pinned stream'),
                       ('Info: 24/7', 'Info: 24/7'), ('Radio in Fixton:', 'Radio in Fixton:'),
                       ('source: _Some Wiki_', 'source: Some Wiki'), ('ok', 'ok')):
        check(f'notes: {note!r} -> {want!r}', _notes(note) == want)
    # radio-browser tag notes (D86): "Tags: " + the tags joined with ", "; every other note untouched
    nfd_cafe = unicodedata.normalize('NFD', 'café')
    for note, want in (('tags: music,variety', 'Tags: music, variety'),                  # the common shape
                       ('  tags:jazz , soul  ', 'Tags: jazz, soul'),                     # trimmed
                       ('tags: Rock,rock,ROCK,pop', 'Tags: Rock, pop'),                  # case-insensitive, first kept
                       ('tags: straße,STRASSE', 'Tags: straße'),                         # casefold, not lower
                       (f'tags: café,{nfd_cafe}', 'Tags: café'),                         # NFC before comparing
                       ('tags: ,, jazz ,,', 'Tags: jazz'),                               # empty tags dropped
                       ('tags: #,#charts,club  dance', 'Tags: #charts, club dance'),     # no letter/digit dropped
                       ('tags: darkwave; ebm; gothic,ebm', 'Tags: darkwave, ebm, gothic'),  # ; is a separator
                       ('tags: hip-hop,r&b/urban,top 40', 'Tags: hip-hop, r&b/urban, top 40'),  # not - or /
                       ('tags: #,-', ''),                                                # nothing readable left
                       ('tags: _Soul_,funk', 'Tags: Soul, funk'),                        # _emphasis_ first
                       ('Tags: music, variety', 'Tags: music, variety'),                 # idempotent
                       ('TAGS: a,b', 'TAGS: a,b'), ('Radio tags: a,b', 'Radio tags: a,b'),  # not the label
                       ('curated: tags,and,commas', 'curated: tags,and,commas')):
        check(f'notes: tag note {note!r} -> {want!r}', _notes(note) == want)
    check('logo: "null" and ftp -> "", https kept', pad['logo'] == '' and num['logo'] == ''
          and strs['logo'] == 'https://a.example.test/l.png')
    check('tag of Other/Other is "Other"', pad['tag'] == 'Other')
    check('tag of an empty type is the genre alone', by_name['Untyped'][0]['tag'] == 'Ambient')
    sep = ' · '
    tags = {(t, g): app_tag({'type': t, 'genre': g}) for t in ('', ' ', 'Music', 'Other', 'Jazz')
            for g in ('', ' ', 'Music', 'Other', 'Jazz')}
    check('app_tag: never a leading, trailing or doubled separator, always trimmed',
          all(not v.startswith(sep.strip()) and not v.endswith(sep.strip()) and sep * 2 not in v
              and f'{sep.strip()}{sep.strip()}' not in v and v == v.strip() for v in tags.values()))
    check('app_tag: type · genre, genre dropped when Other or equal to the type, empty parts dropped',
          tags['Music', 'Jazz'] == 'Music · Jazz' and tags['Music', 'Other'] == 'Music'
          and tags['Music', 'Music'] == 'Music' and tags['', 'Jazz'] == 'Jazz' and tags[' ', 'Jazz'] == 'Jazz'
          and tags['Music', ''] == 'Music' and tags['', 'Other'] == '' and tags['', ''] == '')
    check('order: country, votes desc (null as 0), name, stream_url by code point', st == sorted(st, key=_order))
    o = [(e['name'], e['stream_url'][-2:]) for e in st if e['stream_url'][-2:] in ('o0', 'o1', 'o2', 'o3')]
    check('order: code point tie-breaks', o == [('Zeta', 'o0'), ('Zeta', 'o2'), ('alpha', 'o1'), ('Éclair', 'o3')])
    check('countries in code-point order', [e['country'] for e in st] == sorted(e['country'] for e in st))

    # validation: the duplicate above had a different name spelling, so rule 5 reports the dropped row
    problems = validate_app_catalog(doc, source_rows, langs)
    check('rule 5 reports the deduped row', any('missing' in p and "'Fixture Two'" in p for p in problems)
          and any('stations exported' in p for p in problems))
    clean_rows = [r for r in source_rows if r['name'] != 'fixture  two']
    clean, _ = build_app_catalog([(xa, [r for r in xa_rows if r in clean_rows]), (xb, xb_rows), (coll, coll_rows)],
                                 _UTC, langs)
    check('a clean document validates', validate_app_catalog(clean, clean_rows, langs) == [])

    def broken(mutate):
        d = json.loads(json.dumps(clean))
        mutate(d)
        return validate_app_catalog(d, clean_rows, langs)

    check('rule 1: schema_version 2', broken(lambda d: d.update(schema_version=2)) != [])
    check('rule 1: schema_version true', broken(lambda d: d.update(schema_version=True)) != [])
    check('rule 1: generated_utc format', broken(lambda d: d.update(generated_utc='2026-01-02 03:04')) != [])
    check('rule 2: extra key', broken(lambda d: d['stations'][0].update(extra=1)) != [])
    check('rule 2: missing key', broken(lambda d: d['stations'][0].pop('logo')) != [])
    check('rule 2: key order', broken(lambda d: d['stations'].__setitem__(
        0, dict(reversed(list(d['stations'][0].items()))))) != [])
    check('rule 2: votes as a string', broken(lambda d: d['stations'][0].update(votes='12')) != [])
    check('rule 2: bitrate 0', broken(lambda d: d['stations'][0].update(bitrate=0)) != [])
    check('rule 2: internet_only as a string', broken(lambda d: d['stations'][0].update(internet_only='No')) != [])
    check('rule 2: city placeholder', broken(lambda d: d['stations'][0].update(city='—')) != [])
    check('rule 2: empty tag', broken(lambda d: d['stations'][0].update(tag='')) != [])
    check('rule 2: a raw radio-browser tag note', any('raw radio-browser tag list' in p for p in broken(
        lambda d: d['stations'][0].update(notes='tags: jazz,soul'))))
    check('rule 2: a formatted tag note passes', broken(lambda d: d['stations'][0].update(notes='Tags: jazz')) == [])
    check('rule 3: empty name', broken(lambda d: d['stations'][0].update(name='')) != [])
    check('rule 3: empty country', broken(lambda d: d['stations'][0].update(country='')) != [])
    check('rule 3: invalid URL', broken(lambda d: d['stations'][0].update(stream_url='ftp://x')) != [])
    check('rule 4: duplicate (name, country, stream_url)',
          any('duplicate' in p for p in broken(lambda d: d['stations'].append(d['stations'][0]))))
    check('rule 5: a missing row', any('missing' in p for p in broken(lambda d: d['stations'].pop())))
    check('rule 6: empty', any('outside' in p for p in validate_app_catalog(
        {'schema_version': 1, 'generated_utc': _UTC, 'stations': []}, [], langs)))
    many = [_row(name=f'Fixture {i}', stream_url=f'https://a.example.test/{i}') for i in range(MAX_ENTRIES + 1)]
    big, _ = build_app_catalog([(xa, many)], _UTC, langs)
    check('rule 6: more than 10,000', any('outside' in p for p in validate_app_catalog(big, many, langs)))
    for value in ('German,French', 'German, German', 'German, ', ' German', 'Deutsch', 'Instrumental',
                  'German,  French', 'German;French', ', German', 'Low  German'):
        check(f'rule 7: language {value!r} fails',
              any('language' in p for p in broken(lambda d: d['stations'][0].update(language=value))))
        check(f'rule 7: {value!r} fails the language rule itself', _language_problem(value, _mapped_keys(langs)))
    for value in ('', 'German', 'German, French', 'English, German, Low German', 'Klingon', 'Serbo-Croatian'):
        check(f'rule 7: language {value!r} passes',
              broken(lambda d: d['stations'][0].update(language=value)) == [])

    # ---------------------------------------------------------------- languages (D84, contracts §2.6)
    def lang(raw):
        return normalize_language(raw, langs)

    for raw, want in (
            ('American English,British English,Deutsch Fränkisch,English,German,Low German,Swiss German',
             'English, German, Low German'),                                          # the §2.6 examples
            ('Deutch,Gernan', 'German'), ('Instrumental', ''),
            ('German,French', 'German, French'), ('German;French', 'German, French'),  # split on , and ;
            ('German , French ;English', 'German, French, English'),
            ('Serbo-Croatian', 'Serbo-Croatian'),                                      # not on - / or .
            ('English/ French', 'English, French'), ('Français - Lëtzebuergesch', 'French, Luxembourgish'),
            ('GERMAN', 'German'), ('german', 'German'), ('  gErMaN  ', 'German'), ('low   GERMAN', 'Low German'),
            ('Deutsch', 'German'), ('deutch', 'German'), ('Ελληνικά', 'Greek'), ('ΕΛΛΗΝΙΚΆ', 'Greek'),
            ('Français', 'French'),                                                  # NFD input -> NFC key
            ('German,Deutsch,English,german', 'German, English'),                      # first seen, once
            ('German,,', 'German'), (',;, ;', ''), ('', ''), ('   ', ''), (None, ''),
            ('Instrumental,English', 'English'), ('Multilingual,Instrumental', ''),
            ('French,Français - Lëtzebuergesch,Luxembourgish', 'French, Luxembourgish')):
        got = lang(raw)
        check(f'normalize_language({raw!r}) -> {want!r}, no unknown', got == (want, []))
    check('unknown: kept in capwords form and reported as written (whitespace collapsed)',
          lang('hIGH   valyrian') == ('High Valyrian', ['hIGH valyrian']))
    check('unknown: a token with . stays one token', lang('Fixt. lang') == ('Fixt. Lang', ['Fixt. lang']))
    check('unknown: deduped by exported name, reported in the order met',
          lang('German,Klingon,klingon') == ('German, Klingon', ['Klingon', 'klingon']))
    check('unknown: a token with - or / is one token', lang('Fixt-Lang/Other') == ('Fixt-lang/other',
                                                                                  ['Fixt-Lang/Other']))
    check('LANGUAGE_SEPARATOR is exactly ", "', LANGUAGE_SEPARATOR == ', ')

    xl = {'code': 'XL', 'name': 'Languageland'}
    lang_rows = [_row(country='XL', name=f'Lang {i}', stream_url=f'https://l.example.test/{i}', language=raw)
                 for i, raw in enumerate(('Klingon', 'German,klingon', 'KLINGON,klingon', 'Vulcan', 'Deutch',
                                          'Instrumental', '', 'English;German'))]
    # a duplicate whose language differs: the dedupe never reads the language
    lang_rows.append(_row(country='XL', name='Lang 4', stream_url='https://l.example.test/4', language='French'))
    ldoc, lstats = build_app_catalog([(xl, lang_rows)], _UTC, langs)
    by_url = {e['stream_url'][-1]: e['language'] for e in ldoc['stations']}
    check('languages: the exported values', by_url == {'0': 'Klingon', '1': 'German, Klingon', '2': 'Klingon',
                                                       '3': 'Vulcan', '4': 'German', '5': '', '6': '',
                                                       '7': 'English, German'})
    check('languages: the dedupe ignores the language', lstats['duplicates_removed'] == 1)
    check('languages: stats count the distinct exported names', lstats['languages'] == 4)
    check('languages: unknown keys with first spelling and entry count (once per entry)',
          lstats['unknown_languages'] == {'klingon': ('Klingon', 3), 'vulcan': ('Vulcan', 1)})
    lang_expected = [r for r in lang_rows if r['language'] != 'French']
    check('languages: unknown tokens are not a validation failure', validate_app_catalog(ldoc, lang_expected,
                                                                                        langs) == [])
    check('languages: a table-free build still exports every row', build_app_catalog(
        [(xl, lang_rows)], _UTC, {})[1]['exported'] == 8)

    # the table (common.language_table): shape of the lookup
    check('language_table: canonical, alias, list alias and drop keys',
          langs['german'] == ('German',) and langs['low german'] == ('Low German',)
          and langs['deutsch'] == ('German',) and langs['français - lëtzebuergesch'] == ('French', 'Luxembourgish')
          and langs['instrumental'] == () and 'Instrumental' not in langs)
    check('language_table: aliases and drop may be missing or empty',
          language_table({'languages': ['German']}, 'f.yaml') == {'german': ('German',)}
          and language_table({'languages': ['German'], 'aliases': None, 'drop': []}, 'f.yaml')
          == {'german': ('German',)})
    check('language_key: NFC, lower case, whitespace runs to one space, trimmed',
          language_key(' Low \t GERMAN\n') == 'low german' and language_key('Français') == 'français')

    def table_error(doc):
        try:
            language_table(doc, 'languages-fixture.yaml')
        except ValueError as e:
            return str(e) if 'languages-fixture.yaml' in str(e) else ''
        return ''

    ok = {'languages': ['German', 'Low German']}
    for label, doc, want in (
            ('not a mapping', ['German'], 'mapping'),
            ('no languages', {'aliases': {}}, 'non-empty list'),
            ('empty languages', {'languages': []}, 'non-empty list'),
            ('languages not a list', {'languages': 'German'}, 'non-empty list'),
            ('unknown top-level key (a typo)', {**ok, 'alias': {}}, "'alias'"),
            ('aliases not a mapping', {**ok, 'aliases': ['deutsch']}, 'aliases must'),
            ('drop not a list', {**ok, 'drop': 'music'}, 'drop must'),
            ('a canonical name not a string', {'languages': ['German', 1]}, 'canonical name 1'),
            ('an empty canonical name', {'languages': ['German', '']}, "canonical name ''"),
            ('an untrimmed canonical name', {'languages': [' German']}, "' German'"),
            ('doubled spaces in a canonical name', {'languages': ['Low  German']}, "'Low  German'"),
            ('a , in a canonical name', {'languages': ['German, French']}, "'German, French'"),
            ('a ; in a canonical name', {'languages': ['German;French']}, "'German;French'"),
            ('a canonical name not in NFC', {'languages': ['Français']}, 'NFC'),
            ('two canonical names with one key', {'languages': ['German', 'GERMAN']}, 'same key'),
            ('an unquoted boolean alias key', {**ok, 'aliases': {False: 'German'}}, 'quote it'),
            ('an alias key not in key form', {**ok, 'aliases': {'Deutsch': 'German'}}, "write it as 'deutsch'"),
            ('an alias key with doubled spaces', {**ok, 'aliases': {'swiss  german': 'German'}},
             "write it as 'swiss german'"),
            ('an alias to an unlisted name', {**ok, 'aliases': {'deutsch': 'Germna'}}, "['Germna']"),
            ('an alias to a name in the wrong case', {**ok, 'aliases': {'deutsch': 'german'}}, "['german']"),
            ('an alias to an empty list', {**ok, 'aliases': {'deutsch': []}}, 'non-empty list'),
            ('an alias to nothing', {**ok, 'aliases': {'deutsch': None}}, 'non-empty list'),
            ('an alias list with an unlisted name', {**ok, 'aliases': {'x y': ['German', 'Nope']}}, "['Nope']"),
            ('an alias key that is a canonical key', {**ok, 'aliases': {'german': 'German'}}, 'matches itself'),
            ('a key both alias and drop', {**ok, 'aliases': {'music': 'German'}, 'drop': ['music']},
             'both an alias and a drop'),
            ('a drop key that is a canonical key', {**ok, 'drop': ['low german']}, 'key of a canonical name'),
            ('a drop key not a string', {**ok, 'drop': [True]}, 'quote it'),
            ('a drop key not in key form', {**ok, 'drop': ['Instrumental']}, "write it as 'instrumental'"),
            ('an empty drop key', {**ok, 'drop': ['']}, 'is empty'),
            ('a drop key listed twice', {**ok, 'drop': ['music', 'music']}, 'listed twice')):
        msg = table_error(doc)
        check(f'language_table: {label} is a hard error naming the file and the problem', want in msg)
    both = table_error({'languages': ['German'], 'aliases': {'Deutsch': 'German'}, 'drop': ['German']})
    check('language_table: every problem is reported at once', "'Deutsch'" in both and "'German'" in both)

    # writer
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / 'app-catalog.json'
        path.write_bytes(b'previous')
        write_app_catalog(clean, path)
        raw = path.read_bytes()
        text = raw.decode('utf-8')
        lines = text.split('\n')
        check('writer: no BOM, \\n line ends, final newline', not raw.startswith(b'\xef\xbb\xbf')
              and '\r' not in text and text.endswith(']}\n'))
        check('writer: header line', lines[0] == '{"schema_version":1,"generated_utc":"2026-01-02T03:04:05Z",'
                                                 '"stations":[')
        check('writer: one station per line', len(lines) == len(clean['stations']) + 3 and lines[-2] == ']}'
              and all(json.loads(line.rstrip(',')) == e for line, e in zip(lines[1:-2], clean['stations'])))
        check('writer: UTF-8, not ASCII-escaped', '"Éclair"' in text and '\\u' not in text)
        check('writer: output parses back to the document', json.loads(text) == clean)
        again = Path(tmp) / 'again.json'
        write_app_catalog(json.loads(text), again)
        check('writer: round-trips byte-for-byte', again.read_bytes() == raw)
        check('writer: no temp file left', sorted(p.name for p in Path(tmp).iterdir())
              == ['again.json', 'app-catalog.json'])
        check('writer: the file is world-readable (0644), not mkstemp\'s 0600', path.stat().st_mode & 0o777 == 0o644)

    for label in failures:
        print(f'FAIL {label}')
    print(f'app_catalog self-test: {checks - len(failures)}/{checks} checks passed')
    return 1 if failures else 0


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        sys.exit(self_test())
    print('usage: app_catalog.py --self-test   (the export itself runs from build_all.py)', file=sys.stderr)
    sys.exit(2)
