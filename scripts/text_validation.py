"""Run the authoritative Core text validator without starting AutoCAD.

Set CAD_TRANSLATE_TEXTCLI to a published executable or DLL. Deployment can
also place the published files in ROOT/bin/text-validation. Source checkouts
fall back to one Release build, whose artifacts are reused by later calls.

A cached binary is only reused while it is newer than every source file it was
built from. The validator is the authoritative preflight gate, so running a
stale copy would silently enforce rules that no longer exist in src/cad: any
older layout is skipped, rebuilt, and the deployment copy refreshed.

Content failures are returned unchanged; execution failures raise RuntimeError.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
NAME = "CadTranslation.TextCli"
SOURCE_ROOT = ROOT / "src" / "cad"
SOURCE_PATTERNS = ("*.cs", "*.csproj", "*.props", "*.targets")
# The validation rules live in Core, so Core's stamp decides whether a layout is current.
STAMP_FILES = ("CadTranslation.Core.dll", f"{NAME}.dll")


def _run(command, timeout):
    try:
        return subprocess.run(command, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RuntimeError(f"Core text validation could not execute: {error}") from error


def _command(path):
    return ["dotnet", str(path)] if path.suffix.lower() == ".dll" else [str(path)]


def _binary(directory):
    for filename in (f"{NAME}.exe", NAME, f"{NAME}.dll"):
        path = directory / filename
        if path.is_file():
            return path
    return None


def _newest_source():
    newest = 0.0
    for pattern in SOURCE_PATTERNS:
        for path in SOURCE_ROOT.rglob(pattern):
            if "obj" in path.parts or "bin" in path.parts:
                continue
            try:
                newest = max(newest, path.stat().st_mtime)
            except OSError:
                continue
    return newest


def _is_stale(directory):
    stamps = []
    for name in STAMP_FILES:
        path = directory / name
        if path.is_file():
            try:
                stamps.append(path.stat().st_mtime)
            except OSError:
                continue
    if not stamps:
        return True
    return _newest_source() > max(stamps)


def _build(project):
    result = _run(["dotnet", "build", str(project), "--configuration", "Release",
                   "--nologo", "--verbosity", "quiet"], 300)
    if result.returncode != 0:
        raise RuntimeError(
            f"Core text validation build failed ({result.returncode}): {result.stdout}\n{result.stderr}")


def _refresh_deployment(deployment, release):
    """Copy the fresh Release build over a deployed validator layout, keeping unrelated files."""
    deployment.mkdir(parents=True, exist_ok=True)
    for source in release.iterdir():
        if source.is_file() and source.suffix in (".dll", ".exe", ".json", ".pdb"):
            shutil.copy2(source, deployment / source.name)


def _resolve_command():
    override = os.environ.get("CAD_TRANSLATE_TEXTCLI")
    if override:
        path = Path(override).expanduser().resolve()
        if not path.is_file():
            raise RuntimeError(f"CAD_TRANSLATE_TEXTCLI does not name a file: {path}")
        return _command(path)

    project = SOURCE_ROOT / NAME / f"{NAME}.csproj"
    release = project.parent / "bin/Release/net8.0"
    deployment = ROOT / "bin/text-validation"
    for directory in (deployment, release / "publish", release, project.parent / "bin/Debug/net8.0"):
        path = _binary(directory)
        if path is None or _is_stale(directory):
            continue
        return _command(path)

    if not project.is_file():
        raise RuntimeError("Core text validation CLI is missing. Set CAD_TRANSLATE_TEXTCLI to a published executable or DLL.")
    _build(project)
    if deployment.is_dir():
        _refresh_deployment(deployment, release)
        path = _binary(deployment)
        if path is not None and not _is_stale(deployment):
            return _command(path)
    dll = release / f"{NAME}.dll"
    if not dll.is_file():
        raise RuntimeError(f"Core text validation produced no binary at {dll}")
    return _command(dll)


def validate_batch(manifest_path, translations_path):
    """Return Core's isValid/errors report, preserving every record diagnostic."""
    command = _resolve_command() + ["--manifest", str(Path(manifest_path).resolve()),
                                     "--translations", str(Path(translations_path).resolve())]
    result = _run(command, 60)
    try:
        report = json.loads(result.stdout)
    except (ValueError, TypeError) as error:
        raise RuntimeError(f"Core text validation returned invalid JSON ({result.returncode}): {result.stdout}\n{result.stderr}") from error
    if (not isinstance(report, dict) or type(report.get("isValid")) is not bool
            or not isinstance(report.get("errors"), list)):
        raise RuntimeError(f"Core text validation returned an invalid report: {report!r}")
    if result.returncode not in (0, 1):
        raise RuntimeError(f"Core text validation execution failed ({result.returncode}): {json.dumps(report, ensure_ascii=False)}\n{result.stderr}")
    if report["isValid"] != (result.returncode == 0):
        raise RuntimeError("Core text validation exit code disagrees with its report.")
    return report
