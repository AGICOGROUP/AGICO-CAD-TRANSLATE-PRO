"""Opt-in native regression: preserve source, clone table geometry, then add nothing on rerun.

Use an existing approved translation job only as a fixed translation fixture; all CAD
stages run in fresh directories. Example: --source drawing.dwg --translation-job jobs/fixture
--job-root jobs/copy-regression-new. Requires AutoCAD 2025 and the current deployed DLL.
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def rows(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]

def run(source, fixture, root):
    if root.exists():
        raise FileExistsError(root)
    manifest = rows(fixture / "exchange/manifest.input.jsonl")
    translations = {r["recordId"]: r for r in rows(fixture / "exchange/translations.output.jsonl")}
    targets = {r["rawText"]: translations[r["recordId"]]["translatedText"] for r in manifest}
    def stage(*args):
        subprocess.run([sys.executable, str(ROOT / "scripts/cad_translate.py"), *map(str, args)], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    def attempt(drawing, name):
        job = root / name
        stage("export", "--source", drawing, "--job", job, "--output-mode", "bilingual", "--source-language", "zh-CN", "--target-language", "en")
        stage("prepare-translations", "--job", job)
        batch = job / "exchange/fixture.jsonl"
        result = []
        by_id = {r["recordId"]: r for r in rows(job / "exchange/manifest.input.jsonl")}
        for part in (job / "exchange/translation-worklist").glob("*.jsonl"):
            for request in rows(part):
                row = by_id[request["recordId"]]
                result.append({"recordId": row["recordId"], "translatedText": targets.get(row["rawText"], row["plainText"])})
        batch.write_text("\n".join(json.dumps(r, ensure_ascii=False) for r in result)+"\n", encoding="utf-8")
        stage("assemble-translations", "--job", job, "--translated", batch)
        stage("import", "--job", job, "--translations", job / "exchange/translations.output.jsonl")
        return job
    first = attempt(source, "first")
    table = json.loads((first / "artifacts/bilingual-table-layout.json").read_text())
    assert any(t["strategy"] == "table-copy" for t in table["tables"]), table["tables"]
    assert table["copies"], "No verified clone receipts"
    native = json.loads((first / "artifacts/bilingual-native-check.json").read_text())
    assert native["status"] == "passed" and not native["changedSources"]
    second = attempt(first / "results/candidate.dwg", "second")
    native2 = json.loads((second / "artifacts/bilingual-native-check.json").read_text())
    table2 = json.loads((second / "artifacts/bilingual-table-layout.json").read_text())
    assert native2["status"] == "passed" and native2["addedCount"] == 0, native2
    assert not table2["copies"], "Repeated pass cloned the table again"
    print(json.dumps({"status": "passed", "firstAdded": native["addedCount"], "secondAdded": native2["addedCount"],
        "copiedEntities": len(table["copies"]), "job": str(first)}, indent=2))

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--translation-job", type=Path, required=True)
    parser.add_argument("--job-root", type=Path, required=True)
    args = parser.parse_args()
    run(args.source.resolve(), args.translation_job.resolve(), args.job_root.resolve())
