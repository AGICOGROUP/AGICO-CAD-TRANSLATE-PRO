"""Run the native near-label placement regression without a user drawing."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'scripts'))
from cad_translate import discover_autocad, terminate_process_tree

def run(plugin, output, command):
    output.mkdir(parents=True, exist_ok=False)
    report = output / 'report.json'
    script = output / 'test.scr'
    script.write_text('_.NETLOAD\n"' + plugin.resolve().as_posix() + '"\n' + command + '\n', encoding='utf-8')
    env = dict(os.environ, CAD_CONTACT_TEST_REPORT=str(report.resolve()))
    with (output / 'console.log').open('wb') as log:
        process = subprocess.Popen([str(discover_autocad() / 'accoreconsole.exe'), '/s', str(script.resolve())], env=env, stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + 60
            while not report.exists() and process.poll() is None and time.monotonic() < deadline:
                time.sleep(.2)
        finally:
            if process.poll() is None:
                terminate_process_tree(process)
    result = json.loads(report.read_text(encoding='utf-8-sig'))
    print(json.dumps(result))
    return 0 if result['status'] == 'passed' else 1

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--plugin', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--command', default='CAD_NEAR_LABEL_TEST')
    args = parser.parse_args()
    sys.exit(run(args.plugin,args.output,args.command))
