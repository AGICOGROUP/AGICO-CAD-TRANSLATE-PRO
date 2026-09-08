"""Deploy maintained resources to a mounted skill, with per-file recoverable backups."""
import argparse
import hashlib
import json
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path

ALLOWED = {"SKILL.md", "global.json", ".gitignore", "agents", "assets", "references", "scripts", "src", "tests", "docs"}

def digest(path):
    with path.open("rb") as stream: return hashlib.file_digest(stream, "sha256").hexdigest()

def deploy(source, target, files, removed):
    source, target = source.resolve(), target.resolve()
    if target == Path(target.anchor) or target == source or target.is_relative_to(source):
        raise ValueError("Deployment requires a separate, bounded mounted directory")
    paths = []
    for name in dict.fromkeys(files + removed):
        rel = Path(name)
        if rel.is_absolute() or not rel.parts or rel.parts[0] not in ALLOWED or ".." in rel.parts:
            raise ValueError("Out-of-scope deployment path: " + name)
        src, dst = (source / rel).resolve(), (target / rel).resolve()
        if not src.is_relative_to(source) or not dst.is_relative_to(target): raise ValueError("Deployment path escapes root")
        if name in files and not src.is_file(): raise FileNotFoundError(src)
        paths.append((name, src, dst))
    backup = target / "deploy-backups" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    backup.mkdir(parents=True, exist_ok=False)
    rows = []
    for name, src, dst in paths:
        before = digest(dst) if dst.is_file() else None
        wanted = digest(src) if name in files else None
        if before == wanted: continue
        saved = backup / name
        if before:
            saved.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(dst, saved)
        if name in files:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
            if digest(dst) != wanted: raise RuntimeError("Deployment hash mismatch: " + name)
        elif before:
            # Preserve deleted source resources as a recoverable move, never recursive deletion.
            dst.replace(saved)
        rows.append({"path": name, "beforeSha256": before, "afterSha256": wanted})
    receipt = {"source": str(source), "target": str(target), "backup": str(backup), "changed": rows,
        "verifiedFileCount": len(files)}
    (backup / "deployment.json").write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding="utf-8")
    return receipt

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", type=Path, required=True)
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1]
    def git_names(*args):
        return subprocess.check_output(["git", "-C", str(source), *args, "-z"]).decode("utf-8").strip("\0").split("\0")
    removed = [p for p in git_names("diff", "--name-only", "--diff-filter=D", "HEAD") if p]
    files = [p for p in dict.fromkeys(git_names("ls-files", "--cached", "--others", "--exclude-standard")) if p and p not in removed]
    result = deploy(source, args.target, files, removed)
    print(json.dumps({"backup": result["backup"], "changedCount": len(result["changed"]),
        "verifiedFileCount": result["verifiedFileCount"]}, ensure_ascii=False, indent=2))
