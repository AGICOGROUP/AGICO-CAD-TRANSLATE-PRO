"""Fresh native fixture: single-column strips must stay local labels, a real grid is a table.

Reproduces the two structures that were dropped on the Algeria PCC drawing:
a narrow column of stacked title cells with rotated signature labels, and two stacked
equipment frames joined only by process piping. A crowded 2x3 grid is the control that
must still take a table strategy.
"""
import argparse
import json
import sys
from pathlib import Path

from run_v2_fixture import cad, ezdxf, to_dwg

SIGNATURE = ["日   期", "签   名", "实   名", "专   业"]
BOXES = ["制冷机组", "板式换热器"]
TABLE = ["设备名称", "型号", "数量", "单位", "重量", "备注"]
TARGETS = {
    "日   期": "Date", "签   名": "Signature", "实   名": "Real Name", "专   业": "Discipline",
    "制冷机组": "Chiller Unit", "板式换热器": "Plate Heat Exchanger",
    # Cells hold the target alone but not source+target, so a real table must be copied.
    "设备名称": "Name", "型号": "Model", "数量": "Qty",
    "单位": "Unit", "重量": "Weight", "备注": "Remark",
}
LOCAL = "cell-local-or-nearby"
CELL = 12


def build(path):
    doc = ezdxf.new("R2018")
    doc.styles.new("Fixture", dxfattribs={"font": "simsun.ttc"})
    space = doc.modelspace()
    for index, label in enumerate(SIGNATURE):
        y = index * 8
        for a, b in [((0, y), (30, y)), ((0, y + 8), (30, y + 8)), ((0, y), (0, y + 8)), ((30, y), (30, y + 8))]:
            space.add_line(a, b)
        space.add_text(label, dxfattribs={"insert": (2.5, y + 1.5), "height": 1.8, "rotation": 90, "style": "Fixture"})
    for index, label in enumerate(BOXES):
        y = 40 + index * 20
        space.add_lwpolyline([(0, y), (20, y), (20, y + 7), (0, y + 7)], close=True)
        space.add_text(label, dxfattribs={"insert": (1.5, y + 2.4), "height": 2.2, "style": "Fixture"})
    for x in (5, 15):
        space.add_line((x, 47), (x, 60))
    for row in range(3):
        for column in range(2):
            x, y = 50 + column * CELL, row * 8
            for a, b in [((x, y), (x + CELL, y)), ((x, y + 8), (x + CELL, y + 8)), ((x, y), (x, y + 8)), ((x + CELL, y), (x + CELL, y + 8))]:
                space.add_line(a, b)
            space.add_text(TABLE[row * 2 + column], dxfattribs={"insert": (x + 1, y + 3), "height": 2.4, "style": "Fixture"})
    doc.saveas(path)


def run(output):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    host = cad.discover_autocad()
    source = output / "single-column.dxf"
    build(source)
    source = to_dwg(source, host)
    job = output / "job"
    if cad.run_export(source, job, "zh-CN", "en", host, output_mode="bilingual"):
        raise SystemExit("export failed")
    cad.prepare_translation_worklist(job)
    rows = []
    for part in (job / "exchange" / "translation-worklist").glob("*.jsonl"):
        for row in cad._read_jsonl(part):
            rows.append({"recordId": row["recordId"], "translatedText": TARGETS.get(row["sourceText"], row["sourceText"])})
    batch = job / "exchange" / "fixture.jsonl"
    cad._atomic_write_jsonl(batch, rows)
    assembled = cad.assemble_translations(job, batch)
    if cad.run_import(job, Path(assembled["output"]), host):
        raise SystemExit((job / "artifacts" / "import-result.json").read_text(encoding="utf-8"))
    report = cad.summarize_audit(job)
    pairs = json.loads((job / "artifacts" / "bilingual-pairs.json").read_text(encoding="utf-8"))["pairs"]
    sources = {row["handle"]: row for row in cad._read_jsonl(job / "exchange" / "manifest.input.jsonl")}
    table = json.loads((job / "artifacts" / "bilingual-table-layout.json").read_text(encoding="utf-8"))
    by_label = {}
    for pair in pairs:
        raw = sources[pair["sourceHandle"]]["rawText"]
        if raw in TARGETS:
            by_label[raw] = pair["placementStrategy"]
    assert report["status"] == "passed", report
    for label in SIGNATURE + BOXES:
        assert by_label.get(label) == LOCAL, (label, by_label.get(label), pairs)
    assert any(strategy.startswith("table") for label, strategy in by_label.items() if label in TABLE), by_label
    assert any(entry["strategy"].startswith("table") for entry in table["tables"]), table
    summary = {"status": "passed", "added": report["addedCount"], "localStrategies": by_label,
               "tableStrategies": sorted({entry["strategy"] for entry in table["tables"]})}
    (output / "report.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    run(parser.parse_args().output)
