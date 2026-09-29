"""Hidden, isolated native PNG screenshots with drawing/file bindings."""
import argparse
import ctypes
import hashlib
import json
import math
import shutil
import subprocess
import time
import uuid
from pathlib import Path


def digest(path):
    with Path(path).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()


def render(drawing,output,engine,window=None,job=None,timeout=60,layout='Model'):
    drawing,output,engine=Path(drawing).resolve(),Path(output).resolve(),Path(engine).resolve()
    job=Path(job).resolve() if job else output.parent
    if not output.is_relative_to(job) or output==drawing:
        raise ValueError('PNG output must be inside the working directory')
    if not drawing.is_file() or not engine.is_file():raise FileNotFoundError('Native drawing or engine unavailable')
    if window is not None and (len(window)!=4 or not all(math.isfinite(x) for x in window) or window[0]>=window[2] or window[1]>=window[3]):
        raise ValueError('Invalid native view window')
    if output.exists():raise FileExistsError(output)
    if not layout or any(c in layout for c in '\n\r"'):raise ValueError('Invalid layout name')
    output.parent.mkdir(parents=True,exist_ok=True)
    original_hash=digest(drawing);copy=output.parent/(uuid.uuid4().hex+'-view-input.dwg');shutil.copy2(drawing,copy)
    script=output.with_suffix('.scr')
    for path in (copy,output):
        if any(c in str(path) for c in '\n\r"'):raise ValueError('Unsafe CAD script path')
    zoom='_E\n' if window is None else '_W\n'+f'{window[0]},{window[1]}\n{window[2]},{window[3]}\n'
    space='_.TILEMODE\n1\n' if layout=='Model' else '_.TILEMODE\n0\n_.CTAB\n'+layout+'\n_.PSPACE\n'
    view='_.UCS\n_W\n_.PLAN\n_W\n' if layout=='Model' else ''
    # Core Console scripts use the Windows ANSI code page for localized values.
    encoding='cp'+str(ctypes.windll.kernel32.GetACP()) if hasattr(ctypes,'windll') else 'utf-8'
    script.write_text('_.FILEDIA\n0\n_.CMDDIA\n0\n'+space+view+'_.QTEXTMODE\n0\n_.REGENALL\n_.ZOOM\n'+zoom+'_.PNGOUT\n"'+output.as_posix()+'"\n_ALL\n\n',encoding=encoding)
    with output.with_suffix('.log').open('wb') as log:
        process=subprocess.Popen([str(engine),'/i',str(copy),'/s',str(script)],stdout=log,stderr=log,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        deadline=time.monotonic()+timeout
        while time.monotonic()<deadline and process.poll() is None:
            if output.is_file() and output.stat().st_size>512:break
            time.sleep(.2)
        if process.poll() is None:
            subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],stdout=log,stderr=log,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        process.wait(timeout=10)
    if not output.is_file() or output.stat().st_size<=512:raise RuntimeError('Native PNG was not produced; inspect render log')
    if digest(drawing)!=original_hash:raise RuntimeError('Drawing changed during native render')
    receipt=dict(native=True,drawingSha256=original_hash,pngSha256=digest(output),enginePath=str(engine),engineSha256=digest(engine),window=window,layout=layout)
    output.with_suffix(output.suffix+'.render.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
    return str(output)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--drawing',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--engine',type=Path,required=True);parser.add_argument('--job',type=Path,required=True);parser.add_argument('--window',type=float,nargs=4)
    parser.add_argument('--layout',default='Model')
    args=parser.parse_args();print(render(**vars(args)))
