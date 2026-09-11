"""Bounded saved-stage diagnosis; no import, correction or delivery approval."""
import math
from collections import Counter
from pathlib import Path

from job_timing import summarize
from pipeline_io import digest, read_report, write_report


def diagnose(job, windows=(), *, render=False):
    job = Path(job).resolve()
    windows = list(windows)
    if len(windows) > 8 or any(len(w) != 4 or not all(math.isfinite(v) for v in w)
                              or w[2] <= w[0] or w[3] <= w[1] for w in windows):
        raise ValueError('Use at most eight valid world-coordinate detail windows.')
    if render and not windows:
        raise ValueError('Diagnosis rendering needs a readable detail window; overview alone is insufficient.')
    artifacts = job / 'artifacts'
    config = read_report(job / 'config/export-job.json')
    mode = config['outputMode']
    issues = []

    def read(name):
        path = artifacts / name
        if not path.is_file():
            return {}
        try:
            value = read_report(path)
            if not isinstance(value, dict):
                raise ValueError('Expected a report object')
            return value
        except (OSError, ValueError) as error:
            issues.append(f'{name}: {str(error)[:180]}')
            return {}

    final = read(f'{mode}-final.json')
    verification = read('verification.json')
    binding = read('candidate-binding.json')
    candidate = final.get('candidatePath') or binding.get('candidatePath') or config['outputPath']
    expected_candidate = (final.get('candidateSha256') if final.get('candidatePath')
                          else binding.get('candidateSha256'))

    def drawing(path, expected, *, owned=False):
        path = Path(path).resolve()
        status = 'missing'
        actual = None
        if owned and (not path.is_relative_to(job) or path == Path(config['sourcePath']).resolve()):
            status = 'outside-job'
        elif path.is_file():
            actual = digest(path)
            status = 'matched' if expected and actual == expected else 'stale' if expected else 'unbound'
        return {'path': str(path), 'sha256': actual, 'status': status}

    drawings = {'source': drawing(config['sourcePath'], config.get('sourceSha256'))}
    if mode == 'replace':
        drawings['beforeCompose'] = drawing(
            artifacts / ('replace-before-compose' + Path(config['outputPath']).suffix),
            verification.get('candidateSha256'), owned=True)
    drawings['candidate'] = drawing(candidate, expected_candidate, owned=True)
    for role in ('source', 'candidate'):
        if drawings[role]['status'] != 'matched':
            issues.append(f"{role}: {drawings[role]['status']}; inspect bindings before using saved images.")

    audit = read(f'{mode}-layout-audit.json')
    risks = audit.get('manualReview', [])
    groups = {}
    for risk in risks:
        key = (risk.get('definitionName'), risk.get('regionId'), risk.get('code'))
        group = groups.setdefault(key, {'definition': key[0], 'region': key[1], 'code': key[2],
                                        'count': 0, 'examples': []})
        group['count'] += 1
        if len(group['examples']) < 3:
            group['examples'].append({k: risk.get(k) for k in ('recordId', 'otherRecordId', 'candidateWorldBounds')})
    timings = []
    for path in sorted(artifacts.glob('*-timing.json'), key=lambda p: p.stat().st_mtime, reverse=True):
        if path.name == 'workflow-timing.json':
            continue
        value = read(path.name)
        timings.append({'path': str(path), **{k: value[k] for k in
            ('operation', 'seconds', 'totalSeconds', 'phases', 'exitCode', 'timedOut') if k in value}})
        if len(timings) == 6:
            break
    logical = read('logical-flow-report.json')
    result = {'job': str(job), 'purpose': 'diagnosis-only', 'timing': summarize(job),
        'drawings': drawings, 'stageTimings': timings,
        'layout': {'riskCount': len(risks), 'byCode': dict(Counter(r.get('code', 'unknown') for r in risks)),
                   'groups': sorted(groups.values(), key=lambda g: g['count'], reverse=True)[:8],
                   'path': str(artifacts / f'{mode}-layout-audit.json'),
                   'scope': 'Saved layout audit; replacement composition can subsequently change placement.'},
        'composition': {k: logical.get(k) for k in ('replacedRecords', 'composedObjects')},
        'images': {}, 'windows': windows, 'issues': issues,
        'nextAction': 'Compare the same detail before/after composition. Test the specific cause locally before '
                      'one full regression; rendering saved stages does not test changed importer code.'}
    if render and all(drawings[r]['status'] == 'matched' for r in ('source', 'candidate')):
        from render_review import render_many
        for role, item in drawings.items():
            if item['status'] != 'matched':
                continue
            requests = [] if role == 'beforeCompose' else [(artifacts / f'diagnose-{role}-overview.png', None)]
            requests += [(artifacts / f'diagnose-{role}-detail-{i + 1}.png', w) for i, w in enumerate(windows)]
            outputs = render_many(Path(item['path']), requests, timeout=60)
            result['images'][role] = [p.name for p in outputs]
    write_report(artifacts / 'diagnostic-summary.json', result)
    return result
