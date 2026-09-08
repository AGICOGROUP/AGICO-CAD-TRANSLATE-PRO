import json
import hashlib
import os
import shutil
from pathlib import Path

def read_jsonl(path: Path) -> list[dict[str, object]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]

def write_report(path: Path | None, report: dict[str, object]) -> None:
    if path is None:
        return
    path = path.expanduser().resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

def read_report(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))

def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def launch_import(job, translations, root, timeout, config, runtime):
    """Shared host transport only. Each pipeline owns preflight and final acceptance."""
    owned = job / "exchange" / "translations.output.jsonl"
    if translations != owned.resolve(): shutil.copy2(translations, owned)
    config = dict(config)
    staged = job / "artifacts" / (config["outputMode"] + "-staged" + Path(config["outputPath"]).suffix)
    config.update(operation="import", translationPath=str(owned), outputPath=str(staged),
        resultPath=str(job / "artifacts" / "import-result.json"))
    path = job / "config" / "import-job.json"
    write_report(path, config)
    runtime.require_ready(Path(config["sourcePath"]), root)
    code = runtime._run_stage("import", path, Path(config["workingPath"]), root, timeout)
    if code: return code, staged
    runtime._require_succeeded_result(Path(config["resultPath"]), "import")
    if not staged.is_file(): raise RuntimeError("Import returned without a saved drawing.")
    return 0, staged

def publish_candidate(staged, output):
    if Path(output).exists(): raise FileExistsError("Candidate already exists; use a fresh job.")
    os.replace(staged, output)
