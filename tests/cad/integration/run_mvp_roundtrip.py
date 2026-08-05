"""One export plus one import MVP smoke. Invoke with an explicit real CPython executable."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import sys
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tests.cad.integration.run_coreconsole import (
    AUTOCAD_CORE_CONSOLE,
    PLUGIN_DLL,
    ensure_autocad_profile,
    run_coreconsole,
)


SAMPLE = ROOT / "cad样本1.dwg"
ARTIFACT_ROOT = ROOT / "artifacts"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def snapshot(path: Path) -> dict[str, int | str]:
    state = path.stat()
    return {"sha256": sha256(path), "mtimeNs": state.st_mtime_ns, "size": state.st_size}


def require_preflight() -> None:
    executable = Path(sys.executable).resolve()
    if "windowsapps" in str(executable).lower():
        raise RuntimeError(f"Refusing WindowsApps Python alias: {executable}")
    if not AUTOCAD_CORE_CONSOLE.is_file() or not PLUGIN_DLL.is_file() or not SAMPLE.is_file():
        raise RuntimeError("Core Console, release plugin, or sample DWG is missing.")
    ensure_autocad_profile()


def make_config(job: Path, source: Path, working: Path, source_hash: str, operation: str, translation: Path | None = None) -> dict[str, object]:
    return {
        "schemaVersion": "1.0", "jobId": f"cad-task6-{operation}-{job.name}", "operation": operation,
        "sourcePath": str(source), "workingPath": str(working), "sourceSha256": source_hash,
        "manifestPath": str(job / "exchange" / "manifest.input.jsonl"),
        "translationPath": None if translation is None else str(translation),
        "outputPath": str(job / "results" / "candidate.dwg"),
        "resultPath": str(job / f"{operation}-result.json"), "artifactDirectory": str(job / "artifacts"),
        "sourceLanguage": "zh-CN", "targetLanguage": "en",
    }


def choose_marker_record(records: list[dict[str, object]]) -> dict[str, object]:
    for record in records:
        if record["objectType"] in ("AcDbText", "AcDbMText") and not record["protectedTokens"] and record["plainText"]:
            return record
    raise RuntimeError("No safe DBText/MText record without protected tokens was exported.")


def main() -> int:
    require_preflight()
    job = ARTIFACT_ROOT / f"cad-task6-roundtrip-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}"
    if job.exists():
        raise RuntimeError(f"Refusing to reuse job directory: {job}")
    (job / "exchange").mkdir(parents=True)
    (job / "artifacts").mkdir()
    (job / "results").mkdir()
    working = job / "working.dwg"
    shutil.copy2(SAMPLE, working)
    source_before, working_before = snapshot(SAMPLE), snapshot(working)

    export_config = make_config(job, SAMPLE, working, str(source_before["sha256"]), "export")
    export_config_path = job / "export-job.json"
    export_config_path.write_text(json.dumps(export_config, ensure_ascii=False, indent=2), encoding="utf-8")
    export = run_coreconsole(export_config_path, working, operation="export", diagnostic_directory=job / "diagnostics-export")
    if export.returncode != 0:
        raise RuntimeError(f"Export Core Console exit={export.returncode}; no import will run.\n{export.stdout}\n{export.stderr}")
    manifest_path = Path(str(export_config["manifestPath"]))
    result_path = Path(str(export_config["resultPath"]))
    if not manifest_path.is_file() or not result_path.is_file():
        raise RuntimeError("Export did not produce retained manifest/result; no import will run.")
    export_result = json.loads(result_path.read_text(encoding="utf-8"))
    if export_result.get("status") != "succeeded":
        raise RuntimeError("Export envelope is not succeeded; no import will run.")
    records = [json.loads(line) for line in manifest_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    marker = choose_marker_record(records)
    translations: list[dict[str, object]] = []
    for record in records:
        translations.append({
            "schemaVersion": "1.0", "recordId": record["recordId"], "inputHash": record["inputHash"],
            "translatedText": "CAD MVP TEST" if record["recordId"] == marker["recordId"] else record["plainText"],
            "reviewStatus": "approved", "reason": "mvp controlled smoke",
        })
    translations_path = job / "exchange" / "translations.output.jsonl"
    translations_path.write_text("".join(json.dumps(item, ensure_ascii=False) + "\n" for item in translations), encoding="utf-8")

    import_config = make_config(job, SAMPLE, working, str(source_before["sha256"]), "import", translations_path)
    import_config_path = job / "import-job.json"
    import_config_path.write_text(json.dumps(import_config, ensure_ascii=False, indent=2), encoding="utf-8")
    import_result = run_coreconsole(import_config_path, working, operation="import", diagnostic_directory=job / "diagnostics-import")
    if import_result.returncode != 0:
        raise RuntimeError(f"Import Core Console exit={import_result.returncode}.\n{import_result.stdout}\n{import_result.stderr}")
    import_envelope = json.loads(Path(str(import_config["resultPath"])).read_text(encoding="utf-8"))
    candidate = Path(str(import_config["outputPath"]))
    if import_envelope.get("status") != "succeeded" or import_envelope.get("processedRecords") != len(records) or not candidate.is_file():
        raise RuntimeError("Import envelope/candidate verification failed.")
    source_after, working_after = snapshot(SAMPLE), snapshot(working)
    if source_after != source_before or working_after != working_before:
        raise RuntimeError("Source or working DWG changed during smoke.")
    summary = {
        "job": str(job), "sourceBefore": source_before, "sourceAfter": source_after,
        "workingBefore": working_before, "workingAfter": working_after,
        "recordCount": len(records), "changedRecordId": marker["recordId"], "changedObjectType": marker["objectType"],
        "manifestPath": str(manifest_path), "translationsPath": str(translations_path), "candidatePath": str(candidate),
        "exportResult": export_result, "importResult": import_envelope,
        "candidateSha256": sha256(candidate),
    }
    (job / "smoke-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
