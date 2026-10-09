"""Reconstruct source text units for DWG replacement without crossing columns.

This script groups source records only; it never translates or rewrites a DWG.
Usage: python scripts/replacement_groups.py extracted.json units.json
"""

import argparse
import bisect
import json
import re
from pathlib import Path


NEW_ITEM = re.compile(r"^\s*(?:[（(]?\d+[.)、．）]?|[一二三四五六七八九十]+[、.．])\s*")
NUMBERED_ITEM = re.compile(r"^\s*(?:[（(]\d+[)）]|[1-9]\d*[.)、．）]|[一二三四五六七八九十]+[、.．])(?!\d)\s*")
TERMINAL = re.compile(r"[。．.;；!！?？:：]\s*$")
SUBHEADING = re.compile(r"[:：]\s*$")
CONTINUATION_START = re.compile(r"^(?:并|且|以及|同时|应|按|在|与|对|其|使|以|若|当|或|不应|不得|需)")


def _box(row):
    value = row.get('box')
    if not isinstance(value, list) or len(value) != 4 or value[2] <= value[0] or value[3] <= value[1]:
        return None
    return [float(x) for x in value]


def _text(row):
    return row.get('visibleText') or row.get('raw') or ''


def _same_region(a, b):
    return a.get('region') == b.get('region')


def _new_unit(members):
    boxes = [_box(m) for m in members]
    return dict(block=members[0].get('block'), region=members[0].get('region'),
                members=members, source=''.join(_text(m) for m in members),
                box=[min(b[0] for b in boxes), min(b[1] for b in boxes),
                     max(b[2] for b in boxes), max(b[3] for b in boxes)],
                height=max(float(m.get('height') or 1) for m in members))


def _vertical_barriers(geometry):
    by_block = {}
    for entity in geometry:
        if entity.get('visible') is False:
            continue
        shape = entity.get('shape') or entity
        if entity.get('type') == 'AcDbLine':
            segments = [(shape.get('a'), shape.get('b'))]
        elif entity.get('type') == 'AcDbPolyline':
            vertices = shape.get('vertices') or []
            segments = list(zip(vertices, vertices[1:] + vertices[:1] if shape.get('closed') else vertices[1:]))
        else:
            continue
        for a, b in segments:
            a = a.get('p') if isinstance(a, dict) else a
            b = b.get('p') if isinstance(b, dict) else b
            if not a or not b:
                continue
            dx, dy = abs(a[0] - b[0]), abs(a[1] - b[1])
            if dy > 0 and dx <= dy * 0.01:
                by_block.setdefault(entity.get('block'), []).append((0.5 * (a[0] + b[0]), min(a[1], b[1]), max(a[1], b[1])))
    return {block: sorted(lines) for block, lines in by_block.items()}


def _grid_between(left, right, barriers):
    a, b = _box(left), _box(right)
    if a[2] >= b[0]:
        return False
    lines = barriers.get(left.get('block'), [])
    start = bisect.bisect_right(lines, (a[2], float('inf'), float('inf')))
    cy = 0.25 * (a[1] + a[3] + b[1] + b[3])
    for index in range(start, len(lines)):
        x, y0, y1 = lines[index]
        if x >= b[0]:
            break
        if y0 <= cy <= y1:
            return True
    return False


def _can_join_inline(left, right, barriers):
    if not _same_region(left, right):
        return False
    if _grid_between(left, right, barriers):
        return False
    a, b = _box(left), _box(right)
    ha, hb = float(left.get('height') or 1), float(right.get('height') or 1)
    gap = b[0] - a[2]
    return (abs(a[1] - b[1]) <= 0.45 * max(ha, hb)
            and 0.7 <= ha / hb <= 1.4
            and -0.35 * max(ha, hb) <= gap <= 1.6 * max(ha, hb))


def _can_join_paragraph(parent, lower):
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
    if dy <= 1.7 * h:
        return True
    stripped = lower['source'].strip()
    if SUBHEADING.search(stripped) or (len(stripped) <= 8
                                      and not re.search(r"[，。；、,.!?！？]", stripped)
                                      and not CONTINUATION_START.match(stripped)):
        return False
    first = parent['first']
    indent = b[0] - first['box'][0]
    return bool(NUMBERED_ITEM.match(first['source']) and 0.5 * h <= indent <= 3 * h)


def build_units(rows, geometry=()):
    """Return ordered units; uncertain geometry and structured cells stay atomic."""
    barriers = _vertical_barriers(geometry)
    by_block = {}
    for row in rows:
        if _text(row).strip():
            by_block.setdefault(row.get('block'), []).append(row)

    result = []
    for block in sorted(by_block, key=str):
        block_rows = by_block[block]
        free = [r for r in block_rows if r.get('kind') in ('TEXT', 'MTEXT')
                and not r.get('generatedByDimension') and _box(r) is not None
                and abs(float(r.get('rotation') or 0)) < 0.2]
        atomic = [r for r in block_rows if r not in free]
        free.sort(key=lambda r: (-_box(r)[1], _box(r)[0]))
        bands = []
        for row in free:
            h = float(row.get('height') or 1)
            if bands and abs(_box(row)[1] - bands[-1]['baseline']) <= 0.45 * max(h, bands[-1]['height']):
                bands[-1]['rows'].append(row)
                bands[-1]['height'] = max(h, bands[-1]['height'])
            else:
                bands.append(dict(baseline=_box(row)[1], height=h, rows=[row]))

        # Sort each baseline by x before joining. A slightly higher right-hand
        # fragment must not be processed before its left-hand neighbor.
        lines = []
        for band in bands:
            for row in sorted(band['rows'], key=lambda r: _box(r)[0]):
                matches = [unit for unit in lines if _can_join_inline(unit['members'][-1], row, barriers)]
                if matches:
                    unit = min(matches, key=lambda x: _box(row)[0] - x['box'][2])
                    unit.update(_new_unit(unit['members'] + [row]))
                else:
                    lines.append(_new_unit([row]))

        # A continuation can join only an aligned line below it. This is
        # intentionally conservative: separate units are safer than crossing
        # columns, table cells, or numbered notes.
        paragraphs = []
        for line in sorted(lines, key=lambda x: (-x['box'][1], x['box'][0])):
            matches = [unit for unit in paragraphs if _can_join_paragraph(unit, line)]
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
                paragraphs.append({**line, 'first': line, 'last': line, 'nlines': 1})
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('extracted', type=Path)
    parser.add_argument('units', type=Path)
    args = parser.parse_args()
    source = json.loads(args.extracted.read_text(encoding='utf-8-sig'))
    units = build_units(source['rows'], source.get('geometry', []))
    result = dict(schemaVersion=1, sourceSha256=source.get('sourceSha256'),
                  cadInputSha256=source.get('cadInputSha256'), units=units)
    args.units.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'{len(source["rows"])} rows -> {len(units)} source units')


if __name__ == '__main__':
    main()
