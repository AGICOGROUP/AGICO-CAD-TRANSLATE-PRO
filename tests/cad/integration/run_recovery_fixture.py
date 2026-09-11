"""Native recovery/correction checks against fresh jobs from run_v2_fixture.py."""
import argparse
import json
from pathlib import Path
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parents[3]/'scripts'))
import cad_translate as cad
from pipeline_io import digest, read_report, read_jsonl, write_report

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
        results.append(dict(mode=mode, seconds=round(time.monotonic()-started,3), candidate=corrected['candidate'],
            originalPreserved=True, previousCandidatePreserved=True, staleCorrectionRejected=True, requiresVisualReview=True))
    write_report(args.fixtures/'recovery-report.json', {'cases': results})
    print(json.dumps(results))
