"""Replace the mounted skill with the complete maintained native skill."""
import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from native_job import deploy, digest, write
ROOT=Path(__file__).resolve().parents[1]
ROOT_FILES=['SKILL.md','global.json','.gitignore','pytest.ini']
RESOURCE_DIRS=['agents','references','scripts','tests','docs']
def maintained_files():
    files=list(ROOT_FILES)
    for directory in RESOURCE_DIRS:
        for path in (ROOT/directory).rglob('*'):
            if path.is_file() and not any(p in ('__pycache__','obj','bin','.pytest_cache') for p in path.relative_to(ROOT).parts):
                files.append(path.relative_to(ROOT).as_posix())
    return sorted(files)
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--target',type=Path,required=True)
    args=parser.parse_args()
    archive=Path.home()/'.codex'/'skill-archives'/('native-deploy-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    files=maintained_files()
    if args.target.resolve()==ROOT:
        result=dict(target=str(args.target),archive=None,installedFiles=len(files),removedOrReplaced=[],sharedDirectory=True,
                    resolvedPath=str(ROOT),sha256={name:digest(ROOT/name) for name in files})
        write(ROOT/'work'/'skill-migration'/'mounted-verification.json',result)
    else:
        result=deploy(ROOT,args.target,files,archive)
    print(json.dumps(dict(target=result['target'],archive=result['archive'],installedFiles=result['installedFiles'],removedOrReplacedCount=len(result['removedOrReplaced'])),indent=2))
