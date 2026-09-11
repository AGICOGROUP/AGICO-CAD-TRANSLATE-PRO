"""Render a native CAD review window without saving the drawing or waiting on QUIT."""
import argparse
import json
import math
import subprocess
import shutil
import time
from pathlib import Path
from cad_translate import discover_autocad, terminate_process_tree

def render(drawing, output, window=None, timeout=60, paper=False):
    return render_many(drawing, [(output, window)], timeout, paper)[0]


def render_many(drawing, requests, timeout=60, paper=False):
    """Render all requested windows in one disposable CAD session."""
    drawing = drawing.resolve()
    requests = [(output.resolve(), window) for output, window in requests]
    if not drawing.is_file(): raise FileNotFoundError(drawing)
    if not requests: return []
    outputs = [output for output, _ in requests]
    if len(set(outputs)) != len(outputs): raise ValueError("Duplicate review output path")
    for output, window in requests:
        if output.exists(): raise FileExistsError(output)
        if any(c in str(output) for c in '\r\n"'): raise ValueError("Unsafe output path")
        if window is not None and (len(window) != 4 or not all(math.isfinite(v) for v in window)
                or window[2] <= window[0] or window[3] <= window[1]):
            raise ValueError("Invalid review window")
        output.parent.mkdir(parents=True, exist_ok=True)
    output = outputs[0]
    # Core Console's localized QUIT prompt can save on "N". Render only an owned
    # copy, and end the process after PNGOUT instead of sending a save response.
    render_input = output.with_name("." + output.stem + "-input" + drawing.suffix)
    if render_input.exists(): raise FileExistsError(render_input)
    shutil.copy2(drawing, render_input)
    script = output.with_suffix(".scr")
    view = '' if paper else '_.UCS\n_W\n_.PLAN\n_W\n'
    commands = f'_.FILEDIA\n0\n_.CMDDIA\n0\n_.TILEMODE\n{0 if paper else 1}\n' + view + '_.QTEXTMODE\n0\n_.REGENALL\n'
    for target, window in requests:
        zoom = "_E\n" if window is None else f"_W\n{window[0]},{window[1]}\n{window[2]},{window[3]}\n"
        commands += '_.ZOOM\n' + zoom + '_.PNGOUT\n"' + target.as_posix() + '"\n_ALL\n\n'
    script.write_text(commands, encoding="utf-8")
    started = time.monotonic()
    with output.with_suffix(".log").open("wb") as log:
        process = subprocess.Popen([str(discover_autocad() / "accoreconsole.exe"), "/i", str(render_input), "/s", str(script)], stdout=log, stderr=log)
        while process.poll() is None:
            if all(p.is_file() and p.stat().st_size > 512 for p in outputs):
                try: process.wait(timeout=1)
                except subprocess.TimeoutExpired: terminate_process_tree(process)
                break
            if time.monotonic() - started > timeout:
                terminate_process_tree(process)
                raise TimeoutError("Native review render timed out")
            time.sleep(.2)
    missing = [p.name for p in outputs if not p.is_file() or p.stat().st_size <= 512]
    if missing: raise RuntimeError(f"AutoCAD did not produce complete review images: {missing}")
    return outputs


def review_regions(windows, handles):
    """Keep every placed instance, using the native source/target union bounds."""
    requested = {h.strip().upper() for h in handles.split(',') if h.strip()}
    selected = [w for w in windows if w['sourceHandle'].upper() in requested]
    missing = requested - {w['sourceHandle'].upper() for w in selected}
    if missing: raise ValueError(f"No native review window for handles: {sorted(missing)}")
    regions = []
    for w in selected:
        b = w['bounds']
        box = [b[k] for k in ('left', 'bottom', 'right', 'top')]
        if not all(math.isfinite(v) for v in box) or box[2] <= box[0] or box[3] <= box[1]:
            raise ValueError(f"Invalid native bounds for {w['sourceHandle']}")
        if w['instancePath'].split('/')[0].casefold() != '*model_space':
            raise ValueError('Paper-space windows require explicit layout review')
        regions.append({'sourceHandle': w['sourceHandle'], 'instancePath': w['instancePath'], 'window': box})
    return regions


def render_job(job, handles):
    job = job.resolve()
    config = json.loads((job / 'config/export-job.json').read_text(encoding='utf-8'))
    artifacts = job / 'artifacts'
    windows = json.loads((artifacts / 'bilingual-review-windows.json').read_text(encoding='utf-8'))['windows']
    regions = [{'window': None}] + review_regions(windows, handles)
    plan = []
    requests = {'source': [], 'candidate': []}
    for index, region in enumerate(regions):
        label = 'overview' if index == 0 else f"{region['sourceHandle']}-{index}"
        images = []
        for role, drawing in [('source', config['sourcePath']), ('candidate', config['outputPath'])]:
            output = artifacts / f'{role}-{label}.png'
            requests[role].append((output, region['window']))
            images.append(output.name)
        plan.append(dict(region, images=images))
    for role, drawing in [('source', config['sourcePath']), ('candidate', config['outputPath'])]:
        render_many(Path(drawing), requests[role], timeout=max(60, len(regions) * 10))
    path = artifacts / 'review-render-plan.json'
    path.write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding='utf-8')
    return path


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--drawing", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--job", type=Path)
    parser.add_argument("--handles", default='')
    parser.add_argument("--window", type=float, nargs=4)
    parser.add_argument("--paper", action="store_true")
    args = parser.parse_args()
    if args.job:
        if args.drawing or args.output or args.window or args.paper:
            parser.error('--job cannot be combined with single-drawing options')
        print(render_job(args.job, args.handles))
    else:
        if not args.drawing or not args.output: parser.error('--drawing and --output are required without --job')
        print(render(args.drawing, args.output, args.window, paper=args.paper))
