"""Inspect or correct a retained job in an isolated copy with branch DLLs."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'scripts'))
from cad_translate import _run_stage, _require_succeeded_result, discover_autocad
from pipeline_io import digest, write_report


def clone_job(source, target):
    target.mkdir(parents=True, exist_ok=False)
    for folder in ('config', 'exchange', 'artifacts', 'working'):
        (target/folder).mkdir()
    for folder in ('exchange', 'artifacts'):
        for path in (source/folder).iterdir():
            if path.suffix in ('.json', '.jsonl'):
                shutil.copy2(path, target/folder/path.name)
    config = json.loads((source/'config/import-job.json').read_text(encoding='utf-8'))
    working = target/'working/source.dwg'
    shutil.copy2(config['workingPath'], working)
    candidate = target/'artifacts'/('retained'+Path(config['outputPath']).suffix)
    shutil.copy2(config['outputPath'], candidate)
    config.update(workingPath=str(working), outputPath=str(candidate), artifactDirectory=str(target/'artifacts'),
        manifestPath=str(target/'exchange/manifest.input.jsonl'), translationPath=str(target/'exchange/translations.output.jsonl'))
    return config


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    p = argparse.ArgumentParser()
    p.add_argument('--job', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--plugin-dir', type=Path, required=True)
    p.add_argument('--corrections', type=Path)
    args = p.parse_args()
    target = args.output.resolve()
    config = clone_job(args.job.resolve(), target)
    operation = 'correct' if args.corrections else 'inspect'
    candidate = Path(config['outputPath'])
    old_hash = digest(candidate)
    source_hash = digest(config['sourcePath'])
    config.update(operation=operation, candidateSha256=old_hash, resultPath=str(target/f'artifacts/{operation}-result.json'))
    if args.corrections:
        corrections = json.loads(args.corrections.read_text(encoding='utf-8'))
        corrections['candidateSha256'] = old_hash
        owned = target/'exchange/corrections.json'
        write_report(owned, corrections)
        config.update(candidatePath=str(candidate), correctionPath=str(owned), outputPath=str(target/'artifacts/corrected.dwg'))
    path = target/f'config/{operation}-job.json'
    write_report(path, config)
    os.environ['CAD_TRANSLATE_PLUGIN_DIR'] = str(args.plugin_dir.resolve())
    started = time.monotonic()
    code = _run_stage(operation, path, Path(config['workingPath']), discover_autocad(), 300)
    assert digest(candidate) == old_hash and digest(config['sourcePath']) == source_hash
    print(json.dumps({'operation': operation, 'exitCode': code, 'seconds': round(time.monotonic()-started, 3), 'result': config['resultPath']}))
    _require_succeeded_result(Path(config['resultPath']), operation)
