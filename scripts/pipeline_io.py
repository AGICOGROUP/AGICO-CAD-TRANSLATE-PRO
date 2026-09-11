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
    runtime.authoritative_preflight(job, Path(config["manifestPath"]), owned)
    return launch_operation(job, root, timeout, config, runtime, "import")


def fresh_path(path):
    path = Path(path)
    index = 2
    candidate = path
    while candidate.exists():
        candidate = path.with_name(f"{path.stem}-{index:04d}{path.suffix}")
        index += 1
    return candidate


def launch_operation(job, root, timeout, config, runtime, operation, candidate=None, corrections=None):
    """One native attempt, bound before launch and retained even on native failure."""
    from task_recovery import write_binding
    stage = dict(config)
    path = fresh_path(job / "config" / f"{operation}-job.json")
    attempt = path.stem.removesuffix("-job")
    staged = (Path(candidate) if operation == "inspect" else fresh_path(
        job / "artifacts" / f"{config['outputMode']}-staged{Path(config['outputPath']).suffix}"))
    stage.update(operation=operation, translationPath=str(job / "exchange/translations.output.jsonl"),
        outputPath=str(staged), resultPath=str(fresh_path(job / "artifacts" / f"{attempt}-result.json")))
    if candidate is not None:
        stage.update(candidatePath=str(candidate), candidateSha256=digest(candidate))
    if corrections is not None:
        owned = fresh_path(job / "config" / "corrections.json")
        write_report(owned, corrections)
        stage["correctionPath"] = str(owned)
    runtime.require_ready(Path(config["sourcePath"]), root)
    write_report(path, stage)
    write_binding(job, config, staged, path)
    try:
        code = runtime._run_stage(operation, path, Path(config["workingPath"]), root, timeout)
    finally:
        write_binding(job, config, staged, path)
    if code:
        if Path(stage["resultPath"]).is_file():
            runtime._require_succeeded_result(Path(stage["resultPath"]), operation)
        return code, staged
    runtime._require_succeeded_result(Path(stage["resultPath"]), operation)
    if not staged.is_file(): raise RuntimeError(f"{operation} returned without a saved drawing.")
    return 0, staged

def publish_candidate(staged, output):
    if Path(staged).resolve() == Path(output).resolve(): return
    if Path(output).exists(): raise FileExistsError("Candidate already exists; use a fresh job.")
    shutil.copy2(staged, output)
