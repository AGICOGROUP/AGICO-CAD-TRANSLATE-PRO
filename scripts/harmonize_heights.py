"""Harmonise the text height inside each note column / paragraph run of a saved candidate.

The fitter decides a height scale per record, from the box that record happens to sit in:
a line that keeps 1.0 next to a line squeezed to 0.25 makes one paragraph read with two
very different text sizes ("text is alternately large and small"). This step looks at the
whole run instead of one line at a time and moves every member to the run's median scale,
then emits `correct`-format corrections so the engine's own geometry-only pass applies them.

Usage:
    python scripts/harmonize_heights.py --job "jobs/<job>" [--apply] [--max-delta 0.35]

Without --apply it only reports the runs it would change. With --apply it writes
exchange/harmonize-corrections.json and runs `correct` on it.
"""
import argparse
import io
import json
import os
import statistics
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# A run is harmonised only when the members really disagree, and never below the floor the
# readability policy already guarantees for notes.
MIN_SPREAD = 0.20
FLOOR = 0.40
CEILING = 1.0
# A line moves toward the run's median only within these bounds: pulling a squeezed line up
# past +15% could collide with the neighbours its own fit avoided, and cutting a line by
# more than 30% would throw away size the fit had already proven it could use.
MAX_GROWTH_FACTOR = 1.15
MIN_SHRINK_FACTOR = 0.70
GROUP_MAX_VERTICAL_GAP_IN_LINE_HEIGHTS = 6.0
MIN_MEMBERS = 3
LEFT_MARGIN_TOLERANCE = 1.5


def load_jsonl(path):
    rows = []
    with io.open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def box(bounds):
    if bounds is None:
        return None
    if "minX" in bounds:
        return (bounds["minX"], bounds["maxX"], bounds["minY"], bounds["maxY"])
    if "left" in bounds:
        return (bounds["left"], bounds["right"], bounds["bottom"], bounds["top"])
    return None


def runs_of(records):
    """Group records that read as one paragraph/column: same definition, consecutive rows
    no further apart than a few line heights, and x-ranges that overlap the run so far.

    Members keep their own source text height, so the grouping must not filter by it - the
    reader compares the *rendered* sizes, not the scales."""
    out = []
    for definition_rows in _split_by(records, lambda r: r["definition"]):
        # A reading column is a set of lines that start at the same margin; only inside such
        # a column does a vertical chain mean "one paragraph". Chaining the raw y-order would
        # let a stray dimension or callout cut every paragraph after two lines.
        columns = []
        for row in sorted(definition_rows, key=lambda r: r["left"]):
            if columns and row["left"] - min(m["left"] for m in columns[-1]) <= LEFT_MARGIN_TOLERANCE * max(row["height"], 1e-6):
                columns[-1].append(row)
            else:
                columns.append([row])
        for column in columns:
            column.sort(key=lambda r: -r["top"])
            index = 0
            while index < len(column):
                run = [column[index]]
                end = index + 1
                while end < len(column):
                    previous, current = column[end - 1], column[end]
                    gap = previous["top"] - current["bottom"]
                    allowance = GROUP_MAX_VERTICAL_GAP_IN_LINE_HEIGHTS * max(previous["height"], current["height"])
                    if gap <= allowance:
                        run.append(current)
                        end += 1
                    else:
                        break
                out.append(run)
                index = end
    return out


def _split_by(rows, key):
    buckets = {}
    for row in rows:
        buckets.setdefault(key(row), []).append(row)
    return list(buckets.values())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--job", required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--max-delta", type=float, default=0.35,
                        help="largest scale change applied to a single line")
    parser.add_argument("--only-region", default=None,
                        help="restrict to records whose regionId contains this text")
    args = parser.parse_args()

    job = args.job if os.path.isabs(args.job) else os.path.join(ROOT, args.job)
    artifacts = os.path.join(job, "artifacts")
    adjustments = json.load(io.open(os.path.join(artifacts, "replace-layout-adjustments.json"), encoding="utf-8"))["adjustments"]
    audit = json.load(io.open(os.path.join(artifacts, "replace-layout-audit.json"), encoding="utf-8"))["texts"]
    candidate = {(row.get("handle") or "").upper(): row for row in load_jsonl(os.path.join(artifacts, "replace-candidate.jsonl"))}
    manifest = {row["recordId"]: row for row in load_jsonl(os.path.join(job, "exchange", "manifest.input.jsonl"))}
    region_of = {t["recordId"]: (t.get("regionId") or "") for t in audit}

    records = []
    skipped = 0
    for entry in adjustments:
        source = box(entry.get("sourceBounds"))
        original = entry.get("originalHeight")
        current = entry.get("newHeight")
        if not source or not original or not current:
            skipped += 1
            continue
        record_id = entry["recordId"]
        region = region_of.get(record_id, "")
        if args.only_region and args.only_region not in region:
            continue
        handle = (entry.get("newHandle") or "").upper()
        snapshot = candidate.get(handle)
        if snapshot is None or not snapshot.get("rawText"):
            skipped += 1
            continue
        properties = (manifest.get(record_id) or {}).get("properties") or {}
        records.append({
            "recordId": record_id, "handle": handle, "text": snapshot["rawText"],
            "objectType": snapshot.get("objectType"),
            "definition": snapshot.get("ownerPath", "").rsplit("/", 1)[-1],
            "left": source[0], "right": source[1], "bottom": source[2], "top": source[3],
            "height": float(properties.get("height") or original),
            "originalHeight": float(original), "currentHeight": float(current),
            "scale": float(current) / float(original), "region": region,
        })

    corrections = []
    changed_runs = []
    for run in runs_of(records):
        if len(run) < MIN_MEMBERS:
            continue
        rendered = [member["currentHeight"] for member in run]
        typical = statistics.median(rendered)
        if typical <= 0 or (max(rendered) - min(rendered)) / typical < MIN_SPREAD:
            continue
        # The run moves to its own median rendered height: over-large lines come down (no
        # overflow risk) and squeezed lines come up only as far as they safely can.
        target = typical
        touched = []
        for member in run:
            wanted = min(target, member["currentHeight"] * MAX_GROWTH_FACTOR)
            wanted = max(wanted, member["currentHeight"] * MIN_SHRINK_FACTOR)
            wanted = min(wanted, member["originalHeight"] * CEILING)
            change = abs(wanted - member["currentHeight"]) / member["currentHeight"]
            if change <= 0.03 or change > args.max_delta:
                continue
            corrections.append({"handle": member["handle"], "expectedText": member["text"],
                                "height": round(wanted, 4)})
            touched.append((member["handle"], round(member["currentHeight"], 1), round(wanted, 1)))
        if touched:
            changed_runs.append((run[0]["definition"], len(run), len(touched), typical, touched))

    print("records considered %d (skipped %d) | runs changed %d | corrections %d"
          % (len(records), skipped, len(changed_runs), len(corrections)))
    for definition, size, changed, typical, touched in changed_runs[:12]:
        print("   def=%-14s members=%d median=%.0f -> %d lines (e.g. %s)"
              % (definition, size, typical, changed, touched[:2]))

    if not corrections:
        print("nothing to harmonise")
        return 0

    # The correction document is bound to the candidate it was measured on.
    final = json.load(io.open(os.path.join(artifacts, "replace-final.json"), encoding="utf-8"))
    out_path = os.path.join(job, "exchange", "harmonize-corrections.json")
    io.open(out_path, "w", encoding="utf-8").write(
        json.dumps({"candidateSha256": final["candidateSha256"], "edits": corrections},
                   ensure_ascii=False, indent=2))
    print("corrections written:", out_path)
    if not args.apply:
        print("dry run; pass --apply to run the correct step")
        return 0

    command = [sys.executable, os.path.join(ROOT, "scripts", "cad_translate.py"), "correct",
               "--job", job, "--corrections", out_path]
    print("+", " ".join(command))
    return subprocess.call(command)


if __name__ == "__main__":
    raise SystemExit(main())
