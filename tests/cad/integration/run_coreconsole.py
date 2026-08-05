"""Run deterministic CadTranslation commands through AutoCAD Core Console."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import winreg


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
AUTOCAD_CORE_CONSOLE = Path(r"C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe")
PLUGIN_DLL = (
    REPOSITORY_ROOT
    / "src"
    / "cad"
    / "CadTranslation.AutoCAD2025"
    / "bin"
    / "x64"
    / "Release"
    / "net8.0-windows"
    / "CadTranslation.AutoCAD2025.dll"
)
SCRIPT_TEMPLATE = REPOSITORY_ROOT / "tests" / "cad" / "scripts" / "load-and-run.scr"
COMMANDS = {
    "scan": "CADTRANS_SCAN",
    "export": "CADTRANS_EXPORT",
    "import": "CADTRANS_IMPORT",
    "verify": "CADTRANS_VERIFY",
    "compose": "CADTRANS_COMPOSE",
}


class CoreConsoleProfileError(RuntimeError):
    """The caller is not executing in an initialized AutoCAD user context."""


def ensure_autocad_profile() -> str:
    """Return an existing profile name without importing, creating, or changing profiles."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Autodesk\AutoCAD\R25.0") as release_key:
            product_key, _ = winreg.QueryValueEx(release_key, "CurVer")
        profiles_path = rf"Software\Autodesk\AutoCAD\R25.0\{product_key}\Profiles"
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, profiles_path) as profiles_key:
            try:
                profile_name, _ = winreg.QueryValueEx(profiles_key, "")
            except OSError:
                profile_name = winreg.EnumKey(profiles_key, 0)
        if not profile_name:
            raise OSError("No profile subkey exists.")
        return profile_name
    except OSError as error:
        raise CoreConsoleProfileError(
            "AutoCAD 2025 has no initialized profile in this process's HKCU. "
            "Run Core Console in the actual interactive AutoCAD user's context; do not create or alter a user profile."
        ) from error


def build_coreconsole_command(
    script_path: Path,
    profile_name: str = "<<unnamed-profile>>",
    drawing_path: Path | None = None,
) -> list[str]:
    """Use the installed product and active profile without requiring a language pack."""
    command = [
        str(AUTOCAD_CORE_CONSOLE),
        "/product",
        "ACAD",
        "/p",
        profile_name,
        "/nologo",
        "/nohardware",
        "/s",
        str(script_path),
    ]
    if drawing_path is not None:
        command.extend(["/i", str(drawing_path)])
    return command


def run_coreconsole(
    config_path: Path | None,
    drawing_path: Path,
    *,
    operation: str = "scan",
    diagnostic_directory: Path | None = None,
    timeout_seconds: int = 1800,
) -> subprocess.CompletedProcess[str]:
    """Load the release plugin and issue one allow-listed command without prompting."""
    if operation not in COMMANDS:
        raise ValueError(f"Unsupported CAD translation operation: {operation!r}.")
    if not AUTOCAD_CORE_CONSOLE.is_file():
        raise FileNotFoundError(f"AutoCAD Core Console is missing: {AUTOCAD_CORE_CONSOLE}")
    plugin_path = PLUGIN_DLL.resolve(strict=True)
    active_drawing_path = drawing_path.resolve(strict=True)
    profile_name = ensure_autocad_profile()
    if plugin_path.suffix.lower() != ".dll" or not plugin_path.is_relative_to(REPOSITORY_ROOT):
        raise ValueError(f"Unsafe plugin path: {plugin_path}")
    if any(character in str(plugin_path) for character in ('\r', '\n', '"')):
        raise ValueError("Plugin path cannot be represented safely in an AutoCAD script.")

    template = SCRIPT_TEMPLATE.read_text(encoding="utf-8")
    script_text = (
        template.replace("__CADTRANS_PLUGIN_DLL__", f'"{plugin_path}"')
        .replace("__CADTRANS_COMMAND__", COMMANDS[operation])
    )
    if "__CADTRANS_" in script_text:
        raise ValueError("Core Console script template has unresolved placeholders.")

    runner_directory = _create_runner_directory(config_path)
    try:
        script_path = runner_directory / "load-and-run.scr"
        script_path.write_text(script_text, encoding="utf-8", newline="\n")
        environment = os.environ.copy()
        if config_path is None:
            environment.pop("CADTRANS_JOB_CONFIG", None)
        else:
            environment["CADTRANS_JOB_CONFIG"] = str(config_path)
        diagnostics = diagnostic_directory or (runner_directory / "diagnostics")
        environment["CADTRANS_DIAGNOSTIC_DIRECTORY"] = str(diagnostics)

        process = subprocess.Popen(
            build_coreconsole_command(script_path, profile_name, active_drawing_path),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environment,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
        try:
            stdout, stderr = process.communicate(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            terminate_process_tree(process)
            stdout, stderr = process.communicate()
            message = _decode(stdout) + _decode(stderr) + _diagnostic_reference(diagnostics)
            log_path = _write_runner_log(runner_directory, "coreconsole-timeout.log", message)
            raise TimeoutError(f"Core Console timed out; process tree terminated; log={log_path}") from None

        decoded_stdout = _decode(stdout)
        decoded_stderr = _decode(stderr) + _diagnostic_reference(diagnostics)
        _write_runner_log(runner_directory, "coreconsole.log", decoded_stdout + decoded_stderr)
        return subprocess.CompletedProcess(process.args, process.returncode, decoded_stdout, decoded_stderr)
    finally:
        _remove_if_empty(runner_directory)


def terminate_process_tree(process: subprocess.Popen[bytes] | subprocess.Popen[str]) -> None:
    """Terminate the known process and its Windows child tree after a test timeout."""
    if process.poll() is not None:
        return
    subprocess.run(["taskkill", "/pid", str(process.pid), "/t", "/f"], check=False, capture_output=True)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        # A restrictive execution context can deny taskkill's tree traversal. The known parent
        # must still be stopped deterministically; the child PID is checked by the regression test.
        process.kill()
        process.wait(timeout=5)


def _decode(value: bytes | None) -> str:
    if not value:
        return ""
    if value.startswith((b"\xff\xfe", b"\xfe\xff")) or value.count(b"\x00") * 4 > len(value):
        return value.decode("utf-16", errors="replace")
    return value.decode("utf-8", errors="replace")


def _create_runner_directory(config_path: Path | None) -> Path:
    if config_path is not None and config_path.is_absolute() and config_path.parent.is_dir():
        root = config_path.parent / ".cadtrans-runner"
    else:
        root = Path(tempfile.gettempdir()) / "CadTranslation" / "runner"
    root.mkdir(parents=True, exist_ok=True)
    return Path(tempfile.mkdtemp(prefix="run-", dir=root))


def _write_runner_log(root: Path, name: str, content: str) -> Path:
    path = root / name
    path.write_text(content, encoding="utf-8")
    return path


def _diagnostic_reference(directory: Path) -> str:
    paths = sorted(directory.rglob("command-failure.json"))
    return "" if not paths else f"\nCADTRANS_DIAGNOSTIC={paths[-1]}\n"


def _remove_if_empty(directory: Path) -> None:
    try:
        directory.rmdir()
    except OSError:
        pass
