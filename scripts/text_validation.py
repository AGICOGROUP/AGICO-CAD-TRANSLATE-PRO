"""Run the authoritative Core text validator without starting AutoCAD.

Set CAD_TRANSLATE_TEXTCLI to a published executable or DLL. Deployment can
also place the published files in ROOT/bin/text-validation. Source checkouts
fall back to one Release build, whose artifacts are reused by later calls.
Content failures are returned unchanged; execution failures raise RuntimeError.
"""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
NAME = "CadTranslation.TextCli"


def _run(command, timeout):
    try:
        return subprocess.run(command, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RuntimeError(f"Core text validation could not execute: {error}") from error


def _command(path):
    return ["dotnet", str(path)] if path.suffix.lower() == ".dll" else [str(path)]


def _resolve_command():
    override = os.environ.get("CAD_TRANSLATE_TEXTCLI")
    if override:
        path = Path(override).expanduser().resolve()
        if not path.is_file():
            raise RuntimeError(f"CAD_TRANSLATE_TEXTCLI does not name a file: {path}")
        return _command(path)

    project = ROOT / "src/cad" / NAME / f"{NAME}.csproj"
    release = project.parent / "bin/Release/net8.0"
    for directory in (ROOT / "bin/text-validation", release / "publish", release,
                      project.parent / "bin/Debug/net8.0"):
        for filename in (f"{NAME}.exe", NAME, f"{NAME}.dll"):
            path = directory / filename
            if path.is_file():
                return _command(path)

    if not project.is_file():
        raise RuntimeError("Core text validation CLI is missing. Set CAD_TRANSLATE_TEXTCLI to a published executable or DLL.")
    result = _run(["dotnet", "build", str(project), "--configuration", "Release", "--nologo", "--verbosity", "quiet"], 180)
    dll = release / f"{NAME}.dll"
    if result.returncode != 0 or not dll.is_file():
        raise RuntimeError(f"Core text validation build failed ({result.returncode}): {result.stdout}\n{result.stderr}")
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
