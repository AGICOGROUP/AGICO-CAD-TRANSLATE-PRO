"""Run the native near-label placement regression without a user drawing."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'scripts'))
from cad_translate import discover_autocad, terminate_process_tree

JOB_INPUTS = {
    'CAD_BILINGUAL_REVIEW_TEST': ('config/export-job.json', 'artifacts/bilingual-pairs.json', 'artifacts/bilingual-candidate.jsonl'),
    'CAD_NEAR_LABEL_PROBE': ('exchange/manifest.input.jsonl', 'artifacts/bilingual-pairs.json', 'results/candidate.dwg'),
}


def run(plugin, output, command, job=None):
    plugin, output = plugin.resolve(), output.resolve()
    if not plugin.is_file():
        raise ValueError(f'Native test plugin does not exist: {plugin}')
    if not re.fullmatch(r'CAD_[A-Z0-9_]+', command):
        raise ValueError('Expected one CAD test command')
    env = dict(os.environ, CAD_CONTACT_TEST_REPORT=str(output / 'report.json'))
    if command in JOB_INPUTS:
        job = job or env.get('CAD_LAYOUT_REVIEW_JOB')
        if not job:
            raise ValueError(f'{command} requires --job (CAD_LAYOUT_REVIEW_JOB)')
        job = Path(job).resolve()
        for required in JOB_INPUTS[command]:
            if not (job / required).is_file():
                raise ValueError(f'Incomplete review job: missing {required} under {job}')
        env['CAD_LAYOUT_REVIEW_JOB'] = str(job)
    output.mkdir(parents=True, exist_ok=False)
    report = output / 'report.json'
    script = output / 'test.scr'
    script.write_text('_.NETLOAD\n"' + plugin.resolve().as_posix() + '"\n' + command + '\n', encoding='utf-8')
    result = None
    with (output / 'console.log').open('wb') as log:
        process = subprocess.Popen([str(discover_autocad() / 'accoreconsole.exe'), '/s', str(script.resolve())], env=env, stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + 60
            while True:
                exit_code = process.poll()
                try:
                    result = json.loads(report.read_text(encoding='utf-8-sig'))
                    if not isinstance(result, dict) or result.get('status') not in ('passed', 'failed'):
                        result = {'status': 'failed', 'code': 'invalid-test-report'}
                    break
                except (FileNotFoundError, json.JSONDecodeError, UnicodeDecodeError):
                    pass  # Older commands may still be writing their JSON report.
                if exit_code is not None or time.monotonic() >= deadline:
                    result = {'status': 'failed',
                              'code': 'host-exited-without-report' if exit_code is not None else 'host-timeout',
                              'exitCode': exit_code, 'consoleLog': str(output / 'console.log')}
                    break
                time.sleep(.2)
        finally:
            if process.poll() is None:
                terminate_process_tree(process)
    report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result))
    return 0 if result['status'] == 'passed' else 1

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--plugin', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--command', default='CAD_NEAR_LABEL_TEST')
    parser.add_argument('--job', type=Path, help='Existing diagnostic job required by review/probe commands')
    args = parser.parse_args()
    try:
        sys.exit(run(args.plugin,args.output,args.command,args.job))
    except ValueError as error:
        parser.error(str(error))
