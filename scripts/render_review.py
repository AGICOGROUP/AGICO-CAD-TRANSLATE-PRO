"""Render a native CAD review window without saving the drawing or waiting on QUIT."""
import argparse
import subprocess
import shutil
import time
from pathlib import Path
from cad_translate import discover_autocad, terminate_process_tree

def render(drawing, output, window=None, timeout=60, paper=False):
    drawing, output = drawing.resolve(), output.resolve()
    if not drawing.is_file(): raise FileNotFoundError(drawing)
    if output.exists(): raise FileExistsError(output)
    if any(c in str(output) for c in '\r\n"'): raise ValueError("Unsafe output path")
    output.parent.mkdir(parents=True, exist_ok=True)
    # Core Console's localized QUIT prompt can save on "N". Render only an owned
    # copy, and end the process after PNGOUT instead of sending a save response.
    render_input = output.with_name("." + output.stem + "-input" + drawing.suffix)
    if render_input.exists(): raise FileExistsError(render_input)
    shutil.copy2(drawing, render_input)
    script = output.with_suffix(".scr")
    zoom = "_E\n" if window is None else f"_W\n{window[0]},{window[1]}\n{window[2]},{window[3]}\n"
    script.write_text(f'_.FILEDIA\n0\n_.CMDDIA\n0\n_.TILEMODE\n{0 if paper else 1}\n_.REGENALL\n_.ZOOM\n' + zoom +
        '_.PNGOUT\n"' + output.as_posix() + '"\n_ALL\n\n', encoding="utf-8")
    started = time.monotonic()
    with output.with_suffix(".log").open("wb") as log:
        process = subprocess.Popen([str(discover_autocad() / "accoreconsole.exe"), "/i", str(render_input), "/s", str(script)], stdout=log, stderr=log)
        while process.poll() is None:
            if output.is_file() and output.stat().st_size > 512:
                try: process.wait(timeout=1)
                except subprocess.TimeoutExpired: terminate_process_tree(process)
                break
            if time.monotonic() - started > timeout:
                terminate_process_tree(process)
                raise TimeoutError("Native review render timed out")
            time.sleep(.2)
    if not output.is_file(): raise RuntimeError("AutoCAD did not produce a review image")
    return output

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--drawing", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--window", type=float, nargs=4)
    parser.add_argument("--paper", action="store_true")
    args = parser.parse_args()
    print(render(args.drawing, args.output, args.window, paper=args.paper))
