"""No-edit SaveAs control; runs branch DLLs against an owned drawing copy."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import sys

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'scripts'))
from cad_translate import discover_autocad, terminate_process_tree

def run(source, output, plugin, mode='save', candidate=None):
    source, output, plugin = source.resolve(), output.resolve(), plugin.resolve()
    output.mkdir(parents=True, exist_ok=False)
    original = hashlib.sha256(source.read_bytes()).hexdigest()
    working = output / 'working.dwg'
    shutil.copy2(source, working)
    target = candidate.resolve() if candidate else output / 'saved.dwg'
    report = output / 'report.json'
    script = output / 'probe.scr'
    script.write_text('_.NETLOAD\n"' + plugin.as_posix() + '"\nCAD_STRUCTURE_PROBE\n', encoding='utf-8')
    env = dict(os.environ, CAD_STRUCTURE_SOURCE=str(source), CAD_STRUCTURE_OUTPUT=str(target),
        CAD_STRUCTURE_REPORT=str(report), CAD_STRUCTURE_MODE=mode)
    started = time.monotonic()
    with (output/'console.log').open('wb') as log:
        process = subprocess.Popen([str(discover_autocad()/'accoreconsole.exe'),'/i',str(working),'/s',str(script)],env=env,stdout=log,stderr=log)
        while process.poll() is None:
            if report.exists() and (output/'report.json.candidate.json').exists():
                terminate_process_tree(process)
                break
            if time.monotonic()-started>90:
                terminate_process_tree(process)
                raise TimeoutError(str(output/'console.log'))
            time.sleep(.2)
    assert hashlib.sha256(source.read_bytes()).hexdigest()==original, 'Source changed'
    data=json.loads(report.read_text(encoding='utf-8-sig'))
    print(json.dumps({'status':data['status'],'seconds':round(time.monotonic()-started,3),'report':str(report),
        'counts':{k:{i:v for i,v in d.items() if i not in ('removed','added')} for k,d in data.get('differences',{}).items()}}))

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--plugin',type=Path,required=True)
    parser.add_argument('--mode',default='save',choices=['save','save-false','compare','move','delete','anonymous-swap'])
    parser.add_argument('--candidate',type=Path)
    args=parser.parse_args()
    run(args.source,args.output,args.plugin,args.mode,args.candidate)
