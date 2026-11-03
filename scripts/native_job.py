"""Shared contracts and saved-DWG gates. Translation and native CAD remain model-directed."""
import argparse
import collections
import hashlib
import json
import math
import re
import shutil
import struct
import subprocess
import time
from pathlib import Path


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def prepare(source, job, mode, source_language, target_language):
    source, job = Path(source).resolve(), Path(job).resolve()
    require(source.is_file() and source.suffix.lower() == '.dwg', 'A native DWG source is required')
    require(not job.exists(), 'Job directory already exists; new tests require a new job')
    require(mode in ('replace', 'bilingual'), 'Unknown native mode')
    require(source_language and target_language and source_language != target_language, 'Explicit language direction required')
    job.mkdir(parents=True)
    shutil.copy2(source, job / 'input.dwg')
    task = dict(schemaVersion=1, sourcePath=str(source), sourceSha256=digest(source),
                mode=mode, sourceLanguage=source_language, targetLanguage=target_language,
                startedAt=time.time(), status='prepared', nativeInputPath=str(job/'input.dwg'))
    write(job / 'task.json', task)
    return task


def key(row):
    return row['handle'].upper(), row['slot']


def row_index(snapshot):
    rows = snapshot['rows']
    result = {key(row): row for row in rows}
    require(len(result) == len(rows), 'Duplicate handle/slot in snapshot')
    return result


def shape_rectangle(box):
    require(isinstance(box, list) and len(box) == 4 and all(isinstance(x,(int,float)) and math.isfinite(x) for x in box), 'Invalid region')
    require(box[0] < box[2] and box[1] < box[3], 'Empty region')


def in_region(box, region, tolerance=1e-5):
    return box[0] >= region[0]-tolerance and box[1] >= region[1]-tolerance and box[2] <= region[2]+tolerance and box[3] <= region[3]+tolerance


def overlap(a, b):
    return len(a) == len(b) == 4 and min(a[2],b[2])-max(a[0],b[0]) > 1e-5 and min(a[3],b[3])-max(a[1],b[1]) > 1e-5


def same_native_font(a,b):
    if a.get('font')==b.get('font'):
        return True
    fields=('fontFace','fontBold','fontItalic')
    return bool(a.get('fontFace')) and all(a.get(f)==b.get(f) and f in a and f in b for f in fields) and all(not r.get('font') or Path(r['font']).suffix.lower() in ('.ttf','.ttc') for r in (a,b))


def check_projection(snapshot):
    require(snapshot.get('worldBoxCoverage')=='complete', 'Complete instance projection required')
    blocks={b['name']:b for b in snapshot.get('blocks',[])}
    for row in snapshot['rows']:
        views=row.get('worldBoxes')
        require(isinstance(views,list), 'Missing text instance projection')
        if not views:
            if not row.get('raw','').strip() or row.get('visible') is False:
                continue
            if row.get('kind')=='ATTDEF' and row.get('instanceDisplay')=='attribute-definition-suppressed':
                continue
            block=blocks.get(row['block'],{})
            require(block.get('layout') is False and block.get('visibleInstanceCount')==0 and row['block'] not in ('*Model_Space','*Paper_Space'), 'Visible text has empty instance projection')
        for view in views:
            require(view.get('root') and isinstance(view.get('instancePath'),list), 'Invalid instance projection')
            shape_rectangle(view['box'])


def check_coverage_resolutions(snapshot, mapping):
    issues = snapshot.get('coverageIssues', [])
    resolutions = mapping.get('scope', {}).get('coverageResolutions', [])
    issue_keys = [(issue.get('type'), issue.get('handle')) for issue in issues]
    resolution_keys = [(item.get('type'), item.get('handle')) for item in resolutions]
    require(len(set(issue_keys)) == len(issue_keys) and
            len(set(resolution_keys)) == len(resolution_keys) and
            set(issue_keys) == set(resolution_keys), 'Unresolved extraction coverageIssues')
    rows = {row['handle']: row for row in snapshot.get('rows', [])}
    geometry = {item['handle']: item for item in snapshot.get('geometry', [])}
    actions = {edit['handle']: edit for edit in mapping.get('edits', [])}
    for item in resolutions:
        handle, reason = item['handle'], item.get('reason')
        if item['type'] == 'missing-text-bounds':
            row, edit = rows.get(handle, {}), actions.get(handle, {})
            require(row and edit.get('action') == 'retain' and
                    row.get('raw') == edit.get('target') and
                    not re.search('[\u3400-\u9fff]', row['raw']) and
                    ((reason == 'nonprinting-marker' and row['raw'] == 'TH_SUPERPART' and
                      0 < row.get('height', 0) < 1e-20) or
                     (reason == 'unchanged-ascii-degenerate' and row['raw'] == 'I')),
                    'Unresolved text coverage issue: ' + handle)
        elif item['type'] == 'unreadable-custom-or-image':
            entity = geometry.get(handle, {})
            shape = entity.get('shape', {})
            known_geometry = (shape.get('OriginalClassName') == 'TH_ParaBasePntUA' or
                              (shape.get('OriginalClassName') == 'AmgStdPart' and
                               shape.get('OriginalDxfName') == 'STDPART2D' and
                               'AutoCAD Mechanical' in shape.get('ApplicationDescription', '')))
            require(reason == 'preserved-proxy-graphics' and
                    entity.get('type') == 'AcDbZombieEntity' and
                    known_geometry and
                    shape.get('HasFields') is False and
                    not any(shape.get('XData', [])),
                    'Unresolved proxy coverage issue: ' + handle)
        else:
            raise ValueError('Unresolved extraction coverage issue: ' + handle)


def verify_engineering_geometry(source, saved, mapping):
    added_text_per_block = collections.Counter(addition['block'] for addition in mapping.get('additions', []))
    for flow in mapping.get('reflows', []):
        added_text_per_block[flow['block']] += 1 - len(flow['sourceHandles'])
    original_blocks = {block['handle']: block for block in source.get('blocks', [])}
    saved_blocks = {block['handle']: block for block in saved.get('blocks', [])}
    require(len(original_blocks) == len(source.get('blocks', [])) and
            len(saved_blocks) == len(saved.get('blocks', [])) and
            original_blocks.keys() == saved_blocks.keys(), 'Saved engineering block definitions changed')
    original_names = {block['name']: handle for handle, block in original_blocks.items()}
    saved_names = {block['name']: handle for handle, block in saved_blocks.items()}
    require(len(original_names) == len(original_blocks) and
            len(saved_names) == len(saved_blocks), 'Duplicate engineering block names')

    def references(block, names):
        result = {}
        for ref in block.get('references', []):
            require(ref['handle'] not in result and ref['child'] in names,
                    'Invalid engineering block reference')
            result[ref['handle']] = {**{k: v for k, v in ref.items() if k != 'box'},
                                     'child': names[ref['child']]}
        return result

    for handle, old in original_blocks.items():
        new = saved_blocks[handle]
        if old['name'] != new['name']:
            old_family = re.fullmatch(r'\*(D|U|X)\d+', old['name'])
            new_family = re.fullmatch(r'\*(D|U|X)\d+', new['name'])
            require(old_family and new_family and old_family.group(1) == new_family.group(1),
                    'Saved engineering block name changed: ' + handle)
        for field in ('layout', 'origin'):
            if field in old or field in new:
                require(old.get(field) == new.get(field),
                        'Saved engineering block definition changed: ' + handle)
        require(new.get('entities') == old.get('entities') + added_text_per_block[old['name']],
                'Saved engineering block entity census changed: ' + handle)
        require(references(old, original_names) == references(new, saved_names),
                'Saved engineering block references changed: ' + handle)

    old_geometry = {g['handle']: g for g in source.get('geometry', [])}
    new_geometry = {g['handle']: g for g in saved.get('geometry', [])}
    require(len(old_geometry) == len(source.get('geometry', [])) and
            len(new_geometry) == len(saved.get('geometry', [])) and
            old_geometry.keys() == new_geometry.keys(), 'Saved engineering geometry census changed')

    def shape_equal(left, right, kind, path=()):
        if left == right:
            return True
        if isinstance(left, dict) and isinstance(right, dict):
            return left.keys() == right.keys() and all(
                shape_equal(left[k], right[k], kind, path + (k,)) for k in left)
        if isinstance(left, list) and isinstance(right, list):
            return len(left) == len(right) and all(
                shape_equal(a, b, kind, path + (i,))
                for i, (a, b) in enumerate(zip(left, right)))
        # Native save/reopen can round a double by a few representable values.
        # ULP bounds scale with floating-point precision, not drawing tolerance.
        if (type(left) in (int, float) and type(right) in (int, float) and
                (type(left) is float or type(right) is float) and
                math.isfinite(left) and math.isfinite(right) and
                abs(left-right) <= max(1e-12,4*max(math.ulp(left), math.ulp(right)))):
            return True
        if (kind == 'AcDbEllipse' and path and path[0] in {'StartPoint','EndPoint'} and
                type(left) in (int,float) and type(right) in (int,float) and
                math.isfinite(left) and math.isfinite(right)):
            # Endpoints are derived from center + axes; cancellation can exceed
            # an endpoint's own ULP without changing the defining ellipse.
            base = [abs(v) for k in ('Center','MajorAxis','MinorAxis')
                    for v in old_shape.get(k,[]) if type(v) in (int,float)]
            return abs(left-right) <= max(1e-12,4*math.ulp(max(base,default=0)))
        return False

    for handle, old in old_geometry.items():
        new = new_geometry[handle]
        old_owner, new_owner = old.get('block'), new.get('block')
        if old_owner is not None or new_owner is not None:
            require(old_owner in original_names and new_owner in saved_names and
                    original_names[old_owner] == saved_names[new_owner],
                    'Saved engineering geometry owner changed: ' + handle)
        require({k: v for k, v in old.items() if k not in ('block', 'shape')} ==
                {k: v for k, v in new.items() if k not in ('block', 'shape')},
                'Saved engineering geometry attributes changed: ' + handle)
        old_shape, new_shape = old['shape'], new['shape']
        if old.get('type') == 'AcDbZombieEntity' and old_shape != new_shape:
            owner = original_blocks[original_names[old_owner]]
            if (owner.get('visibleInstanceCount') == 0 and
                    old_shape.get('OriginalClassName') == 'TH_ParaBasePntUA' and
                    new_shape.get('OriginalClassName') == 'TH_ParaBasePntUA' and
                    old_shape.get('HasFields') is False and new_shape.get('HasFields') is False):
                old_shape = {k: v for k, v in old_shape.items() if k != 'GraphicsMetafileType'}
                new_shape = {k: v for k, v in new_shape.items() if k != 'GraphicsMetafileType'}
        require(shape_equal(old_shape, new_shape, old.get('type')),
                'Saved engineering geometry changed: ' + handle)

    def instances(snapshot, names):
        result = {}
        for instance in snapshot.get('instances', []):
            require(instance['path'] not in result and instance['block'] in names,
                    'Invalid engineering block instance')
            # Aggregate extents include translated glyphs, unlike the placement matrix.
            result[instance['path']] = {**{k: v for k, v in instance.items() if k != 'bounds'},
                                        'block': names[instance['block']]}
        require(len(result) == len(snapshot.get('instances', [])), 'Duplicate engineering block instance')
        return result

    require(instances(source, original_names) == instances(saved, saved_names),
            'Block instance transforms changed')


def protected_inline_y(row):
    box = row.get('box', [])
    position = row.get('position')
    text = (row.get('visibleText') or row.get('raw') or '').strip()
    if (row.get('kind') == 'TEXT' and abs(float(row.get('rotation') or 0)) < 1e-8
            and len(text) == 1
            and text in '一—–−-_+±=~～.,，。、:：<>≤≥' and len(box) == 4
            and box[3] - box[1] <= .35 * (row.get('height') or 1)
            and isinstance(position, list) and len(position) >= 2
            and isinstance(position[1], (int, float)) and math.isfinite(position[1])):
        return position[1]
    return box[1]


def protected_source_text(rows):
    parts = []
    previous = None
    for row in rows:
        separator = '\n'
        if previous:
            a, b = previous.get('box', []), row.get('box', [])
            h = max(previous.get('height') or 1, row.get('height') or 1)
            if (a and b and len(a) == len(b) == 4
                    and previous.get('block') == row.get('block')
                    and abs(protected_inline_y(previous) - protected_inline_y(row)) <= .45 * h
                    and -.35 * h <= b[0] - a[2] <= 1.6 * h):
                separator = ''
        if parts:
            parts.append(separator)
        parts.append(row.get('visibleText', row['raw']))
        previous = row
    return ''.join(parts)


def check_protected_values(source_text, target_text, values):
    """Check explicitly declared literals; semantic equivalence remains reviewed."""
    require(isinstance(values, list), 'Protected values must be a list')
    def visible(text):
        text = re.sub(r'\\U\+([0-9a-fA-F]{4})', lambda m: chr(int(m[1], 16)), text)
        return re.sub(r'\\[ACFHQTWacfqtwp][^;]*;', '', text).replace(r'\P', '\n').replace(r'\~', ' ')
    def present(text, token):
        left = r'(?<![A-Za-z0-9_.])' if token[0].isascii() and token[0].isalnum() else ''
        right = r'(?![A-Za-z0-9_]|\.\d)' if token[-1].isascii() and token[-1].isalnum() else ''
        return re.search(left + re.escape(token) + right, visible(text)) is not None
    for value in values:
        if isinstance(value, str):
            original = target = value
        else:
            require(isinstance(value, dict), 'Protected value declaration is invalid')
            original, target = value.get('source'), value.get('target')
        require(isinstance(original, str) and original and isinstance(target, str) and target,
                'Protected value source/target required')
        require(present(source_text, original) and present(target_text, target),
                'Protected value missing or changed: ' + original)


def circled_identifiers(source):
    """Recognize short engineering IDs actually enclosed by a local native circle."""
    circles, labels = collections.defaultdict(list), collections.defaultdict(list)
    for entity in source.get('geometry', []):
        if (entity.get('type') or entity.get('kind')) not in ('AcDbCircle', 'Circle'):
            continue
        shape = entity.get('shape') or entity.get('parameters') or {}
        if entity.get('visible') is False or shape.get('Visible', shape.get('visible')) in (False, 'False'):
            continue
        center, radius = shape.get('center', shape.get('Center')), shape.get('radius', shape.get('Radius'))
        normal = shape.get('normal', shape.get('Normal', [0, 0, 1]))
        if (not isinstance(center, list) or len(center) < 2
                or not all(type(v) in (int, float) and math.isfinite(v) for v in center[:2])
                or type(radius) not in (int, float) or not math.isfinite(radius) or radius <= 0
                or not isinstance(normal, list) or len(normal) < 3 or abs(abs(normal[2]) - 1) > 1e-8):
            continue
        circles[entity.get('block')].append((center[0]-radius, center[0]+radius, center[0], center[1], radius))
    if not circles:
        return {}
    for row in source['rows']:
        text, box, height = row.get('raw', '').strip(), row.get('box'), row.get('height')
        if (row.get('block') in circles and row.get('kind') == 'TEXT' and row.get('visible') is not False
                and re.fullmatch(r'(?:[A-Za-z]{0,6}\d{1,5}[A-Za-z]?|[A-Za-z](?:[-/][A-Za-z0-9]+)+)', text)
                and isinstance(box, list) and len(box) == 4
                and all(type(v) in (int, float) and math.isfinite(v) for v in box)
                and box[2] > box[0] and box[3] > box[1]
                and type(height) in (int, float) and math.isfinite(height) and height > 0):
            labels[row.get('block')].append(row)
    result = {}
    for block, rows in labels.items():
        ordered, active, index = sorted(circles.get(block, [])), [], 0
        for row in sorted(rows, key=lambda r: r['box'][0]):
            box = row['box']
            while index < len(ordered) and ordered[index][0] <= box[0]:
                active.append(ordered[index]);index += 1
            active = [c for c in active if c[1] >= box[0]]
            matches = [c for c in active if c[1] >= box[2] and c[4] <= 3 * row['height']
                       and all((x-c[2])**2 + (y-c[3])**2 <= c[4]**2
                               for x in (box[0], box[2]) for y in (box[1], box[3]))]
            if matches:
                circle = min(matches, key=lambda c: c[4])
                result[key(row)] = (circle[2], circle[3], circle[4])
    return result


def source_note_x_limits(rows):
    """Keep long horizontal multiline notes in their source column footprint."""
    if len(re.sub(r'\s+', '', protected_source_text(rows))) < 40:
        return None
    boxes, baselines, heights = [], [], []
    multiline = False
    for row in rows:
        box = row.get('box')
        height = row.get('height')
        if (row.get('kind') not in ('TEXT', 'MTEXT') or abs(float(row.get('rotation') or 0)) > 1e-8
                or not isinstance(box, list) or len(box) != 4
                or not all(type(x) in (int, float) and math.isfinite(x) for x in box)
                or type(height) not in (int, float) or not math.isfinite(height) or height <= 0
                or box[2] <= box[0] or box[3] <= box[1]):
            return None
        position = row.get('position')
        native_position = (isinstance(position, list) and len(position) >= 2
                           and all(type(v) in (int, float) and math.isfinite(v) for v in position[:2]))
        left = box[0]
        if native_position and abs(position[0] - left) <= .5 * height:
            left = min(left, position[0])
        boxes.append((left, box[2]))
        baselines.append(position[1] if native_position and row['kind'] == 'TEXT' else box[1])
        heights.append(height)
        text = row.get('visibleText') or row.get('raw') or ''
        multiline |= row['kind'] == 'MTEXT' and ('\n' in text or r'\P' in text or box[3] - box[1] > 1.8 * height)
    multiline |= max(baselines) - min(baselines) >= .75 * min(heights)
    return (min(b[0] for b in boxes), max(b[1] for b in boxes)) if multiline else None


def validate_mapping(source, mapping):
    for item in (source, mapping):
        require(item.get('schemaVersion') == 1 and item.get('tableIndexBase') == 0, 'Native schema 1 and zero-based indices required')
    for name in ('sourceSha256', 'cadInputSha256'):
        require(source.get(name) == mapping.get(name) and re.fullmatch('[0-9a-fA-F]{64}', source.get(name,'')), 'Source/input binding mismatch')
    mode = mapping.get('mode')
    require(mode in ('replace', 'bilingual'), 'Explicit native mode required')
    require(mapping.get('sourceLanguage') and mapping.get('targetLanguage') and mapping['sourceLanguage'] != mapping['targetLanguage'], 'Explicit languages required')
    src = row_index(source)
    keys_by_handle = collections.defaultdict(list)
    for source_key in src:
        keys_by_handle[source_key[0]].append(source_key)
    if mapping.get('mode')=='bilingual':
        check_projection(source)
    edits = mapping.get('edits', [])
    actions = {key(e):e for e in edits}
    require(len(actions) == len(edits) and set(actions) == set(src), 'Incomplete or duplicate source action coverage')
    check_coverage_resolutions(source, mapping)
    flows = {f['group']:f for f in mapping.get('reflows', [])}
    require(len(flows) == len(mapping.get('reflows', [])), 'Duplicate reflow group')
    for marker_key, (x, y, radius) in circled_identifiers(source).items():
        edit, original = actions[marker_key], src[marker_key]
        if edit['action'] == 'reflow-member':
            flow = flows.get(edit.get('reflowGroup'), {})
            require(flow.get('sourceHandles') == [original['handle']]
                    and flow.get('target', '').strip() == original['raw'].strip(),
                    'Circled identifier must remain a separate anchored label: ' + original['handle'])
        else:
            require(edit['action'] == 'retain' or edit['action'] == 'replace' and edit.get('target') == original['raw'],
                    'Circled identifier content changed: ' + original['handle'])
    additions = mapping.get('additions', [])
    require(mode == 'bilingual' or not additions, 'Replacement does not accept bilingual additions')
    require(mode == 'replace' or not flows, 'Bilingual must retain all original entities')
    owners = {}
    for flow in flows.values():
        require(flow.get('target','').strip() and not flow.get('verificationIssues'), 'Unresolved or empty reflow')
        require(flow.get('sourceHandles'), 'Reflow has no sources')
        for handle in flow['sourceHandles']:
            matches = keys_by_handle.get(handle.upper(), [])
            require(len(matches) == 1, 'Reflow must use unambiguous text handles')
            require(matches[0] not in owners, 'Duplicate reflow source')
            require(src[matches[0]]['block'] == flow['block'], 'Reflow crosses owning blocks')
            owners[matches[0]] = flow['group']
        members = [src[keys_by_handle[h.upper()][0]] for h in flow['sourceHandles']]
        check_protected_values(protected_source_text(members),
                               flow['target'], flow.get('protectedValues', []))
        limits = source_note_x_limits(members)
        if limits:
            width = flow.get('layoutPlan', {}).get('width')
            require(type(width) in (int, float) and math.isfinite(width) and 0 < width <= limits[1] - limits[0] + 1e-6,
                    'Reflow exceeds source note width; wrap within the original column: ' + flow['group'])
    added_sources, ids = {}, set()
    for addition in additions:
        aid = addition['id']
        require(aid not in ids, 'duplicate addition id')
        ids.add(aid)
        require(addition.get('target','').strip() and not addition.get('verificationIssues'), 'Empty or unresolved addition')
        require(addition.get('sourceKeys'), 'Addition has no source counterpart')
        for source_key in addition['sourceKeys']:
            k = (source_key[0].upper(), source_key[1])
            require(k in src, 'Unknown addition source counterpart')
            require(k not in added_sources, 'duplicate source counterpart addition')
            require(src[k]['block'] == addition['block'], 'Addition crosses source block')
            added_sources[k] = aid
        check_protected_values(protected_source_text([src[(k[0].upper(), k[1])]
                                                      for k in addition['sourceKeys']]),
                               addition['target'], addition.get('protectedValues', []))
        require(any(src[k]['handle'] == addition['properties'] for k in src if src[k]['block'] == addition['block']), 'Unknown inherited-properties entity')
        plan = addition['placement']
        shape_rectangle(plan['region'])
        position = plan['position']
        require(len(position)==3 and all(math.isfinite(v) for v in position), 'Invalid addition position')
        require(plan['region'][0] <= position[0] <= plan['region'][2] and plan['region'][1] <= position[1] <= plan['region'][3], 'Addition anchor outside region')
        require(plan['width'] > 0 and plan['height'] >= plan['minReadableHeight'] > 0 and math.isfinite(plan.get('rotation',0)), 'Unreadable addition layout')
    counts = collections.Counter()
    for k, edit in actions.items():
        row = src[k]
        require(edit['kind'] == row['kind'] and edit['expectedRaw'] == row['raw'], 'Source kind/raw mismatch')
        require(not edit.get('verificationIssues'), 'Unresolved edit verificationIssues')
        action = edit['action']
        counts[action] += 1
        if row['kind'] == 'TABLE':
            indices = [int(i) for i in row['slot'].split(',')]
            require(len(indices) in (2,3) and all(i>=0 for i in indices), 'Invalid table slot')
            require([edit.get('row'),edit.get('column')] == indices[:2], 'Table row/column mismatch')
            if len(indices)==3:
                require(edit.get('contentIndex')==indices[2], 'Table contentIndex mismatch')
        if mode == 'bilingual':
            require(action == 'retain' and edit.get('target',row['raw']) == row['raw'], 'Bilingual must retain original text')
            required = edit.get('needsTranslation')
            require(isinstance(required,bool), 'Explicit needsTranslation classification required')
            # Obvious Chinese prose must not disappear behind an already-target label.
            if mapping['sourceLanguage'].lower().startswith('zh') and re.search('[\u3400-\u9fff]', row.get('visibleText',row['raw'])):
                require(required or edit.get('retainReason') == 'engineering-token' and edit.get('retainExplanation'), 'Untranslated source needs counterpart')
            if required and k not in added_sources:
                if edit.get('retainReason')=='existing-bilingual-geometry':
                    geometry={g['handle']:g for g in source.get('geometry',[])}
                    refs=edit.get('counterpartGeometryKeys',[])
                    require(refs and edit.get('counterpartComplete') is True and edit.get('counterpartMeaning','').strip(), 'Missing outline counterpart evidence')
                    require(all(ref in geometry and geometry[ref]['block']==row['block'] for ref in refs), 'Invalid outline counterpart geometry')
                    require(edit.get('visualCounterpartReviewed') is True and edit.get('inlineTarget','').strip(), 'Outline counterpart requires semantic and visual review')
                    continue
                if edit.get('retainReason')=='existing-bilingual-inline':
                    visible=row.get('visibleText',row['raw'])
                    a,b=edit.get('inlineSource',''),edit.get('inlineTarget','')
                    require(a and b and a!=b and a in visible and b in visible and edit.get('counterpartComplete') is True and edit.get('counterpartMeaning','').strip(), 'Incomplete inline counterpart evidence')
                    if mapping['sourceLanguage'].lower().startswith('zh') and not mapping['targetLanguage'].lower().startswith('zh'):
                        require(re.search('[\u3400-\u9fff]',a) and not re.search('[\u3400-\u9fff]',b), 'Inline counterpart has wrong language')
                    continue
                require(edit.get('retainReason') == 'existing-bilingual', 'Missing source counterpart')
                refs = edit.get('counterpartKeys',[])
                require(refs and edit.get('counterpartComplete') is True and edit.get('counterpartMeaning','').strip(), 'Incomplete counterpart evidence')
                for ref in refs:
                    ref_key = ref[0].upper(), ref[1]
                    require(ref_key in src and src[ref_key]['raw'].strip() and ref_key != k, 'Invalid existing counterpart')
                    visible=src[ref_key].get('visibleText',src[ref_key]['raw'])
                    if mapping['sourceLanguage'].lower().startswith('zh') and not mapping['targetLanguage'].lower().startswith('zh'):
                        require(not re.search('[\u3400-\u9fff]',visible), 'Existing counterpart has wrong language')
                    if mapping['targetLanguage'].lower().startswith('zh'):
                        require(re.search('[\u3400-\u9fff]',visible), 'Existing counterpart has wrong language')
        elif action == 'replace':
            require(edit.get('target','').strip(), 'Empty replacement target')
            check_protected_values(row['raw'], edit['target'], edit.get('protectedValues', []))
        elif action == 'retain':
            require(edit.get('retainReason'), 'Retain reason required')
        elif action == 'reflow-member':
            require(owners.get(k) == edit.get('reflowGroup'), 'Missing reflow source coverage')
        else:
            raise ValueError('Unsupported native action')
    require(all(actions[k]['action']=='reflow-member' for k in owners), 'Reflow source not owned')
    return dict(counts, additions=len(additions), reflows=len(flows))


def check_reflow_text_overlap(rows, exclusive):
    if not exclusive:
        return
    blocks = collections.defaultdict(list)
    for row in rows:
        box = row.get('inkBox') or row.get('box') or []
        if (not row.get('raw','').strip() or row.get('visible') is False
                or row.get('instanceDisplay') == 'attribute-definition-suppressed'
                or len(box) != 4 or not all(isinstance(v,(int,float)) and math.isfinite(v) for v in box)
                or box[0] >= box[2] or box[1] >= box[3]):
            continue
        blocks[row['block']].append((row,box))
    for items in blocks.values():
        active = []
        for row,box in sorted(items, key=lambda item:item[1][0]):
            active = [(other,b) for other,b in active if b[2] > box[0]+1e-5]
            for other,other_box in active:
                if key(row) in exclusive or key(other) in exclusive:
                    require(not overlap(box,other_box),
                            'Saved reflow text overlap: '+row['handle']+' / '+other['handle'])
            active.append((row,box))


def verify_content(source, mapping, saved, receipt):
    validate_mapping(source, mapping)
    src, dst = row_index(source), row_index(saved)
    edits_by_key = {key(edit): edit for edit in mapping['edits']}
    source_by_handle = {row['handle'].upper(): row for row in source['rows']}
    source_block_ids = {b['name']: b['handle'] for b in source.get('blocks', [])}
    saved_block_ids = {b['name']: b['handle'] for b in saved.get('blocks', [])}
    # Check original source preservation before target receipts to give a useful failure.
    for edit in mapping['edits']:
        k = key(edit)
        if edit['action']=='retain':
            require(k in dst and dst[k]['raw'] == src[k]['raw'], 'Saved original text changed')
            if mapping['mode']=='bilingual':
                fields=['block','layer','color','style','font','bigFont','position','height','width','rotation']
                if src[k]['kind'] in ('TEXT','ATTRIB','ATTDEF'):fields+=['alignmentPoint','widthFactor']
                for field in fields:
                    require(field in src[k] and field in dst[k] and src[k][field] is not None and dst[k][field] is not None, 'Missing original appearance evidence: '+field)
                    same = (same_native_font(src[k],dst[k]) if field=='font' else
                            source_block_ids.get(src[k][field]) == saved_block_ids.get(dst[k][field]) if field=='block' else
                            src[k].get(field)==dst[k].get(field))
                    require(same, 'Saved original appearance changed: '+field)
        elif edit['action']=='replace':
            require(k in dst and dst[k]['raw']==edit['target'], 'Saved target content mismatch')
        elif edit['action']=='reflow-member':
            require(k not in dst, 'Unremoved reflow source')
    check_coverage_resolutions(saved, mapping)
    if mapping['mode']=='bilingual':
        check_projection(saved)
        styles={s['name']:s for s in saved.get('styles',[])}
        for style in source.get('styles',[]):
            other=styles.get(style['name'],{})
            require(same_native_font(style,other) and {k:v for k,v in style.items() if k!='font'}=={k:v for k,v in other.items() if k!='font'}, 'Original shared style changed')
    verify_engineering_geometry(source, saved, mapping)
    expected = {a['id']:a for a in mapping.get('additions',[])}
    actual = {a['id']:a for a in receipt.get('additions',[])}
    require(set(actual)==set(expected) and len(actual)==len(receipt.get('additions',[])), 'Missing/duplicate saved addition receipts')
    generated = set()
    for aid, addition in expected.items():
        result = actual[aid]
        k = result['handle'].upper(), result.get('slot','text')
        require(k in dst and k not in src and k not in generated, 'Invalid saved addition target')
        generated.add(k)
        row=dst[k]
        require(row['raw']==addition['target'] and row['block']==addition['block'], 'Saved addition content/owner mismatch')
        require(row['height'] >= addition['placement']['minReadableHeight'], 'Unreadable saved addition')
        box=row.get('box',[])
        require(len(box)==4 and in_region(box,addition['placement']['region']), 'Saved addition outside region')
        for other in dst.values():
            if not other.get('raw','').strip() or other.get('visible') is False or other.get('instanceDisplay')=='attribute-definition-suppressed':
                continue
            if key(other)!=k and other['block']==row['block']:
                require(not overlap(box,other.get('box',[])), 'Saved bilingual text overlap: '+aid+' / '+other['handle'])
            if key(other)!=k:
                for target_view in row['worldBoxes']:
                    shape_rectangle(target_view['box'])
                    for other_view in other['worldBoxes']:
                        shape_rectangle(other_view['box'])
                        if target_view['root']==other_view['root']:
                            require(not overlap(target_view['box'],other_view['box']), 'Saved instance text overlap: '+aid+' / '+other['handle'])
    expected_flows={f['group']:f for f in mapping.get('reflows',[])}
    actual_flows={f['group']:f for f in receipt.get('reflows',[])}
    require(set(actual_flows)==set(expected_flows) and len(actual_flows)==len(receipt.get('reflows',[])), 'Missing/duplicate reflow receipts')
    exclusive = set()
    for gid, flow in expected_flows.items():
        result=actual_flows[gid];k=result['handle'].upper(),'text'
        require(k in dst and k not in src and k not in generated, 'Invalid saved reflow target')
        generated.add(k)
        require(dst[k]['raw']==flow['target'] and dst[k]['block']==flow['block'], 'Saved reflow target mismatch')
        plan = flow.get('layoutPlan', {})
        region = plan.get('region')
        shape_rectangle(region)
        minimum = flow.get('minReadableHeight', plan.get('minReadableHeight'))
        require(isinstance(minimum,(int,float)) and not isinstance(minimum,bool)
                and math.isfinite(minimum) and minimum > 0, 'Missing saved reflow readability minimum')
        height = dst[k].get('height')
        require(isinstance(height,(int,float)) and math.isfinite(height) and height >= minimum,
                'Unreadable saved reflow')
        shape_rectangle(dst[k].get('box'))
        require(in_region(dst[k]['box'], region), 'Saved reflow outside region: ' + gid)
        if dst[k].get('inkBox'):
            shape_rectangle(dst[k]['inkBox'])
            require(in_region(dst[k]['inkBox'],region), 'Saved reflow ink outside region: '+gid)
        limits = source_note_x_limits([source_by_handle[h.upper()] for h in flow['sourceHandles']])
        if limits:
            ink = dst[k].get('inkBox') or dst[k]['box']
            require(ink[0] >= limits[0] - 1e-6 and ink[2] <= limits[1] + 1e-6,
                    'Saved reflow exceeds source note width: ' + gid)
        if plan.get('checkTextOverlap') is True:
            exclusive.add(k)
    require(set(dst)=={key(e) for e in mapping['edits'] if e['action']!='reflow-member'}|generated, 'Unexplained saved text census change')
    check_reflow_text_overlap(saved['rows'], exclusive)
    for marker_key, (x, y, radius) in circled_identifiers(source).items():
        edit = edits_by_key[marker_key]
        target_key = ((actual_flows[edit['reflowGroup']]['handle'].upper(), 'text')
                      if edit['action'] == 'reflow-member' else marker_key)
        row = dst[target_key]
        box = row.get('inkBox') or row.get('box')
        shape_rectangle(box)
        require(all((px-x)**2 + (py-y)**2 <= radius**2 + 1e-6
                    for px in (box[0], box[2]) for py in (box[1], box[3])),
                'Saved circled identifier moved outside its original marker: ' + src[marker_key]['handle'])
    return dict(status='content-verified',sourceRecords=len(src),savedRecords=len(dst),additions=len(expected))


def check_visual_review(job, task, candidate_hash, expected_sheet_ids):
    review=read(job/'visual-review.json')
    require(review.get('sourceSha256')==task['sourceSha256'] and review.get('candidateSha256')==candidate_hash, 'Visual review binding mismatch')
    require(review.get('issues')==[] and review.get('translationReviewed') is True and review.get('symbolsReviewed') is True, 'Visual/semantic review unresolved')
    sheets=review.get('sheets',[])
    require(sheets and len({s['id'] for s in sheets})==len(sheets), 'Actual sheet inventory required')
    require(set(expected_sheet_ids)=={s['id'] for s in sheets}, 'Visual review omitted actual sheets')
    for sheet in sheets:
        require(sheet.get('reviewed') is True and sheet.get('denseRegions'), 'Each sheet and dense region must be reviewed')
        for evidence in [sheet]+sheet['denseRegions']:
            for role in ('source','candidate'):
                img=(job/evidence[role+'Image']).resolve()
                require(img.is_relative_to(job) and img.is_file(), 'Missing native visual image')
                data=img.read_bytes()
                require(data[:8]==b'\x89PNG\r\n\x1a\n' and len(data)>512, 'Invalid visual PNG evidence')
                receipt=read(img.with_suffix(img.suffix+'.render.json'))
                wanted=task['sourceSha256'] if role=='source' else candidate_hash
                require(receipt.get('drawingSha256')==wanted and receipt.get('pngSha256')==digest(img) and receipt.get('native') is True, 'Native visual evidence binding mismatch')
    return review


def verify(job):
    job=Path(job).resolve();task=read(job/'task.json')
    source=read(job/'extracted.json');mapping=read(job/'mapping.json');saved=read(job/'saved'/'extracted.json');receipt=read(job/'write-status.json')
    candidate=job/'candidate.dwg';candidate_hash=digest(candidate)
    require(digest(task['sourcePath'])==task['sourceSha256']==source['sourceSha256'], 'Original source changed')
    require(mapping['mode']==task['mode'] and mapping['sourceLanguage']==task['sourceLanguage'] and mapping['targetLanguage']==task['targetLanguage'], 'Task mode/direction changed')
    require(receipt.get('schemaVersion')==1 and receipt.get('errors')==[] and receipt.get('status')=='written-awaiting-verification' and receipt.get('candidateSha256')==candidate_hash, 'Native write status mismatch')
    require(saved.get('openedCandidateSha256')==candidate_hash, 'Fresh saved candidate extraction required')
    require(Path(saved['engine']['inputPath']).resolve()==job/'saved'/'input.dwg' and digest(job/'saved'/'input.dwg')==saved['cadInputSha256'], 'Verification must open the actual separate copy')
    result=verify_content(source,mapping,saved,receipt)
    sheet_ids=mapping.get('scope',{}).get('sheetIds',[])
    require(sheet_ids and len(set(sheet_ids))==len(sheet_ids), 'Explicit actual sheet inventory required')
    review=check_visual_review(job,task,candidate_hash,sheet_ids)
    result.update(status='verified',mode=task['mode'],sourceSha256=task['sourceSha256'],candidateSha256=candidate_hash,
                  reviewedSheetIds=[s['id'] for s in review['sheets']],warnings=review.get('warnings',[]),
                  mappingSha256=digest(job/'mapping.json'),savedSnapshotSha256=digest(job/'saved'/'extracted.json'),
                  visualReviewSha256=digest(job/'visual-review.json'),writeStatusSha256=digest(job/'write-status.json'))
    write(job/'verification.json',result)
    return result


def deliver(job, output):
    job, output=Path(job).resolve(),Path(output).resolve()
    result=read(job/'verification.json')
    require(result.get('status')=='verified', 'Native verified evidence required')
    task=read(job/'task.json');candidate=job/'candidate.dwg'
    require(digest(candidate)==result['candidateSha256'] and digest(task['sourcePath'])==result['sourceSha256'], 'Delivery binding changed')
    for field,path in [('mappingSha256',job/'mapping.json'),('savedSnapshotSha256',job/'saved'/'extracted.json'),('visualReviewSha256',job/'visual-review.json'),('writeStatusSha256',job/'write-status.json')]:
        require(result.get(field)==digest(path), 'Delivery evidence changed: '+field)
    output.mkdir(parents=True,exist_ok=True)
    suffix=re.sub('[^A-Za-z0-9-]','',task['targetLanguage']).upper()
    stem=Path(task['sourcePath']).stem+'-'+suffix+('-BILINGUAL' if task['mode']=='bilingual' else '')
    existing=[int(p.stem.rsplit('-v',1)[1]) for p in output.glob(stem+'-v*.dwg') if p.stem.rsplit('-v',1)[1].isdigit()]
    version=max(existing,default=0)+1;target=output/f'{stem}-v{version:02d}.dwg'
    with target.open('xb') as stream, candidate.open('rb') as src:
        shutil.copyfileobj(src,stream)
    require(digest(target)==result['candidateSha256'], 'Final copy hash mismatch')
    write(job/'delivery.json',dict(path=str(target),sha256=digest(target),status='delivered'))
    return str(target)


def deploy(source, target, files, archive):
    source,target,archive=Path(source).resolve(),Path(target).resolve(),Path(archive).resolve()
    require(target!=Path(target.anchor) and source!=target and not target.is_relative_to(source) and not source.is_relative_to(target), 'Unsafe deployment roots')
    require(not archive.is_relative_to(target) and not archive.is_relative_to(source), 'Archive must be outside active skill roots')
    paths=[]
    for name in files:
        rel=Path(name);src=(source/rel).resolve();dst=(target/rel).resolve()
        require(not rel.is_absolute() and src.is_relative_to(source) and dst.is_relative_to(target) and src.is_file(), 'Unsafe deployment file')
        paths.append((rel,src,dst))
    require(len({r for r,_,_ in paths})==len(paths), 'Duplicate deployment path')
    keep={r for r,_,_ in paths};preserved={'work','jobs','outputs','runs','.git'}
    target.mkdir(parents=True,exist_ok=True);archive.mkdir(parents=True,exist_ok=False)
    before=[]
    for old in list(target.rglob('*')):
        if not old.is_file():continue
        rel=old.relative_to(target)
        if rel.parts[0] in preserved:continue
        require(old.resolve().is_relative_to(target), 'Deployment path escaped target')
        if rel not in keep or digest(old)!=digest(source/rel):
            backup=archive/rel;backup.parent.mkdir(parents=True,exist_ok=True);shutil.move(str(old),str(backup));before.append(str(rel))
    for rel,src,dst in paths:
        dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst)
        require(digest(src)==digest(dst), 'Mounted deployment mismatch')
    result=dict(installedFiles=len(paths),removedOrReplaced=before,target=str(target),archive=str(archive))
    write(archive/'deployment.json',result)
    return result


def finish_process(process, status, timeout, log, exit_grace=2):
    """Wait for a completed native status, then end only this owned process tree."""
    deadline=time.monotonic()+timeout
    completed=None
    while time.monotonic()<deadline:
        if Path(status).is_file():
            try:
                value=read(status)
                if value.get('status'):completed=value;break
            except (ValueError,OSError):pass
        if process.poll() is not None:break
        time.sleep(.1)
    if process.poll() is None:
        try:process.wait(timeout=exit_grace if completed else 0)
        except subprocess.TimeoutExpired:
            if hasattr(subprocess,'CREATE_NO_WINDOW'):
                subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
            else:process.kill()
            process.wait(timeout=10)
    if completed is None:
        raise TimeoutError('Native stage ended without completed status; inspect retained log')
    return completed


def run_native(job, engine, script, stage, timeout=180):
    job=Path(job).resolve();task=read(job/'task.json');input_path=job/'input.dwg'
    if stage=='verify':
        input_path=job/'saved'/'input.dwg';input_path.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(job/'candidate.dwg',input_path)
    script=Path(script).resolve();engine=Path(engine).resolve()
    require(engine.is_file() and script.is_file(), 'Native engine/script missing')
    require(input_path.is_file() and input_path!=Path(task['sourcePath']).resolve(), 'Only isolated working copies may be opened')
    before=digest(input_path)
    if stage=='write':
        source,mapping=read(job/'extracted.json'),read(job/'mapping.json')
        validate_mapping(source,mapping)
        require(before==mapping['cadInputSha256'], 'Current native input binding changed')
    if stage in ('write','verify'):
        for proof in ('verification.json','delivery.json'):
            if (job/proof).exists():(job/proof).unlink()
    import os
    env=dict(os.environ,CAD_NATIVE_JOB=str(job),CAD_NATIVE_INPUT=str(input_path),CAD_NATIVE_STAGE=stage,
             CAD_NATIVE_SOURCE_HASH=task['sourceSha256'],CAD_NATIVE_INPUT_HASH=before)
    status=job/(stage+'-status.json')
    if status.exists():status.unlink()
    with (job/(stage+'.log')).open('wb') as log:
        process=subprocess.Popen([str(engine),'/i',str(input_path),'/s',str(script)],env=env,stdout=log,stderr=log,
                                 creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        finish_process(process,status,timeout,log)
    require(status.is_file(), 'Native process produced no status')
    result=read(status);require(result.get('status') not in ('failed',None), 'Native stage failed: '+str(result))
    if stage in ('export','verify'):
        manifest_path=job/'extracted.json' if stage=='export' else job/'saved'/'extracted.json'
        snapshot=read(manifest_path);snapshot['cadInputSha256']=digest(input_path)
        if stage=='verify':snapshot['openedCandidateSha256']=before
        write(manifest_path,snapshot)
    require(digest(task['sourcePath'])==task['sourceSha256'], 'Source was modified')
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__);subs=parser.add_subparsers(dest='command',required=True)
    prep=subs.add_parser('prepare');prep.add_argument('--source',type=Path,required=True);prep.add_argument('--job',type=Path,required=True);prep.add_argument('--mode',choices=['replace','bilingual'],required=True);prep.add_argument('--source-language',required=True);prep.add_argument('--target-language',required=True)
    for name in ('validate','verify','deliver'):
        cmd=subs.add_parser(name);cmd.add_argument('--job',type=Path,required=True)
        if name=='deliver':cmd.add_argument('--output',type=Path,required=True)
    native=subs.add_parser('run-native');native.add_argument('--job',type=Path,required=True);native.add_argument('--engine',type=Path,required=True);native.add_argument('--script',type=Path,required=True);native.add_argument('--stage',choices=['export','write','verify'],required=True);native.add_argument('--timeout',type=int,default=180)
    args=parser.parse_args();values=vars(args);command=values.pop('command')
    if command=='prepare':result=prepare(args.source,args.job,args.mode,args.source_language,args.target_language)
    elif command=='validate':result=validate_mapping(read(args.job/'extracted.json'),read(args.job/'mapping.json'))
    elif command=='run-native':result=run_native(**values)
    elif command=='verify':result=verify(args.job)
    else:result=deliver(args.job,args.output)
    print(json.dumps(result,ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
