#!/usr/bin/env python3
"""The app catalog: data/output/app-catalog.json, the station list behind the Add-station picker.

Contract: docs/catalog-contracts.md §2 (decisions D59, D69, D71). Pure export, validation and writer;
build_all.py calls build_app_catalog, validate_app_catalog and write_app_catalog with the same rows it
writes to data/canonical/, after the CSVs and before the XLSX. Station facts come only from those rows
(YAML + sources); nothing here names a station, a URL or a country.

Stdlib only, so the fixture checks run anywhere:
    data/.venv/bin/python data/build/app_catalog.py --self-test
"""
import json
import os
import re
import sys
import tempfile
import unicodedata
from pathlib import Path
from urllib.parse import urlsplit

from common import app_tag, city_aliases, norm, norm_city, row_score, url_norm

SCHEMA_VERSION = 1
MAX_ENTRIES = 10_000
MAX_URL_LENGTH = 2_048
KEYS = ('name', 'name_local', 'country', 'country_label', 'city', 'region', 'frequency_fm', 'type', 'genre',
        'language', 'internet_only', 'stream_url', 'codec', 'bitrate', 'votes', 'notes', 'logo', 'tag')

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


def _notes(value):
    """Trimmed notes without Wikipedia _emphasis_ markers; "" when nothing readable is left: a bare
    source label (optionally followed by punctuation only) or no letter or digit at all."""
    s = _EMPHASIS.sub(r'\1', _text(value))
    if _BARE_LABEL.fullmatch(s) or not any(c.isalnum() for c in s):
        return ''
    return s


def _entry(row, country_label):
    """One canonical row as an app-catalog station (keys in KEYS order, §2.1 normalization)."""
    s = {k: _text(row.get(k)) for k in ('name', 'name_local', 'country', 'city', 'region', 'frequency_fm',
                                         'type', 'genre', 'language', 'stream_url', 'codec', 'logo')}
    for key, marker in PLACEHOLDERS.items():
        if s[key] == marker:
            s[key] = ''
    bitrate, votes = _integer(row.get('bitrate')), _integer(row.get('votes'))
    return {
        'name': s['name'], 'name_local': s['name_local'], 'country': s['country'],
        'country_label': country_label, 'city': s['city'], 'region': s['region'],
        'frequency_fm': s['frequency_fm'], 'type': s['type'], 'genre': s['genre'], 'language': s['language'],
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


def build_app_catalog(sources: list[tuple[dict, list[dict]]], generated_utc: str) -> tuple[dict, dict]:
    """sources: (yaml cfg, canonical rows) per country and per collection, in build order.
    Returns (document, stats) with stats keys working, url_excluded, duplicates_removed, exported."""
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
    stations = sorted((_entry(row, label) for row, label in kept.values()), key=_order)
    doc = {'schema_version': SCHEMA_VERSION, 'generated_utc': generated_utc, 'stations': stations}
    return doc, {'working': working, 'url_excluded': url_excluded, 'duplicates_removed': duplicates,
                 'exported': len(stations)}


def _score(row):
    return row_score({**row, 'votes': _integer(row.get('votes')) or 0})


def _entry_problems(i, e):
    """Rules 2 and 3 of §2.3 for one entry, plus the §2.1 guarantees of its values."""
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
    if not e['tag']:
        out.append(f'{where}: empty tag')
    return out


def validate_app_catalog(doc: dict, source_rows: list[dict]) -> list[str]:
    """Every problem found (empty list = valid)."""
    problems = []
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
        found = _entry_problems(i, e)
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
# Inline fixtures only: fictional country codes, names and example.test URLs (no station facts).

def _row(**over):
    base = dict(country='XA', name='Fixture One', name_local='', city='Fixton', region='North',
                frequency_fm='', type='Music', genre='Pop', language='Fixtish', political_leaning='None',
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

    xa, xb, coll = {'code': 'XA', 'name': 'Fixtureland'}, {'code': 'XB', 'name': 'Otherland'}, \
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
    doc, stats = build_app_catalog(sources, '2026-01-02T03:04:05Z')
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
                                      '2026-01-02T03:04:05Z')
    _, no_block = build_app_catalog([({'code': 'XC', 'name': 'Aliasland'}, alias_rows)], '2026-01-02T03:04:05Z')
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
    check('notes: bare "tags:" -> "", _text_ -> text, tags kept', pad['notes'] == ''
          and num['notes'] == 'relays of Some Name here' and strs['notes'] == 'tags: jazz,soul')
    check('notes: a bare "curated:" label -> ""', by_name['Bare Label'][0]['notes'] == '')
    for note, want in (('tags:', ''), ('curated:', ''), (' curated:  ', ''), ('wiki:', ''), ('source:\t', ''),
                       ('curated+radio-browser:', ''), ('tags: ..', ''), ('curated: —', ''), ('tags: , ;', ''),
                       ('Πηγή:', ''), ('—', ''), (' .. ', ''), (None, ''), ('_curated_:', ''),
                       ('tags: 80s', 'tags: 80s'), ('curated: pinned stream', 'curated: pinned stream'),
                       ('Info: 24/7', 'Info: 24/7'), ('Radio in Fixton:', 'Radio in Fixton:'),
                       ('source: _Some Wiki_', 'source: Some Wiki'), ('ok', 'ok')):
        check(f'notes: {note!r} -> {want!r}', _notes(note) == want)
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
    problems = validate_app_catalog(doc, source_rows)
    check('rule 5 reports the deduped row', any('missing' in p and "'Fixture Two'" in p for p in problems)
          and any('stations exported' in p for p in problems))
    clean_rows = [r for r in source_rows if r['name'] != 'fixture  two']
    clean, _ = build_app_catalog([(xa, [r for r in xa_rows if r in clean_rows]), (xb, xb_rows), (coll, coll_rows)],
                                 '2026-01-02T03:04:05Z')
    check('a clean document validates', validate_app_catalog(clean, clean_rows) == [])

    def broken(mutate):
        d = json.loads(json.dumps(clean))
        mutate(d)
        return validate_app_catalog(d, clean_rows)

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
    check('rule 3: empty name', broken(lambda d: d['stations'][0].update(name='')) != [])
    check('rule 3: empty country', broken(lambda d: d['stations'][0].update(country='')) != [])
    check('rule 3: invalid URL', broken(lambda d: d['stations'][0].update(stream_url='ftp://x')) != [])
    check('rule 4: duplicate (name, country, stream_url)',
          any('duplicate' in p for p in broken(lambda d: d['stations'].append(d['stations'][0]))))
    check('rule 5: a missing row', any('missing' in p for p in broken(lambda d: d['stations'].pop())))
    check('rule 6: empty', any('outside' in p for p in validate_app_catalog(
        {'schema_version': 1, 'generated_utc': '2026-01-02T03:04:05Z', 'stations': []}, [])))
    many = [_row(name=f'Fixture {i}', stream_url=f'https://a.example.test/{i}') for i in range(MAX_ENTRIES + 1)]
    big, _ = build_app_catalog([(xa, many)], '2026-01-02T03:04:05Z')
    check('rule 6: more than 10,000', any('outside' in p for p in validate_app_catalog(big, many)))

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
