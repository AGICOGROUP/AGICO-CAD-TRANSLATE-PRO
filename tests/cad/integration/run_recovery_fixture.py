"""Native recovery/correction checks against fresh jobs from run_v2_fixture.py."""
import argparse
import json
from pathlib import Path
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parents[3]/'scripts'))
import cad_translate as cad
from pipeline_io import digest, read_report, read_jsonl, write_report


def negative_checks(job):
    config = read_report(job/'config/export-job.json')
    mode = config['outputMode']
    candidate = Path(cad.summarize_audit(job)['candidate'])
    old_hash = digest(candidate)
    original_hash = digest(config['sourcePath'])
    rows = read_jsonl(job/f'artifacts/{mode}-candidate.jsonl')
    if mode == 'bilingual':
        pair = next(p for p in read_report(job/'artifacts/bilingual-pairs.json')['pairs'] if p['decision'] == 'added')
        row = next(r for r in rows if r['handle'] == pair['targetHandle'])
    else:
        row = next(r for r in rows if r['objectType'] == 'AcDbMText')
    cases = [(row['handle'], row['rawText'] + ' stale', 'correction_text_changed')]
    if mode == 'bilingual':
        source = read_jsonl(job/'exchange/manifest.input.jsonl')[0]
        cases.append((source['handle'], source['rawText'], 'correction_target_forbidden'))
    for handle, expected, code in cases:
        path = job/'exchange/test-invalid-corrections.json'
        write_report(path, {'candidateSha256': old_hash, 'edits': [dict(handle=handle, expectedText=expected, width=35)]})
        try: cad.run_correct(job, path, cad.discover_autocad())
        except RuntimeError as error: assert code in str(error), str(error)
        else: raise AssertionError(f'Native correction did not reject {code}')
        assert digest(candidate) == old_hash and digest(config['sourcePath']) == original_hash
        assert cad.run_resume(job, cad.discover_autocad())['action'] == 'reused'
    return [case[2] for case in cases]

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixtures', type=Path, required=True)
    args = parser.parse_args()
    results = []
    for mode in ('replace', 'bilingual'):
        job = (args.fixtures/f'zh-{mode}').resolve()
        config = read_report(job/'config/export-job.json')
        original = digest(config['sourcePath'])
        candidate = Path(cad.summarize_audit(job)['candidate'])
        candidate_hash = digest(candidate)
        started = time.monotonic()
        reused = cad.run_resume(job, cad.discover_autocad())
        assert reused['action'] == 'reused' and not reused['deliveryReady']
        rows = read_jsonl(job/f'artifacts/{mode}-candidate.jsonl')
        if mode == 'bilingual':
            pair = next(p for p in read_report(job/'artifacts/bilingual-pairs.json')['pairs'] if p['decision'] == 'added')
            row = next(r for r in rows if r['handle'] == pair['targetHandle'])
        else:
            row = next(r for r in rows if r['objectType'] == 'AcDbMText')
        correction = {'candidateSha256': candidate_hash, 'edits': [dict(handle=row['handle'], expectedText=row['rawText'], width=35)]}
        path = job/'exchange/test-corrections.json'
        write_report(path, correction)
        corrected = cad.run_correct(job, path, cad.discover_autocad())
        assert corrected['action'] == 'corrected' and not corrected['deliveryReady']
        assert corrected['candidate'] != str(candidate) and digest(candidate) == candidate_hash
        assert digest(config['sourcePath']) == original
        assert cad.run_resume(job, cad.discover_autocad())['action'] == 'reused'
        try: cad.run_correct(job, path, cad.discover_autocad())
        except ValueError as error: assert 'stale' in str(error)
        else: raise AssertionError('Stale correction accepted')
        rejected = negative_checks(job)
        results.append(dict(mode=mode, seconds=round(time.monotonic()-started,3), candidate=corrected['candidate'],
            originalPreserved=True, previousCandidatePreserved=True, staleCorrectionRejected=True, nativeRejected=rejected, requiresVisualReview=True))
    write_report(args.fixtures/'recovery-report.json', {'cases': results})
    print(json.dumps(results))
