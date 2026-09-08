"""Real AutoCAD regression: additive source retention, existing pairs and both directions.

Requires ezdxf for fixture generation only. Production runner remains stdlib-only.
Writes only to a fresh directory supplied by --output.
"""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts"))
import cad_translate as cad
import ezdxf

def write_fixture(path, english=False):
    doc = ezdxf.new("R2018")
    doc.styles.new("Fixture", dxfattribs={"font": "simsun.ttc"})
    space = doc.modelspace()
    space.add_lwpolyline([(0, 0), (200, 0), (200, 200), (0, 200)], close=True)
    for i in range(12):
        space.add_mtext("Pump" if english else "泵", dxfattribs={"insert": (20 + (i % 3) * 55, 175 - (i // 3) * 35),
            "char_height": 3, "width": 30, "style": "Fixture"})
    # One already-translated pair must be reused, never duplicated.
    space.add_mtext("Pump" if english else "泵", dxfattribs={"insert": (20, 25), "char_height": 3, "width": 30, "style": "Fixture"})
    space.add_mtext("泵" if english else "Pump", dxfattribs={"insert": (20, 20), "char_height": 2, "width": 30, "style": "Fixture"})
    doc.saveas(path)

def to_dwg(dxf, host):
    dwg = dxf.with_suffix(".dwg")
    script = dxf.with_suffix(".scr")
    script.write_text('_.FILEDIA\n0\n_.SAVEAS\n2018\n"' + dwg.as_posix() + '"\n', encoding="utf-8")
    started = time.monotonic()
    with (dxf.with_suffix(".log")).open("wb") as log:
        process = subprocess.Popen([str(host / "accoreconsole.exe"), "/i", str(dxf), "/s", str(script)], stdout=log, stderr=log)
        while process.poll() is None:
            if dwg.is_file() and dwg.stat().st_size > 1024:
                try: process.wait(timeout=2)
                except subprocess.TimeoutExpired: cad.terminate_process_tree(process)
                break
            if time.monotonic() - started > 60:
                cad.terminate_process_tree(process)
                raise RuntimeError("Fixture DWG conversion timed out")
            time.sleep(.2)
    if not dwg.is_file(): raise RuntimeError("DWG fixture was not created")
    return dwg

def translate(source, job, mode, source_lang, target_lang, host):
    started = time.monotonic()
    if cad.run_export(source, job, source_lang, target_lang, host, output_mode=mode): raise RuntimeError("export failed")
    work = cad.prepare_translation_worklist(job)
    rows = []
    for part in (job / "exchange" / "translation-worklist").glob("part-*.jsonl"):
        for row in cad._read_jsonl(part):
            value = row["sourceText"].replace("泵", "Pump") if target_lang == "en" else row["sourceText"].replace("Pump", "泵")
            rows.append({"recordId": row["recordId"], "translatedText": value})
    compact = job / "exchange" / "fixture-translations.jsonl"
    cad._atomic_write_jsonl(compact, rows)
    assembled = cad.assemble_translations(job, compact)
    if cad.run_import(job, Path(assembled["output"]), host):
        raise RuntimeError((job / "artifacts" / "import-result.json").read_text(encoding="utf-8"))
    result = cad.summarize_audit(job)
    assert result["status"] == "passed", result
    result.update(seconds=round(time.monotonic() - started, 3), worklist=work)
    return result

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    host = cad.discover_autocad()
    reports = []
    for english in (False, True):
        label = "en" if english else "zh"
        source = args.output / (label + ".dxf")
        write_fixture(source, english)
        source = to_dwg(source, host)
        source_lang, target_lang = ("en", "zh-CN") if english else ("zh-CN", "en")
        for mode in ("replace", "bilingual"):
            reports.append(translate(source, args.output / (label + "-" + mode), mode, source_lang, target_lang, host))
            if mode == "bilingual":
                assert reports[-1]["addedCount"] == 12, reports[-1]
                assert reports[-1]["skippedExistingCount"] == 1, reports[-1]
                repeated = translate(Path(reports[-1]["candidate"]), args.output / (label + "-bilingual-repeat"), mode, source_lang, target_lang, host)
                assert repeated["addedCount"] == 0, repeated
                assert repeated["skippedExistingCount"] == 13, repeated
                reports.append(repeated)
    (args.output / "report.json").write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(reports, ensure_ascii=False, indent=2))

if __name__ == "__main__": main()
