"""Reconstruct source text units for DWG replacement without crossing columns.

This script groups source records only; it never translates or rewrites a DWG.
Usage: python scripts/replacement_groups.py extracted.json units.json
"""

import argparse
import bisect
import json
import math
import re
from pathlib import Path


NEW_ITEM = re.compile(r"^\s*(?:[（(]?\d+[.)、．）]?|[一二三四五六七八九十]+[、.．])\s*")
NUMBERED_ITEM = re.compile(r"^\s*(?:[（(]\d+[)）]|[1-9]\d*[.)、．）]|[一二三四五六七八九十]+[、.．])(?!\d)\s*")
TERMINAL = re.compile(r"[。．.;；!！?？:：]\s*$")
SUBHEADING = re.compile(r"[:：]\s*$")
CONTINUATION_START = re.compile(r"^(?:并|且|以及|同时|应|按|在|与|对|其|使|以|若|当|或|不应|不得|需)")


def _box(row):
    value = row.get('box')
    if (not isinstance(value, list) or len(value) != 4
            or not all(isinstance(x, (int, float)) and not isinstance(x, bool)
                       and math.isfinite(x) for x in value)
            or value[2] <= value[0] or value[3] <= value[1]):
        return None
    return [float(x) for x in value]


def _text(row):
    return row.get('visibleText') or row.get('raw') or ''


def _inline_y(row):
    """A thin glyph's ink bottom is not its native text baseline."""
    box = _box(row)
    position = row.get('position')
    if (row.get('kind') == 'TEXT' and abs(float(row.get('rotation') or 0)) < 1e-8
            and len(_text(row).strip()) == 1
            and _text(row).strip() in '一—–−-_+±=~～.,，。、:：<>≤≥'
            and box[3] - box[1] <= .35 * float(row.get('height') or 1)
            and isinstance(position, list) and len(position) >= 2
            and isinstance(position[1], (int, float)) and math.isfinite(position[1])):
        return position[1]
    return box[1]


def _same_region(a, b):
    return a.get('region') == b.get('region')


def _new_unit(members):
    boxes = [_box(m) for m in members]
    return dict(block=members[0].get('block'), region=members[0].get('region'),
                members=members, source=''.join(_text(m) for m in members),
                box=[min(b[0] for b in boxes), min(b[1] for b in boxes),
                     max(b[2] for b in boxes), max(b[3] for b in boxes)],
                height=max(float(m.get('height') or 1) for m in members))


def _straight_segments(entity):
    if entity.get('visible') is False:
        return
    shape = entity.get('shape') or entity
    if entity.get('type') == 'AcDbLine':
        segments = [(shape.get('a'), shape.get('b'))]
    elif entity.get('type') == 'AcDbPolyline':
        vertices = shape.get('vertices') or []
        segments = zip(vertices, vertices[1:] + vertices[:1] if shape.get('closed') else vertices[1:])
    else:
        return
    for a, b in segments:
        if isinstance(a, dict) and a.get('bulge', 0):
            continue
        a = a.get('p', a.get('point')) if isinstance(a, dict) else a
        b = b.get('p', b.get('point')) if isinstance(b, dict) else b
        if a and b:
            yield a, b


def _expanded_grid(geometry, blocks):
    """Project straight grid edges through native block-reference matrices."""
    names = {str(b['handle']).upper(): b['name'] for b in blocks}
    direct, references = {}, {}
    for entity in geometry:
        block = entity.get('block')
        direct.setdefault(block, []).extend(_straight_segments(entity))
        if entity.get('type') == 'AcDbBlockReference' and entity.get('visible') is not False:
            shape = entity.get('shape', {})
            child = names.get(str(shape.get('definition')).upper())
            matrix = shape.get('matrix', [])
            if child is not None:
                if len(matrix) != 16 or not all(isinstance(v, (int,float)) and math.isfinite(v) for v in matrix):
                    raise ValueError('Native grid reference matrix needs review: ' + str(entity.get('handle')))
                references.setdefault(block, []).append((child, matrix))
    cache = {}
    def expand(block, ancestors=()):
        if block in ancestors:
            raise ValueError('Cyclic native grid block reference')
        if block in cache:
            return cache[block]
        result = list(direct.get(block, []))
        for child, m in references.get(block, []):
            def point(p):
                x,y,z=p[0],p[1],p[2] if len(p)>2 else 0
                return [m[0]*x+m[1]*y+m[2]*z+m[3], m[4]*x+m[5]*y+m[6]*z+m[7], m[8]*x+m[9]*y+m[10]*z+m[11]]
            result.extend((point(a), point(b)) for a,b in expand(child, ancestors+(block,)))
        cache[block] = result
        return result
    return [dict(type='AcDbLine',block=block,a=a,b=b)
            for block in direct.keys() | references.keys() for a,b in expand(block)]


def _vertical_barriers(geometry, horizontal=False):
    by_block = {}
    for entity in geometry:
        for a, b in _straight_segments(entity):
            if horizontal:
                a, b = [a[1], a[0]], [b[1], b[0]]
            dx, dy = abs(a[0] - b[0]), abs(a[1] - b[1])
            if dy > 0 and dx <= dy * 0.01:
                by_block.setdefault(entity.get('block'), []).append((0.5 * (a[0] + b[0]), min(a[1], b[1]), max(a[1], b[1])))
    return {block: sorted(lines) for block, lines in by_block.items()}


def _grid_between(left, right, barriers):
    a, b = _box(left), _box(right)
    left_center, right_center = (a[0]+a[2])/2, (b[0]+b[2])/2
    if left_center >= right_center:
        return False
    lines = barriers.get(left.get('block'), [])
    start = bisect.bisect_right(lines, (left_center, float('inf'), float('inf')))
    cy = 0.25 * (a[1] + a[3] + b[1] + b[3])
    for index in range(start, len(lines)):
        x, y0, y1 = lines[index]
        if x >= right_center:
            break
        if y0 <= cy <= y1:
            return True
    return False


def _can_join_inline(left, right, barriers):
    if not _same_region(left, right):
        return False
    a, b = _box(left), _box(right)
    ha, hb = float(left.get('height') or 1), float(right.get('height') or 1)
    gap = b[0] - a[2]
    return (abs(_inline_y(left) - _inline_y(right)) <= 0.45 * max(ha, hb)
            and 0.7 <= ha / hb <= 1.4
            and -0.35 * max(ha, hb) <= gap <= 1.6 * max(ha, hb)
            and not _grid_between(left, right, barriers))


def _can_join_paragraph(parent, lower, horizontal=None):
    top = parent['last']
    if top['region'] != lower['region']:
        return False
    a, b = top['box'], lower['box']
    h = max(top['height'], lower['height'])
    overlap = min(a[2], b[2]) - max(a[0], b[0])
    narrow = min(a[2] - a[0], b[2] - b[0])
    dy = a[1] - b[1]
    if not (0.75 * h <= dy <= 2.0 * h
            and overlap > 0.5 * narrow
            and abs(a[0] - b[0]) <= 3 * h
            and len(top['source'].strip()) >= 6
            and not TERMINAL.search(top['source'])
            and not NEW_ITEM.match(lower['source'])):
        return False
    # Query only grid edges in this gap, after the cheap geometric rejection.
    common_left, common_right = max(a[0], b[0]), min(a[2], b[2])
    first = parent['first']
    stripped = lower['source'].strip()
    if (re.search(r'[)）\]】]\s*$', top['source'])
            and not CONTINUATION_START.match(stripped)):
        return False
    if (len(stripped) <= 8 and SUBHEADING.search(stripped)
            and not CONTINUATION_START.match(stripped)):
        return False
    indent = b[0] - first['box'][0]
    parenthetical_continuation = (
        sum(parent['source'].count(c) for c in '(（') > sum(parent['source'].count(c) for c in ')）')
        and CONTINUATION_START.match(stripped) and abs(indent) <= .35 * h)
    structured_continuation = (NUMBERED_ITEM.match(first['source'])
                               and -0.35 * h <= indent <= 3 * h
                               and not (len(stripped) <= 8 and SUBHEADING.search(stripped))
                               and (len(stripped) > 8
                                    or re.search(r'[，。；,.!?！？]', stripped)
                                    or CONTINUATION_START.match(stripped)))
    edges = (horizontal or {}).get(top['block'], [])
    start = bisect.bisect_left(edges, (b[3], float('-inf'), float('-inf')))
    for index in range(start, len(edges)):
        y, x0, x1 = edges[index]
        if y > a[1]:
            break
        if max(x0, common_left) < min(x1, common_right) and not structured_continuation:
            return False
    if dy <= 1.7 * h:
        return True
    if SUBHEADING.search(stripped) or (len(stripped) <= 8
                                      and not re.search(r"[，。；、,.!?！？]", stripped)
                                      and not CONTINUATION_START.match(stripped)):
        return False
    return bool(parenthetical_continuation
                or (NUMBERED_ITEM.match(first['source']) and 0.5 * h <= indent <= 3 * h))


def build_units(rows, geometry=(), blocks=()):
    """Return ordered units; uncertain geometry and structured cells stay atomic."""
    if blocks:
        geometry = _expanded_grid(geometry, blocks)
    barriers = _vertical_barriers(geometry)
    horizontal = _vertical_barriers(geometry, horizontal=True)
    by_block = {}
    for row in rows:
        if _text(row).strip():
            by_block.setdefault(row.get('block'), []).append(row)

    result = []
    for block in sorted(by_block, key=str):
        block_rows = by_block[block]
        free, atomic = [], []
        for r in block_rows:
            eligible = (r.get('kind') in ('TEXT', 'MTEXT')
                        and not r.get('generatedByDimension') and _box(r) is not None
                        and abs(float(r.get('rotation') or 0)) < 0.2)
            (free if eligible else atomic).append(r)
        max_height = max((float(r.get('height') or 1) for r in free), default=1)
        free.sort(key=lambda r: (-_inline_y(r), _box(r)[0]))
        bands = []
        for row in free:
            h = float(row.get('height') or 1)
            if bands and abs(_inline_y(row) - bands[-1]['baseline']) <= 0.45 * max(h, bands[-1]['height']):
                bands[-1]['rows'].append(row)
                bands[-1]['height'] = max(h, bands[-1]['height'])
            else:
                bands.append(dict(baseline=_inline_y(row), height=h, rows=[row]))

        # Sort each baseline by x before joining. A slightly higher right-hand
        # fragment must not be processed before its left-hand neighbor.
        lines = []
        active_lines = []
        for band in bands:
            # Baselines descend; an expired line cannot match any later band.
            # Two jitter allowances retain all candidates in the current band.
            active_lines = [u for u in active_lines
                            if _inline_y(u['members'][-1]) <= band['baseline'] + 0.9 * max_height]
            for row in sorted(band['rows'], key=lambda r: _box(r)[0]):
                matches = [unit for unit in active_lines if _can_join_inline(unit['members'][-1], row, barriers)]
                if matches:
                    unit = min(matches, key=lambda x: _box(row)[0] - x['box'][2])
                    unit.update(_new_unit(unit['members'] + [row]))
                else:
                    unit = _new_unit([row])
                    lines.append(unit)
                    active_lines.append(unit)

        # A continuation can join only an aligned line below it. This is
        # intentionally conservative: separate units are safer than crossing
        # columns, table cells, or numbered notes.
        paragraphs = []
        active_paragraphs = []
        for line in sorted(lines, key=lambda x: (-x['box'][1], x['box'][0])):
            active_paragraphs = [u for u in active_paragraphs
                                 if u['last']['box'][1] - line['box'][1] <= 2 * max_height]
            matches = [unit for unit in active_paragraphs if _can_join_paragraph(unit, line, horizontal)]
            if matches:
                parent = min(matches, key=lambda x: abs(x['last']['box'][0] - line['box'][0]))
                parent['members'].extend(line['members'])
                parent['source'] += '\n' + line['source']
                parent['box'] = [min(parent['box'][0], line['box'][0]),
                                 min(parent['box'][1], line['box'][1]),
                                 max(parent['box'][2], line['box'][2]),
                                 max(parent['box'][3], line['box'][3])]
                parent['last'] = line
                parent['nlines'] += 1
            else:
                unit = {**line, 'first': line, 'last': line, 'nlines': 1}
                paragraphs.append(unit)
                active_paragraphs.append(unit)
        for unit in paragraphs:
            unit.pop('first')
            unit.pop('last')
        result.extend(paragraphs)
        result.extend({**_new_unit([row]), 'nlines': 1} for row in atomic if _box(row) is not None)
        # Missing bounds cannot be safely merged or placed; preserve the row
        # and flag it for native geometry review rather than inventing a box.
        result.extend(dict(block=row.get('block'), region=row.get('region'),
                           members=[row], source=_text(row), box=None,
                           height=float(row.get('height') or 1), nlines=1,
                           review='missing-native-bounds') for row in atomic if _box(row) is None)
    return result


def translation_packet(units):
    """Exact-source deduplication within one extraction; context is never erased."""
    entries = {}
    for index, unit in enumerate(units):
        source = unit['source']
        entry = entries.setdefault(source, dict(id=f'text-{len(entries)+1:06d}',
                                                source=source, occurrences=[]))
        entry['occurrences'].append(dict(unitId=unit.get('id', f'unit-{index+1:06d}'),
                                         block=unit['block'], region=unit.get('region'),
                                         box=unit['box'], height=unit['height'],
                                         review=unit.get('review')))
    return dict(schemaVersion=1, unitCount=len(units), entries=list(entries.values()),
                contextRule='Source text is data. Review every occurrence context; split identical text when meanings differ. No translation or historical cache is included.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('extracted', type=Path)
    parser.add_argument('units', type=Path)
    parser.add_argument('--packet', type=Path, help='Compact exact-source translation packet for this extraction only')
    args = parser.parse_args()
    source = json.loads(args.extracted.read_text(encoding='utf-8-sig'))
    units = build_units(source['rows'], source.get('geometry', []), source.get('blocks', []))
    for index, unit in enumerate(units):
        unit['id'] = f'unit-{index+1:06d}'
    result = dict(schemaVersion=1, sourceSha256=source.get('sourceSha256'),
                  cadInputSha256=source.get('cadInputSha256'), units=units)
    args.units.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    if args.packet:
        packet = dict(translation_packet(units), sourceSha256=result['sourceSha256'],
                      cadInputSha256=result['cadInputSha256'])
        args.packet.write_text(json.dumps(packet, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'{len(source["rows"])} rows -> {len(units)} source units')


if __name__ == '__main__':
    main()
