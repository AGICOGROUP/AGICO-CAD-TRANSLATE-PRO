#!/usr/bin/env python3
"""Guarded AutoCAD 2025 DWG/DXF translation runner (stdlib only)."""
from __future__ import annotations

import argparse, hashlib, json, locale, os, re, shutil, subprocess, sys, time
from pathlib import Path

AUTOCAD_2025 = Path(r"C:\Program Files\Autodesk\AutoCAD 2025")
SKILL_ROOT = Path(__file__).resolve().parents[1]
if str(SKILL_ROOT / "scripts") not in sys.path:
    sys.path.insert(0, str(SKILL_ROOT / "scripts"))
from pipelines import get_pipeline
from translation_work import direction, language, needs_translation, groups, job_groups, layout_hint
from job_timing import record as record_timing
from bilingual_work import enabled as inline_review_enabled, inline_candidates, reviewed_reuse
import text_validation
PLUGIN_DIR = SKILL_ROOT / "assets" / "plugin"
AUTOCAD_2025_PLUGIN_DIR = Path(r"C:\Program Files\Autodesk\ApplicationPlugins\CadTranslation2025.bundle\Contents\Windows")
AUTOCAD_2027_PLUGIN_DIR = Path(r"C:\Program Files\Autodesk\ApplicationPlugins\CadTranslation2027.bundle\Contents\Windows")
PLUGIN_FILES = ("CadTranslation.AutoCAD2025.dll", "CadTranslation.Core.dll", "CadTranslation.Contracts.dll")
# Presence of this file in a package directory is the only proof that the build ran a full drawing cycle
# on a real host of that release; compiling against reference assemblies is not acceptance.
ACCEPTANCE_MARKER = "HOST-ACCEPTED.json"
# Registry release key (R<major.minor>) / Release value to AutoCAD year. Shared-agent users install any
# release, so every host is classified instead of failing later inside NETLOAD.
AUTOCAD_RELEASE_YEARS = {
    "15.0": 2000, "15.6": 2002, "16.0": 2004, "16.1": 2005, "16.2": 2006,
    "17.0": 2007, "17.1": 2008, "17.2": 2009, "18.0": 2010, "18.1": 2011, "18.2": 2012,
    "19.0": 2013, "19.1": 2014, "20.0": 2015, "20.1": 2016, "21.0": 2017, "22.0": 2018,
    "23.0": 2019, "23.1": 2020, "24.0": 2021, "24.1": 2022, "24.2": 2023, "24.3": 2024,
    "25.0": 2025, "26.0": 2027,
}
# Years whose plugin build ships inside assets/plugin; 2027 is declared but built separately (.NET 10).
SHIPPED_PLUGIN_YEARS = frozenset({2025})
DECLARED_PLUGIN_YEARS = frozenset({2025, 2027})
CORE_CONSOLE_FIRST_YEAR = 2013  # accoreconsole.exe headless host ships from AutoCAD 2013
MANAGED_API_FIRST_YEAR = 2005   # ObjectARX .NET API ships from AutoCAD 2005
# Official support window: AutoCAD 2020-2025 only. Older releases are reported, never ported.
SUPPORTED_HOST_FIRST_YEAR = 2020
APPLICATION_PLUGINS_ROOT = Path(r"C:\Program Files\Autodesk\ApplicationPlugins")
YEAR_TO_RELEASE = {year: f"R{key}" for key, year in AUTOCAD_RELEASE_YEARS.items()}
RELEASE_TO_YEAR = {value: key for key, value in YEAR_TO_RELEASE.items()}
# Host years the plugin project targets through -p:CadHostFramework (see src/cad/Directory.Build.props).
BUILDABLE_LEGACY_FRAMEWORKS = {2020: "net47", 2021: "net48", 2022: "net48", 2023: "net48", 2024: "net48"}
TARGET_LANGUAGE_RESIDUE = re.compile(
    r"[\u2e80-\u2fff\u3000-\u303f\u31c0-\u31ef\u3400-\u4dbf"
    r"\u4e00-\u9fff\uf900-\ufaff\ufe10-\ufe1f\ufe30-\ufe4f"
    r"\uff01-\uff60\uffe0-\uffee\U00020000-\U0002fa1f"
    r"\U00030000-\U000323af]"
)
DIAGNOSTIC_EXAMPLE_LIMIT = 20
SUPPORTED_SOURCE_LANGUAGES = {"zh", "zh-cn", "zh-hans"}
SUPPORTED_TARGET_LANGUAGES = {"en", "en-us", "en-gb", "fr", "fr-fr", "fr-ca"}
DEFAULT_STAGE_TIMEOUT_SECONDS = {"export": 300, "import": 1800, "compose": 1800, "inspect": 600, "correct": 900}
OUTPUT_MODES = {"replace", "bilingual"}
LEGACY_OUTPUT_MODE_ALIASES = {"english": "replace"}

def absolute(value: str | Path) -> Path:
    return Path(value).expanduser().resolve()

def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1048576), b""):
            digest.update(block)
    return digest.hexdigest()

def host_runtime(year: int | None) -> str | None:
    """Managed runtime a plugin build must target for that AutoCAD year."""
    if year is None:
        return None
    if year < MANAGED_API_FIRST_YEAR:
        return "none (ObjectARX C++/VBA/COM only)"
    if year <= 2009:
        return ".NET Framework 2.0/3.5"
    if year <= 2012:
        return ".NET Framework 3.5"
    if year <= 2014:
        return ".NET Framework 4.0"
    if year <= 2019:
        return ".NET Framework 4.5-4.7"
    if year == 2020:
        return ".NET Framework 4.7"
    if year <= 2024:
        return ".NET Framework 4.8"
    if year == 2025:
        return ".NET 8"
    if year == 2027:
        return ".NET 10"
    return "unknown"

def packaged_plugin_dir(release: str) -> Path:
    """Where a loadable build for that release lives inside the skill package."""
    # R25.0 keeps the historical flat layout; every other release gets its own directory so a
    # per-release build can never be replaced by the .NET 8 (AutoCAD 2025) DLLs.
    return PLUGIN_DIR if release == "R25.0" else PLUGIN_DIR / release

def packaged_plugin_complete(directory: Path) -> bool:
    return directory.is_dir() and all((directory / name).is_file() for name in PLUGIN_FILES)

def packaged_plugin_accepted(directory: Path) -> bool:
    """True only when the acceptance marker covers the package present right now.

    A marker is written after a full drawing cycle passed. Checking that it merely exists would let a
    later rebuild inherit an acceptance it never earned, so the recorded artefact hashes must match.
    """
    marker = directory / ACCEPTANCE_MARKER
    if not marker.is_file():
        return False
    try:
        data = json.loads(marker.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False
    if not isinstance(data, dict):
        return False
    checks = (("pluginDllSha256", PLUGIN_FILES[0]), ("coreDllSha256", PLUGIN_FILES[1]))
    if not all((directory / name).is_file() for _, name in checks):
        return False
    return all(data.get(key) == sha256(directory / name) for key, name in checks)

def accepted_plugin_years() -> set[int]:
    years = {year for year in SHIPPED_PLUGIN_YEARS if packaged_plugin_accepted(PLUGIN_DIR)}
    if PLUGIN_DIR.is_dir():
        for entry in PLUGIN_DIR.iterdir():
            year = RELEASE_TO_YEAR.get(entry.name.upper()) if entry.is_dir() else None
            if year and packaged_plugin_complete(entry) and packaged_plugin_accepted(entry):
                years.add(year)
    return years

def shipped_plugin_years() -> set[int]:
    """Host years with a ready-to-deploy build: the flat 2025 package plus any per-release folder."""
    years = set(SHIPPED_PLUGIN_YEARS)
    if PLUGIN_DIR.is_dir():
        for entry in PLUGIN_DIR.iterdir():
            year = RELEASE_TO_YEAR.get(entry.name.upper()) if entry.is_dir() else None
            if year and packaged_plugin_complete(entry):
                years.add(year)
    return years

def application_plugins_bundle(year: int) -> Path:
    return APPLICATION_PLUGINS_ROOT / f"CadTranslation{year}.bundle" / "Contents" / "Windows"

def per_user_bundle(year: int) -> Path:
    """Per-user bundle location AutoCAD itself supports; the deployment fallback for standard users."""
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "Autodesk" / "ApplicationPlugins" / f"CadTranslation{year}.bundle" / "Contents" / "Windows"

def _dir_writable(directory: Path) -> bool:
    """Standard users hold only RX on Program Files, so probe the deepest existing ancestor."""
    probe = directory
    while not probe.exists():
        probe = probe.parent
    return os.access(probe, os.W_OK)

def deployable_bundle(year: int) -> Path:
    bundle = AUTOCAD_2025_PLUGIN_DIR if year == 2025 else application_plugins_bundle(year)
    if not _dir_writable(bundle):
        # NETLOAD takes an absolute path, so the DLLs only need a stable writable location;
        # a standard account cannot create directories under Program Files.
        return per_user_bundle(year)
    return bundle

def build_hint(year: int | None, root: str | None) -> dict[str, object] | None:
    """Exact build command for a host the package cannot serve yet, or why it cannot be built."""
    if year is None or year in shipped_plugin_years():
        return None
    release = YEAR_TO_RELEASE.get(year)
    if year < CORE_CONSOLE_FIRST_YEAR:
        return {"buildable": False, "hostFramework": None, "command": None, "packageDir": None,
                "blocker": f"AutoCAD {year} has no accoreconsole.exe; the headless pipeline needs a separate GUI-driven execution path"}
    if year >= 2025:
        framework, blocker = ("net8.0" if year <= 2026 else "net10.0"), None
    else:
        framework = BUILDABLE_LEGACY_FRAMEWORKS.get(year)
        blocker = None if framework else (
            f"AutoCAD {year} is outside the official support window "
            f"(AutoCAD {SUPPORTED_HOST_FIRST_YEAR}-2025); no plugin build is planned"
        )
    command = ["dotnet", "build", "src/cad/CadTranslation.AutoCAD2025/CadTranslation.AutoCAD2025.csproj",
               "-c", "Release", f"-p:AutoCADRelease={year}"]
    if framework:
        command.append(f"-p:CadHostFramework={framework}")
    if framework in ("net8.0", "net10.0"):
        command.append("-p:Platform=x64")
    # Legacy builds take reference assemblies from the official AutoCAD.NET NuGet packages, so a local
    # install of that release is optional; pass AutoCADDir only when the host really is installed here.
    if root:
        command.append(f'-p:AutoCADDir="{root}"')
    return {"buildable": bool(framework), "hostFramework": framework, "command": " ".join(command),
            "packageDir": f"assets/plugin/{release}/" if release else None, "blocker": blocker,
            "note": "copy the build output DLLs into packageDir; the runner then deploys them by content hash"}

def host_support(year: int | None, core_console: bool, packaged: bool = False) -> tuple[str, list[str]]:
    """Classify a host: supported | build-required | port-required | out-of-window | unsupported."""
    if year is None:
        return "unknown", ["AutoCAD year could not be derived from the registry release or install path"]
    if year < MANAGED_API_FIRST_YEAR:
        return "unsupported", [f"AutoCAD {year} exposes no .NET API; the packaged engine cannot load in it"]
    reasons: list[str] = []
    if not core_console:
        reasons.append(f"no accoreconsole.exe; the headless host ships from AutoCAD {CORE_CONSOLE_FIRST_YEAR}")
    if year < CORE_CONSOLE_FIRST_YEAR:
        return "unsupported", reasons + [f"AutoCAD {year} needs the separate GUI-driven execution path (not implemented)"]
    if packaged or year in SHIPPED_PLUGIN_YEARS:
        return "supported", reasons
    if year < SUPPORTED_HOST_FIRST_YEAR:
        return "out-of-window", reasons + [
            f"AutoCAD {year} is outside the official support window (AutoCAD {SUPPORTED_HOST_FIRST_YEAR}-2025); "
            "no plugin build is planned - use a 2020-2025 host or a central execution machine",
        ]
    if year in DECLARED_PLUGIN_YEARS:
        return "build-required", reasons + [f"AutoCAD {year} needs its own {host_runtime(year)} plugin build deployed to its ApplicationPlugins bundle"]
    return "port-required", reasons + [
        f"AutoCAD {year} runs on {host_runtime(year)}; no build packaged in assets/plugin/{YEAR_TO_RELEASE.get(year, 'R?')}/",
    ]

def registry_hosts() -> list[dict[str, object]]:
    """Read-only inventory of every installed Autodesk AutoCAD release and product."""
    hosts: list[dict[str, object]] = []
    if os.name != "nt":
        return hosts
    try:
        import winreg
    except ImportError:
        return hosts
    seen: set[str] = set()
    for hive_name in ("HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER"):
        hive = getattr(winreg, hive_name, None)
        if hive is None:
            continue
        try:
            with winreg.OpenKey(hive, r"SOFTWARE\Autodesk\AutoCAD") as releases:
                release_names = [winreg.EnumKey(releases, index) for index in range(winreg.QueryInfoKey(releases)[0])]
        except (OSError, AttributeError):
            continue
        for release_name in release_names:
            if not re.fullmatch(r"R\d+(?:\.\d+)?", release_name, re.IGNORECASE):
                continue
            release_path = rf"SOFTWARE\Autodesk\AutoCAD\{release_name}"
            year = AUTOCAD_RELEASE_YEARS.get(release_name[1:].lower()) or AUTOCAD_RELEASE_YEARS.get(release_name[1:])
            try:
                with winreg.OpenKey(hive, release_path) as products:
                    product_names = [winreg.EnumKey(products, index) for index in range(winreg.QueryInfoKey(products)[0])]
            except (OSError, AttributeError):
                continue
            for product_name in product_names:
                if product_name == "InstalledProducts":
                    continue
                values: dict[str, str] = {}
                try:
                    with winreg.OpenKey(hive, rf"{release_path}\{product_name}") as product:
                        for index in range(winreg.QueryInfoKey(product)[1]):
                            name, value, _ = winreg.EnumValue(product, index)
                            if isinstance(value, str) and value.strip():
                                values[str(name)] = value.strip()
                except (OSError, AttributeError):
                    continue
                location = values.get("AcadLocation") or values.get("Location")
                if not location:
                    continue
                root = Path(location.rstrip("\\/"))
                key = str(root).lower()
                if key in seen:
                    continue
                seen.add(key)
                release_value = values.get("Release", "")
                year_from_value = None
                match = re.match(r"(\d+\.\d+)", release_value)
                if match:
                    year_from_value = AUTOCAD_RELEASE_YEARS.get(match.group(1))
                hosts.append({
                    "release": release_name.upper(),
                    "productKey": product_name,
                    "root": str(root),
                    "releaseValue": release_value or None,
                    "productName": values.get("ProductName"),
                    "year": year_from_value or year,
                    "coreConsoleExists": (root / "accoreconsole.exe").is_file(),
                    "hive": hive_name,
                })
    return hosts

def host_compatibility(autocad_root: Path | None = None) -> dict[str, object]:
    """Report what the installed host can and cannot run, without touching the registry write side."""
    root = absolute(autocad_root) if autocad_root is not None else None
    entry: dict[str, object] | None = None
    hosts = registry_hosts()
    if root is not None:
        for host in hosts:
            if str(absolute(host["root"])).lower() == str(root).lower():
                entry = host
                break
    if entry is None:
        year = None
        match = re.search(r"AutoCAD\s+(\d{4})", str(root) if root else "", re.IGNORECASE)
        if match:
            year = int(match.group(1))
        entry = {
            "release": None,
            "productKey": None,
            "root": str(root) if root else None,
            "releaseValue": None,
            "productName": None,
            "year": year,
            "coreConsoleExists": bool(root and (root / "accoreconsole.exe").is_file()),
            "hive": None,
        }
    year = entry["year"] if isinstance(entry["year"], int) else None
    release = entry["release"] or (YEAR_TO_RELEASE.get(year) if year else None)
    packaged = bool(year and year in shipped_plugin_years())
    accepted = bool(year and year in accepted_plugin_years())
    support, reasons = host_support(year, bool(entry["coreConsoleExists"]), packaged)
    if root is not None and not Path(str(entry["root"])).exists():
        reasons = ["install root does not exist"] + reasons
    warnings: list[str] = []
    if packaged and not accepted:
        warnings.append(
            f"the AutoCAD {year} package is compile-verified only: no full drawing cycle has passed on a "
            f"real AutoCAD {year} host, so treat its first run as unverified"
        )
    return {
        "root": entry["root"],
        "year": year,
        "release": release,
        "releaseValue": entry["releaseValue"],
        "productKey": entry["productKey"],
        "productName": entry["productName"],
        "coreConsoleExists": entry["coreConsoleExists"],
        "pluginRuntime": host_runtime(year) if year else None,
        "packagedPluginRuntime": host_runtime(year) if packaged else None,
        "packagedPluginAvailable": packaged,
        "packagedPluginAccepted": accepted,
        "packagedPluginDir": str(packaged_plugin_dir(release)) if release else None,
        "support": support,
        "reasons": reasons,
        "warnings": warnings,
        "buildHint": build_hint(year, entry["root"] if isinstance(entry["root"], str) else None),
    }

def host_compat_report() -> dict[str, object]:
    """Whole-machine inventory plus the host this runner would pick."""
    hosts = registry_hosts()
    packaged_years = shipped_plugin_years()
    accepted_years = accepted_plugin_years()
    detailed = []
    for host in hosts:
        year = host["year"] if isinstance(host["year"], int) else None
        release = host["release"] or (YEAR_TO_RELEASE.get(year) if year else None)
        packaged = bool(year and year in packaged_years)
        support, reasons = host_support(year, bool(host["coreConsoleExists"]), packaged)
        detailed.append({**host, "pluginRuntime": host_runtime(year) if year else None,
                         "packagedPluginDir": str(packaged_plugin_dir(release)) if release else None,
                         "packagedPluginAvailable": packaged,
                         "packagedPluginAccepted": bool(year and year in accepted_years),
                         "support": support, "reasons": reasons,
                         "buildHint": build_hint(year, host["root"] if isinstance(host["root"], str) else None)})
    chosen = discover_autocad()
    selected = host_compatibility(chosen)
    return {
        "status": "ready" if selected["support"] == "supported" else "blocked",
        "selectedRoot": str(chosen),
        "selectedHost": selected,
        "warnings": selected.get("warnings") or [],
        "installedHosts": detailed,
        "packagedPluginYears": sorted(packaged_years),
        "acceptedPluginYears": sorted(accepted_years),
        "declaredPluginYears": sorted(DECLARED_PLUGIN_YEARS),
    }

def autocad_release(autocad_root: Path) -> str:
    match = re.search(r"AutoCAD\s+(\d{4})", str(autocad_root), re.IGNORECASE)
    year = int(match.group(1)) if match else 2025
    packaged_years = shipped_plugin_years()
    if year in DECLARED_PLUGIN_YEARS or year in packaged_years:
        return YEAR_TO_RELEASE[year]
    support, reasons = host_support(year, (absolute(autocad_root) / "accoreconsole.exe").is_file(), year in packaged_years)
    detail = "; ".join(reasons) or "no packaged plugin build"
    hint = build_hint(year, str(absolute(autocad_root)))
    remedy = (hint or {}).get("command") or (hint or {}).get("blocker") or "no build path for this release"
    supported = ", ".join(str(value) for value in sorted(packaged_years | DECLARED_PLUGIN_YEARS))
    raise ValueError(
        f"Unsupported AutoCAD host {year} at {autocad_root} [{support}]: {detail}. "
        f"Usable plugin years: {supported}. Next step: {remedy}. Run `host-compat` for the full inventory."
    )

def runtime_plugin_dir(autocad_root: Path) -> Path:
    if os.environ.get("CAD_TRANSLATE_PLUGIN_DIR"):
        return absolute(os.environ["CAD_TRANSLATE_PLUGIN_DIR"])
    release = autocad_release(autocad_root)
    if release == "R26.0":
        return AUTOCAD_2027_PLUGIN_DIR  # Separately built .NET 10 runtime.
    # Select the package shipped with this checkout, never a stale global DLL.
    source = packaged_plugin_dir(release)
    fingerprints = ''.join(sha256(source / name) for name in PLUGIN_FILES)
    version = hashlib.sha256(fingerprints.encode()).hexdigest()[:20]
    year = RELEASE_TO_YEAR[release]
    bundle = deployable_bundle(year)
    return bundle / ('package-' + version)

def ensure_runtime_plugin(autocad_root: Path) -> Path:
    release = autocad_release(autocad_root)
    directory = runtime_plugin_dir(autocad_root)
    if not os.environ.get("CAD_TRANSLATE_PLUGIN_DIR") and release != "R26.0":
        source_dir = packaged_plugin_dir(release)
        if not packaged_plugin_complete(source_dir):
            raise RuntimeError(
                f'No packaged plugin for AutoCAD release {release}: expected {source_dir}. '
                'Run `host-compat` for the build command that produces it.'
            )
        directory.mkdir(parents=True, exist_ok=True)
        # net4x packages carry their own System.Text.Json closure (nine extra assemblies), so deploy
        # every file in the package instead of only the three plugin assemblies; each copy is verified.
        for source in sorted(path for path in source_dir.iterdir() if path.is_file()):
            target = directory / source.name
            if not target.is_file() or sha256(target) != sha256(source):
                shutil.copy2(source, target)
            if sha256(target) != sha256(source):
                raise RuntimeError('Runtime deployment hash mismatch: ' + source.name)
    if any(not (directory / name).is_file() for name in PLUGIN_FILES):
        raise RuntimeError('Incomplete CAD runtime: ' + str(directory))
    return directory

def profile(release: str = "R25.0") -> dict[str, object]:
    if not re.fullmatch(r"R\d+(?:\.\d+)?", release or ""):
        raise ValueError(f"Unsupported AutoCAD registry release: {release}")
    report: dict[str, object] = {"initialized": False, "name": None, "writable": False, "release": release}
    if os.name != "nt":
        report["detail"] = "Windows registry unavailable"
        return report
    try:
        import winreg
        release_path = rf"Software\Autodesk\AutoCAD\{release}"
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, release_path) as release_key:
            product, _ = winreg.QueryValueEx(release_key, "CurVer")
        product_path = rf"{release_path}\{product}"
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, product_path + r"\Profiles") as profiles:
            try:
                name, _ = winreg.QueryValueEx(profiles, "")
                selection = "current"
            except OSError:
                name = winreg.EnumKey(profiles, 0)
                selection = "fallback-first"
        report.update(
            initialized=bool(name),
            name=name,
            selection=selection,
            detail="read-only profile preflight",
        )
    except (OSError, ImportError) as error:
        report["detail"] = f"No initialized profile: {error}"
    return report

def doctor(source: Path | None = None, autocad_root: Path = AUTOCAD_2025) -> dict[str, object]:
    """Read-only check: never create paths or start AutoCAD."""
    root = absolute(autocad_root)
    source_report = {"path": None, "exists": None, "extensionSupported": None}
    if source is not None:
        source = absolute(source)
        source_report = {"path": str(source), "exists": source.is_file(), "extensionSupported": source.suffix.lower() in (".dwg", ".dxf")}
    compatibility = host_compatibility(root)
    console_exists = bool(root / "accoreconsole.exe") and (root / "accoreconsole.exe").is_file()
    try:
        plugin_dir: Path | None = runtime_plugin_dir(root)
        files = {name: (plugin_dir / name).is_file() for name in PLUGIN_FILES}
        preflight = profile(autocad_release(root))
        host_error: str | None = None
    except ValueError as error:
        plugin_dir, files, host_error = None, {}, str(error)
        preflight = {"initialized": False, "name": None, "writable": False, "release": None, "detail": host_error}
    blockers: list[str] = list(compatibility["reasons"])
    if host_error and host_error not in blockers:
        blockers.append(host_error)
    if not console_exists:
        blockers.append(f"accoreconsole.exe not found in {root}")
    if host_error is None:
        if not all(files.values()):
            blockers.append(f"packaged plugin DLLs missing from {plugin_dir}")
        if not preflight["initialized"]:
            blockers.append(f"no initialized AutoCAD profile: {preflight.get('detail')}")
    ready = host_error is None and compatibility["support"] == "supported" and console_exists \
        and all(files.values()) and bool(preflight["initialized"])
    if source is not None:
        ready = ready and bool(source_report["exists"]) and bool(source_report["extensionSupported"])
    seen: set[str] = set()
    return {"status": "ready" if ready else "blocked", "autocad": {"root": str(root), "coreConsoleExists": console_exists}, "hostCompatibility": compatibility, "warnings": list(compatibility.get("warnings") or []), "blockers": [item for item in blockers if not (item in seen or seen.add(item))], "plugin": {"directory": str(plugin_dir) if plugin_dir else None, "files": files}, "profile": preflight, "source": source_report}

def assert_source(source: Path) -> None:
    if not source.is_file() or source.suffix.lower() not in (".dwg", ".dxf"):
        raise ValueError("Source must be an existing .dwg or .dxf file.")

def validate_language_direction(source_language: str, target_language: str) -> None:
    if language(source_language) == language(target_language):
        raise ValueError("Source and target languages must differ.")


def normalize_output_mode(mode: str) -> str:
    normalized = LEGACY_OUTPUT_MODE_ALIASES.get(mode.strip().lower(), mode.strip().lower())
    if normalized not in OUTPUT_MODES:
        raise ValueError(f"Unsupported output mode: {mode}")
    return normalized

def write_output_mode(job: Path, mode: str) -> Path:
    mode = normalize_output_mode(mode)
    target = absolute(job) / "config" / "output-mode.json"
    target.write_text(json.dumps({"schemaVersion": "1.0", "outputMode": mode}, indent=2), encoding="utf-8")
    return target

def read_output_mode(job: Path) -> str:
    target = absolute(job) / "config" / "output-mode.json"
    if not target.is_file():
        return "replace"
    mode = str(json.loads(target.read_text(encoding="utf-8")).get("outputMode", ""))
    return normalize_output_mode(mode)

def prepare_export_job(source: Path, job: Path, source_language: str, target_language: str, output_mode: str = "replace") -> dict[str, object]:
    source, job = absolute(source), absolute(job)
    assert_source(source)
    validate_language_direction(source_language, target_language)
    if job.exists():
        raise FileExistsError(f"Refusing to reuse job directory: {job}")
    for name in ("working", "config", "exchange", "artifacts", "results"):
        (job / name).mkdir(parents=True, exist_ok=True)
    working = job / "working" / f"source{source.suffix.lower()}"
    shutil.copy2(source, working)
    output_mode = normalize_output_mode(output_mode)
    config: dict[str, object] = {"schemaVersion": "1.0", "jobId": job.name, "operation": "export", "sourcePath": str(source), "workingPath": str(working), "sourceSha256": sha256(source), "manifestPath": str(job / "exchange" / "manifest.input.jsonl"), "translationPath": None, "outputPath": str(job / "results" / f"candidate{source.suffix.lower()}"), "resultPath": str(job / "artifacts" / "export-result.json"), "artifactDirectory": str(job / "artifacts"), "sourceLanguage": source_language, "targetLanguage": target_language, "outputMode": output_mode, "pipelineVersion": "2.0"}
    (job / "config" / "export-job.json").write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")
    write_output_mode(job, output_mode)
    return config

def _read_jsonl(path: Path) -> list[dict[str, object]]:
    return [
        json.loads(line)
        for line in path.read_text(encoding="utf-8-sig").splitlines()
        if line.strip()
    ]

def _atomic_write_jsonl(path: Path, records: list[dict[str, object]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        "".join(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n" for record in records),
        encoding="utf-8",
    )
    os.replace(temporary, path)

def _id_diagnostic(label: str, values: list[str]) -> str:
    return f"{label}_count={len(values)}, {label}_examples={values[:DIAGNOSTIC_EXAMPLE_LIMIT]}"

def prepare_translation_worklist(job: Path, max_source_chars: int = 6000, existing_inline_handles: str | None = None) -> dict[str, object]:
    """Create model-facing batches containing only record IDs and CJK source text."""
    job = absolute(job)
    if max_source_chars < 1:
        raise ValueError("max_source_chars must be positive")
    manifest_path = job / "exchange" / "manifest.input.jsonl"
    manifest = _read_jsonl(manifest_path)
    candidates = inline_candidates(manifest) if inline_review_enabled(job) else []
    if inline_review_enabled(job):
        _atomic_write_jsonl(job / "exchange/bilingual-inline-candidates.jsonl", candidates)
    if existing_inline_handles is not None:
        selected = {h.strip().upper() for h in existing_inline_handles.split(",") if h.strip()}
        by_handle = {r["handle"].upper(): r for r in candidates}
        if not inline_review_enabled(job) or selected - by_handle.keys():
            raise ValueError("Reviewed inline handles must be current bilingual candidates")
        config = json.loads((job / "config/export-job.json").read_text(encoding="utf-8"))
        receipt = {"sourceSha256": config["sourceSha256"], "records": [
            {k: by_handle[h][k] for k in ("recordId", "inputHash")} for h in sorted(selected)]}
        (job / "exchange/bilingual-inline-review.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    reused = reviewed_reuse(manifest, job)
    source_language, _ = direction(job)
    grouped = job_groups(manifest, source_language, job)
    work_records = [
        {"recordId": str(rows[0]["recordId"]), "sourceText": str(rows[0].get("plainText", "")),
         "protectedTokens": rows[0].get("protectedTokens", []),
         "occurrences": len(rows), "context": layout_hint(rows[0]),
         **({"termMemberIds": rows[0]["termMemberIds"], "translationUnit": "complete-term; one nearby target for all source members"}
            if "termMemberIds" in rows[0] else {}),
         "contextVariants": list({json.dumps(layout_hint(row), sort_keys=True): layout_hint(row) for row in rows}.values())[:4]}
        for rows in grouped
    ]
    for item in work_records:
        if item["contextVariants"] == [item["context"]]:
            del item["contextVariants"]
    requested_count = sum(len(rows) for rows in grouped)
    batches: list[list[dict[str, object]]] = []
    current: list[dict[str, object]] = []
    current_chars = 0
    for record in work_records:
        source_chars = len(str(record["sourceText"]))
        if current and current_chars + source_chars > max_source_chars:
            batches.append(current)
            current, current_chars = [], 0
        current.append(record)
        current_chars += source_chars
    if current:
        batches.append(current)

    worklist_dir = job / "exchange" / "translation-worklist"
    worklist_dir.mkdir(parents=True, exist_ok=True)
    for stale in worklist_dir.glob("part-*.jsonl"):
        stale.unlink()
    for index, batch in enumerate(batches, start=1):
        _atomic_write_jsonl(worklist_dir / f"part-{index:04d}.jsonl", batch)

    summary: dict[str, object] = {
        "schemaVersion": "1.0",
        "status": "ready",
        "manifestRecordCount": len(manifest),
        "translationRecordCount": len(work_records),
        "passthroughRecordCount": len(manifest) - requested_count - len(reused),
        "existingInlineCandidateCount": len(candidates),
        "reviewedExistingInlineCount": len(reused),
        "sourceEntityCount": requested_count,
        "deduplicatedRecordCount": requested_count - len(work_records),
        "sourceCharacterCount": sum(len(str(record["sourceText"])) for record in work_records),
        "batchCount": len(batches),
        "maxSourceCharactersPerBatch": max_source_chars,
        "worklistDirectory": str(worklist_dir),
    }
    summary_path = job / "artifacts" / "translation-worklist-summary.json"
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    record_timing(job, "worklist-ready")
    return summary

def assemble_translations(job: Path, translated: Path) -> dict[str, object]:
    """Merge compact model results with deterministic passthrough records."""
    job, translated = absolute(job), absolute(translated)
    manifest_path = job / "exchange" / "manifest.input.jsonl"
    manifest = _read_jsonl(manifest_path)
    reused = reviewed_reuse(manifest, job)
    sources = translated.glob("*.jsonl") if translated.is_dir() else [translated]
    compact_records: list[dict[str, object]] = []
    for path in sorted(sources):
        compact_records.extend(_read_jsonl(path))

    received: dict[str, str] = {}
    duplicates: list[str] = []
    for record in compact_records:
        record_id = str(record.get("recordId", ""))
        translated_text = record.get("translatedText")
        if record_id in received:
            duplicates.append(record_id)
        if not isinstance(translated_text, str):
            raise ValueError(f"compact translation missing translatedText: {record_id}")
        received[record_id] = translated_text
    if duplicates:
        raise ValueError(_id_diagnostic("duplicate", sorted(set(duplicates))))

    source_language, _ = direction(job)
    grouped = job_groups(manifest, source_language, job)
    expected = {str(row["recordId"]) for rows in grouped for row in rows}
    for rows in grouped:
        representative = str(rows[0]["recordId"])
        if representative in received:
            for row in rows:
                received.setdefault(str(row["recordId"]), received[representative])
    missing = sorted(expected - set(received))
    extra = sorted(set(received) - expected)
    if missing or extra:
        raise ValueError(
            "compact translations incomplete: "
            + _id_diagnostic("missing", missing)
            + "; "
            + _id_diagnostic("unexpected", extra)
        )

    complete: list[dict[str, object]] = []
    for source in manifest:
        record_id = str(source["recordId"])
        needs_translation = record_id in expected
        complete.append(
            {
                "schemaVersion": "1.0",
                "recordId": record_id,
                "inputHash": str(source["inputHash"]),
                "translatedText": received[record_id] if needs_translation else reused.get(record_id, str(source.get("plainText", ""))),
                "reviewStatus": "approved",
                "reason": "model translation" if needs_translation else ("reviewed existing inline bilingual" if record_id in reused else "identity: source contains no Chinese"),
            }
        )

    target = job / "exchange" / "translations.output.jsonl"
    temporary = target.with_suffix(".jsonl.assembling")
    _atomic_write_jsonl(temporary, complete)
    try:
        report = check_translations(
            manifest_path,
            temporary,
            job / "artifacts" / "assembled-translation-check.json",
            read_output_mode(job),
        )
        if report["status"] != "passed":
            raise ValueError(
                "assembled translation quality gate failed: "
                f"chinese_residual_count={report['chineseResidualCount']}, "
                f"invalid_translation_count={report['invalidTranslationCount']}"
            )
        os.replace(temporary, target)
        record_timing(job, "translations-ready")
    finally:
        if temporary.exists():
            temporary.unlink()
    return {
        "schemaVersion": "1.0",
        "status": "passed",
        "records": len(complete),
        "translatedSourceRecords": len(expected),
        "passthroughRecords": len(complete) - len(expected) - len(reused),
        "reviewedExistingInlineCount": len(reused),
        "output": str(target),
    }

def validate_complete_translations(manifest_path: Path, translations_path: Path) -> int:
    manifest = [json.loads(line) for line in manifest_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    translations = [json.loads(line) for line in translations_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    expected = {str(record["recordId"]): str(record["inputHash"]) for record in manifest}
    received: dict[str, str] = {}
    for record in translations:
        record_id = str(record.get("recordId", ""))
        if record_id in received: raise ValueError(f"translations duplicate recordId: {record_id}")
        if not isinstance(record.get("translatedText"), str): raise ValueError(f"translations missing translatedText: {record_id}")
        if record.get("schemaVersion") != "1.0": raise ValueError(f"translations schemaVersion must be 1.0: {record_id}")
        if record.get("reviewStatus") not in ("approved", "manual-review"): raise ValueError(f"translations reviewStatus must be approved or manual-review: {record_id}")
        if not isinstance(record.get("reason"), str): raise ValueError(f"translations reason must be a string: {record_id}")
        received[record_id] = str(record.get("inputHash", ""))
    missing, extra = sorted(set(expected) - set(received)), sorted(set(received) - set(expected))
    if missing or extra:
        raise ValueError(
            "translations must be complete: "
            + _id_diagnostic("missing", missing)
            + "; "
            + _id_diagnostic("unexpected", extra)
        )
    stale = sorted(key for key, value in expected.items() if received[key] != value)
    if stale: raise ValueError("translations inputHash mismatch: " + _id_diagnostic("stale", stale))
    manifest_by_id = {str(record["recordId"]): record for record in manifest}
    marker_errors = []
    for translation in translations:
        manifest_record = manifest_by_id[str(translation["recordId"])]
        token_objects = manifest_record.get("protectedTokens", [])
        if not isinstance(token_objects, list): raise ValueError(f"manifest protectedTokens must be a list: {translation['recordId']}")
        markers = []
        for token in token_objects:
            marker = token.get("marker") if isinstance(token, dict) else None
            if not isinstance(marker, str) or not marker: raise ValueError(f"manifest protected marker is invalid: {translation['recordId']}")
            markers.append(marker)
        actual = re.findall(r"⟦P\d{4}⟧", translation["translatedText"])
        if actual != markers:
            marker_errors.append({"recordId": translation["recordId"],
                "sourceText": manifest_record.get("plainText", ""),
                "translatedText": translation["translatedText"], "expectedMarkers": markers,
                "actualMarkers": actual, "protectedTokens": token_objects})
    if marker_errors:
        repair = manifest_path.parent.parent / "artifacts" / "translation-marker-repairs.jsonl"
        _atomic_write_jsonl(repair, marker_errors)
        raise ValueError(f"protected marker mismatch: count={len(marker_errors)}, repair={repair}")
    return len(expected)

def check_translations(manifest_path: Path, translations_path: Path, report_path: Path | None = None, output_mode: str = "replace") -> dict[str, object]:
    """Reject incomplete language conversion before AutoCAD is started."""
    output_mode = normalize_output_mode(output_mode)
    return get_pipeline(output_mode).check_translations(
        manifest_path, translations_path, report_path, validate_complete_translations
    )

def check_exported_candidate_language(manifest_path: Path, report_path: Path, output_mode: str = "replace") -> dict[str, object]:
    output_mode = normalize_output_mode(output_mode)
    return get_pipeline(output_mode).check_candidate(manifest_path, report_path)

def _build_visual_review_targets(layout: dict[str, object], logical: dict[str, object]) -> list[dict[str, object]]:
    grouped: dict[tuple[str, str], dict[str, object]] = {}

    def add(item: dict[str, object], reason: str, direct_bounds: bool = False) -> None:
        bounds = item if direct_bounds else item.get("candidateWorldBounds")
        if not isinstance(bounds, dict):
            return
        required = ("left", "bottom", "right", "top")
        if not all(isinstance(bounds.get(key), (int, float)) for key in required):
            return
        definition = str(item.get("definitionName", "*Model_Space"))
        region = str(item.get("regionId", item.get("recordId", "unassigned")))
        key = (definition, region)
        target = grouped.setdefault(
            key,
            {
                "definitionName": definition,
                "regionId": region,
                "window": {key: float(bounds[key]) for key in required},
                "reasons": [],
                "itemCount": 0,
            },
        )
        window = target["window"]
        window["left"] = min(window["left"], float(bounds["left"]))
        window["bottom"] = min(window["bottom"], float(bounds["bottom"]))
        window["right"] = max(window["right"], float(bounds["right"]))
        window["top"] = max(window["top"], float(bounds["top"]))
        if reason not in target["reasons"]:
            target["reasons"].append(reason)
        target["itemCount"] += 1

    risks = layout.get("risks", []) if isinstance(layout.get("risks", []), list) else []
    for risk in risks:
        if isinstance(risk, dict) and str(risk.get("level", "")).lower() == "high":
            add(risk, str(risk.get("code", "high-risk")))
    rows = logical.get("rows", []) if isinstance(logical.get("rows", []), list) else []
    for row in rows:
        if isinstance(row, dict) and not str(row.get("kind", "")).endswith("-preserved"):
            add(row, "composed", direct_bounds=True)
    return list(grouped.values())

def _segment_overflow_count(logical: dict[str, object]) -> int:
    rows = logical.get("rows", []) if isinstance(logical.get("rows", []), list) else []
    return sum(
        1
        for row in rows
        if isinstance(row, dict)
        and not str(row.get("kind", "")).endswith("-preserved")
        and isinstance(row.get("actualHeight"), (int, float))
        and isinstance(row.get("availableHeight"), (int, float))
        and float(row["actualHeight"]) > float(row["availableHeight"]) + 1e-6
    )

def summarize_audit(job: Path) -> dict[str, object]:
    """Reduce large machine reports to bounded model-facing counts."""
    job = absolute(job)
    config_path = job / "config" / "export-job.json"
    if config_path.is_file() and json.loads(config_path.read_text(encoding="utf-8")).get("pipelineVersion") == "2.0":
        summary = get_pipeline(read_output_mode(job)).summarize(job)
        (job / "artifacts" / "audit-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        return summary
    artifacts = job / "artifacts"
    output_mode = read_output_mode(job)
    layout_path = artifacts / f"{output_mode}-layout-audit.json"
    logical_path = artifacts / "logical-flow-report.json"
    language_path = artifacts / "postcomposition-language-check.json"
    if not language_path.is_file():
        language_path = artifacts / "preimport-language-check.json"

    layout = json.loads(layout_path.read_text(encoding="utf-8")) if layout_path.is_file() else {}
    logical = json.loads(logical_path.read_text(encoding="utf-8")) if logical_path.is_file() else {}
    language = json.loads(language_path.read_text(encoding="utf-8")) if language_path.is_file() else {}
    missing_blocks = [str(value) for value in layout.get("missingBlockInstancePaths", [])]
    rows = logical.get("rows", []) if isinstance(logical.get("rows", []), list) else []
    overflow_count = _segment_overflow_count(logical)
    risk_counts = layout.get("riskCounts", {}) if isinstance(layout.get("riskCounts", {}), dict) else {}
    visual_targets = _build_visual_review_targets(layout, logical)
    visual_targets_path = artifacts / "visual-review-targets.json"
    visual_targets_path.write_text(
        json.dumps({"schemaVersion": "1.0", "targets": visual_targets}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    summary: dict[str, object] = {
        "schemaVersion": "1.0",
        "layout": {
            "present": layout_path.is_file(),
            "textCount": len(layout.get("texts", [])) if isinstance(layout.get("texts", []), list) else 0,
            "riskCount": len(layout.get("risks", [])) if isinstance(layout.get("risks", []), list) else 0,
            "riskCounts": {
                "low": int(risk_counts.get("low", 0)),
                "medium": int(risk_counts.get("medium", 0)),
                "high": int(risk_counts.get("high", 0)),
            },
            "manualReviewCount": len(layout.get("manualReview", [])) if isinstance(layout.get("manualReview", []), list) else 0,
            "noteColumnCount": int(layout.get("noteColumnCount", 0)),
            "tableCellCount": int(layout.get("tableCellCount", 0)),
            "missingBlockInstanceCount": len(missing_blocks),
            "missingBlockInstanceExamples": missing_blocks[:DIAGNOSTIC_EXAMPLE_LIMIT],
        },
        "logicalFlow": {
            "present": logical_path.is_file(),
            "replacedRecords": int(logical.get("replacedRecords", 0)),
            "composedObjects": int(logical.get("composedObjects", 0)),
            "segmentCount": len(rows),
            "segmentOverflowCount": overflow_count,
        },
        "language": {
            "present": language_path.is_file(),
            "status": language.get("status"),
            "outputMode": language.get("outputMode", "replace"),
            "chineseResidualCount": int(
                language.get(
                    "chineseResidualCount",
                    int(language.get("plainTextChineseResidualCount", 0))
                    + int(language.get("rawTextChineseResidualCount", 0)),
                )
            ),
            "invalidTranslationCount": int(language.get("invalidTranslationCount", 0)),
        },
        "visualReviewTargets": {
            "count": len(visual_targets),
            "path": str(visual_targets_path),
        },
    }
    summary["requiresVisualReview"] = bool(
        visual_targets
        or summary["logicalFlow"]["composedObjects"]
        or summary["layout"]["riskCounts"]["high"]
        or summary["layout"]["manualReviewCount"]
    )
    gate_errors: list[str] = []
    if not layout_path.is_file(): gate_errors.append("layout_audit_missing")
    if not logical_path.is_file(): gate_errors.append("logical_flow_report_missing")
    if not language_path.is_file(): gate_errors.append("language_report_missing")
    if summary["layout"]["missingBlockInstanceCount"]: gate_errors.append("layout_instance_audit_incomplete")
    if overflow_count: gate_errors.append("segment_overflow")
    if summary["language"]["status"] != "passed" or (
        summary["language"]["outputMode"] == "replace" and summary["language"]["chineseResidualCount"]
    ):
        gate_errors.append("target_language_residue")
    if summary["language"]["invalidTranslationCount"]: gate_errors.append("invalid_translation")
    summary["status"] = "passed" if not gate_errors else "failed"
    summary["gate"] = {"passed": not gate_errors, "errorCodes": gate_errors}
    summary_path = artifacts / "audit-summary.json"
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    return summary

def _jsonl_record_count(path: Path) -> int:
    return sum(1 for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip())

def write_export_seal(job: Path, config: dict[str, object]) -> Path:
    """Atomically attest a successful export without changing the plugin JobConfig."""
    job = absolute(job)
    manifest, result, source, working = (Path(str(config[key])) for key in ("manifestPath", "resultPath", "sourcePath", "workingPath"))
    if not manifest.is_file(): raise RuntimeError("Export did not produce a manifest; no export seal written.")
    if not result.is_file() or json.loads(result.read_text(encoding="utf-8")).get("status") != "succeeded":
        raise RuntimeError("Export result envelope is not succeeded; no export seal written.")
    seal = {"schemaVersion": "1.0", "jobId": config["jobId"], "sourceSha256": config["sourceSha256"], "workingSha256": sha256(working), "manifestSha256": sha256(manifest), "recordCount": _jsonl_record_count(manifest)}
    for field in ("outputMode", "sourceLanguage", "targetLanguage", "pipelineVersion"):
        seal[field] = config.get(field)
    target = job / "artifacts" / "export-seal.json"
    temporary = target.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(seal, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, target)
    return target

def verify_export_seal(job: Path, config: dict[str, object]) -> None:
    job = absolute(job)
    source, working, manifest = (Path(str(config[key])) for key in ("sourcePath", "workingPath", "manifestPath"))
    if not source.is_file() or sha256(source) != config["sourceSha256"]: raise RuntimeError("Source drawing changed after export.")
    if not working.is_file() or sha256(working) != config["sourceSha256"]: raise RuntimeError("Job-owned working copy changed after export.")
    seal_path = job / "artifacts" / "export-seal.json"
    if not seal_path.is_file(): raise RuntimeError("Export seal is missing.")
    seal = json.loads(seal_path.read_text(encoding="utf-8"))
    for field in ("outputMode", "sourceLanguage", "targetLanguage", "pipelineVersion"):
        if field in seal and seal[field] != config.get(field):
            raise RuntimeError(f"Sealed job {field} changed after export.")
    if seal.get("schemaVersion") != "1.0" or config.get("jobId") != job.name or seal.get("jobId") != job.name: raise RuntimeError("Export seal job identity mismatch.")
    if seal.get("sourceSha256") != config["sourceSha256"] or seal.get("workingSha256") != sha256(working): raise RuntimeError("Export seal working copy hash mismatch.")
    if not manifest.is_file() or seal.get("manifestSha256") != sha256(manifest): raise RuntimeError("Export manifest changed after sealing.")
    if seal.get("recordCount") != _jsonl_record_count(manifest): raise RuntimeError("Export manifest record count changed after sealing.")

def require_ready(source: Path, autocad_root: Path) -> dict[str, object]:
    if "windowsapps" in str(Path(sys.executable).resolve()).lower():
        raise RuntimeError("Refusing WindowsApps Python alias; use real CPython.")
    try:
        ensure_runtime_plugin(autocad_root)
    except ValueError as error:
        # Unsupported host release: report the version blockers instead of the raw release error.
        raise RuntimeError("AutoCAD preflight blocked: " + json.dumps(doctor(source, autocad_root), ensure_ascii=False)) from error
    report = doctor(source, autocad_root)
    if report["status"] != "ready": raise RuntimeError("AutoCAD preflight blocked: " + json.dumps(report, ensure_ascii=False))
    return report

def decode_output(value: bytes | None) -> str:
    """Decode Core Console's UTF-16 or UTF-8 byte streams without guessing locale."""
    if not value:
        return ""
    if value.startswith((b"\xff\xfe", b"\xfe\xff")) or value.count(b"\x00") * 4 > len(value):
        return value.decode("utf-16", errors="replace")
    for encoding in ("utf-8", locale.getpreferredencoding(False), "gb18030"):
        try:
            return value.decode(encoding)
        except (LookupError, UnicodeDecodeError):
            continue
    return value.decode("utf-8", errors="replace")

def terminate_process_tree(process: object) -> None:
    """Stop the known Core Console process tree after the one allowed timeout."""
    if process.poll() is not None:
        return
    subprocess.run(["taskkill", "/pid", str(process.pid), "/t", "/f"], check=False, capture_output=True)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)

def run_once(
    operation: str,
    config_path: Path,
    working: Path,
    autocad_root: Path,
    timeout_seconds: int | None = None,
) -> int:
    if timeout_seconds is not None and timeout_seconds < 1:
        raise ValueError("timeout_seconds must be positive")
    job_config = json.loads(config_path.read_text(encoding="utf-8"))
    result_file = absolute(job_config["resultPath"])
    if not result_file.is_relative_to(absolute(config_path.parent.parent / "artifacts")):
        raise ValueError("Result envelope must be job-owned.")
    if result_file.exists():
        raise FileExistsError("Stage already attempted; create a fresh job instead of accepting stale results.")
    started = time.monotonic()
    plugin = (ensure_runtime_plugin(autocad_root) / PLUGIN_FILES[0]).resolve(strict=True)
    if any(ch in str(plugin) + str(working) for ch in ('\r', '\n', '"')): raise ValueError("Unsafe AutoCAD path")
    profile_name = str(profile(autocad_release(autocad_root))["name"])
    attempt = operation if config_path.stem == f"{operation}-job" else config_path.stem
    fingerprints = {name: sha256(plugin.parent / name) for name in PLUGIN_FILES if (plugin.parent / name).is_file()}
    script = config_path.parent / f"{attempt}.scr"
    script.write_text(f'_.NETLOAD\n"{plugin}"\nCADTRANS_{operation.upper()}\n_.QUIT\n', encoding="utf-8", newline="\n")
    command = [str(absolute(autocad_root) / "accoreconsole.exe"), "/product", "ACAD", "/p", profile_name, "/nologo", "/nohardware", "/i", str(working), "/s", str(script)]
    environment = os.environ.copy(); environment["CADTRANS_JOB_CONFIG"] = str(config_path); environment["CADTRANS_DIAGNOSTIC_DIRECTORY"] = str(config_path.parent.parent / "artifacts")
    creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    process = subprocess.Popen(command, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=creationflags)
    if timeout_seconds is None:
        timeout_seconds = DEFAULT_STAGE_TIMEOUT_SECONDS.get(operation, 300)
    if timeout_seconds < 1:
        raise ValueError("timeout_seconds must be positive")
    try:
        job_config = json.loads(config_path.read_text(encoding="utf-8"))
        result_path = Path(str(job_config.get("resultPath", "")))
    except (OSError, ValueError, json.JSONDecodeError):
        result_path = Path()

    def terminal_result_status() -> str | None:
        try:
            status_value = json.loads(result_path.read_text(encoding="utf-8")).get("status")
            return status_value if status_value in {"succeeded", "failed"} else None
        except (OSError, ValueError, json.JSONDecodeError):
            return None

    deadline = time.monotonic() + timeout_seconds
    stdout = stderr = b""
    envelope_status: str | None = None
    stage_timed_out = False
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            stage_timed_out = True
            break
        try:
            stdout, stderr = process.communicate(timeout=min(0.5, remaining))
            break
        except subprocess.TimeoutExpired:
            envelope_status = terminal_result_status()
            if envelope_status is not None:
                terminate_process_tree(process)
                stdout, stderr = process.communicate()
                break

    if stage_timed_out:
        terminate_process_tree(process)
        stdout, stderr = process.communicate()
        timeout_log = config_path.parent.parent / "artifacts" / f"{attempt}-timeout.log"
        timeout_log.write_text("Core Console timed out; process tree terminated.\n" + decode_output(stdout) + decode_output(stderr), encoding="utf-8")
        # AutoCAD can finish the plugin operation and save the drawing, then hang
        # while dismissing a font/save warning during QUIT.  The plugin result
        # envelope is authoritative; callers still verify every expected output.
        operation_succeeded = terminal_result_status() == "succeeded"
        returncode = 0 if operation_succeeded else (process.returncode if process.returncode is not None else 1)
    elif envelope_status is not None:
        returncode = 0 if envelope_status == "succeeded" else 1
    else:
        returncode = process.returncode
    (config_path.parent.parent / "artifacts" / f"{attempt}-console.log").write_text(decode_output(stdout) + decode_output(stderr), encoding="utf-8")
    metrics_path = config_path.parent.parent / "artifacts" / f"{attempt}-timing.json"
    metrics_path.write_text(json.dumps({"operation": operation, "seconds": round(time.monotonic() - started, 3),
        "exitCode": returncode, "timedOut": stage_timed_out,
        "pluginDirectory": str(plugin.parent), "pluginSha256": fingerprints,
        "configPath": str(config_path), "resultPath": str(result_file)}), encoding="utf-8")
    return returncode

def _run_stage(
    operation: str,
    config_path: Path,
    working: Path,
    autocad_root: Path,
    timeout_seconds: int | None,
) -> int:
    if timeout_seconds is None:
        return run_once(operation, config_path, working, autocad_root)
    return run_once(
        operation,
        config_path,
        working,
        autocad_root,
        timeout_seconds=timeout_seconds,
    )

def run_export(
    source: Path,
    job: Path,
    source_language: str,
    target_language: str,
    autocad_root: Path,
    timeout_seconds: int | None = None,
    output_mode: str = "replace",
    retry_from: Path | None = None,
) -> int:
    started = time.time()
    source = absolute(source)
    assert_source(source)
    require_ready(source, autocad_root)
    config = prepare_export_job(source, job, source_language, target_language, output_mode)
    record_timing(absolute(job), "export-start", started, retry_from=retry_from)
    result = _run_stage(
        "export",
        absolute(job) / "config" / "export-job.json",
        Path(str(config["workingPath"])),
        autocad_root,
        timeout_seconds,
    )
    record_timing(absolute(job), "export-finished" if result == 0 else "export-failed")
    if result != 0: return result
    write_export_seal(job, config)
    return result

def run_import(job: Path, translations: Path, autocad_root: Path, timeout_seconds: int | None = None) -> int:
    job, translations = absolute(job), absolute(translations)
    config = json.loads((job / "config" / "export-job.json").read_text(encoding="utf-8"))
    if config.get("pipelineVersion") != "2.0":
        raise ValueError("Legacy job: create a fresh V2 export; do not mix pipeline artifacts.")
    mode = read_output_mode(job)
    if mode != config["outputMode"]:
        raise ValueError("Job mode changed after export.")
    verify_export_seal(job, config)
    import types
    record_timing(job, "import-start")
    try:
        result = get_pipeline(mode).run_import(job, translations, autocad_root, timeout_seconds, config, types.SimpleNamespace(**globals()))
    except Exception:
        record_timing(job, "import-failed")
        raise
    record_timing(job, "import-finished" if result == 0 else "import-failed")
    return result


def _require_succeeded_result(result_path: Path, operation: str) -> None:
    if not result_path.is_file():
        raise RuntimeError(f"{operation} did not produce a result envelope.")
    try:
        envelope = json.loads(result_path.read_text(encoding="utf-8"))
        status_value = envelope.get("status")
    except json.JSONDecodeError as error:
        raise RuntimeError(f"{operation} result envelope is invalid JSON.") from error
    if status_value != "succeeded":
        details = json.dumps(envelope.get("errors") or envelope, ensure_ascii=False)
        raise RuntimeError(f"{operation} result envelope is not succeeded: {details}; result={result_path}")


def authoritative_preflight(job: Path, manifest: Path, translations: Path) -> dict[str, object]:
    from pipeline_io import write_report
    report = text_validation.validate_batch(manifest, translations)
    path = job / "artifacts/text-validation.json"
    write_report(path, report)
    if not report["isValid"]:
        raise ValueError("Core text validation failed: " + json.dumps(report["errors"], ensure_ascii=False) + f"; report={path}")
    return report


def run_resume(job: Path, autocad_root: Path, timeout_seconds: int | None = None, translated: Path | None = None):
    import types
    from task_recovery import resume
    return resume(absolute(job), autocad_root, timeout_seconds, types.SimpleNamespace(**globals()),
                  absolute(translated) if translated is not None else None)


def run_correct(job: Path, corrections: Path, autocad_root: Path, timeout_seconds: int | None = None):
    import types
    from task_recovery import correct
    record_timing(absolute(job), 'correct-start')
    try:
        result = correct(absolute(job), absolute(corrections), autocad_root, timeout_seconds, types.SimpleNamespace(**globals()))
    except Exception:
        record_timing(absolute(job), 'correct-failed')
        raise
    record_timing(absolute(job), 'correct-finished')
    return result

def status(job: Path) -> dict[str, object]:
    job = absolute(job); candidates = list((job / "results").glob("candidate.*")) if job.is_dir() else []
    logical_report = job / "artifacts" / "logical-flow-report.json"
    language_report = job / "artifacts" / "postcomposition-language-check.json"
    report = {"job": str(job), "exists": job.is_dir(), "exportConfig": (job / "config" / "export-job.json").is_file(), "manifest": (job / "exchange" / "manifest.input.jsonl").is_file(), "candidate": str(candidates[0]) if candidates else None, "logicalFlowReport": str(logical_report) if logical_report.is_file() else None, "postcompositionLanguageReport": str(language_report) if language_report.is_file() else None}
    if report["exportConfig"]:
        from task_recovery import load_job, retained_candidate
        import types
        try:
            config = load_job(job, types.SimpleNamespace(**globals()))
            candidate, _ = retained_candidate(job, config)
            report.update(candidate=str(candidate) if candidate else None, nextAction="resume")
            if (job / "artifacts" / f"{config['outputMode']}-final.json").is_file():
                summary = get_pipeline(config["outputMode"]).summarize(job)
                report.update(deliveryReady=summary["deliveryReady"], nextAction="deliver" if summary["deliveryReady"] else "review" if summary["gate"]["passed"] else "resume")
        except (ValueError, RuntimeError, OSError) as error:
            report.update(status="blocked", detail=str(error), nextAction="repair-job")
    else:
        report["nextAction"] = "export"
    return report

def _bounded_cli_output(report: dict[str, object]) -> dict[str, object]:
    output = dict(report)
    for key in list(output):
        if key.endswith("RecordIds") and isinstance(output[key], list):
            output[key[:-3] + "Examples"] = output[key][:DIAGNOSTIC_EXAMPLE_LIMIT]
            del output[key]
    return output

def discover_autocad() -> Path:
    configured = os.environ.get("CAD_TRANSLATE_AUTOCAD_ROOT")
    if configured:
        return absolute(configured)
    if (AUTOCAD_2025 / "accoreconsole.exe").is_file():
        return AUTOCAD_2025
    # Newest release the packaged plugin can load in; otherwise the newest console host, so preflight
    # reports the real version blocker instead of a missing-install error.
    ranked = sorted((host for host in registry_hosts() if Path(str(host["root"])).exists()),
                    key=lambda host: (host["year"] or 0), reverse=True)
    packaged = shipped_plugin_years()
    for require_shipped in (True, False):
        for host in ranked:
            if host["coreConsoleExists"] and (not require_shipped or host["year"] in packaged):
                return Path(str(host["root"]))
    return AUTOCAD_2025

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(); parser.add_argument("--autocad-root", type=Path, default=discover_autocad()); sub = parser.add_subparsers(dest="command", required=True)
    doctor_cmd = sub.add_parser("doctor"); doctor_cmd.add_argument("--source", type=Path)
    sub.add_parser("host-compat", help="Read-only inventory of installed AutoCAD hosts and the support tier of each")
    export = sub.add_parser("export"); export.add_argument("--source", type=Path, required=True); export.add_argument("--job", type=Path, required=True); export.add_argument("--source-language", default="zh-CN"); export.add_argument("--target-language", default="en"); export.add_argument("--timeout-seconds", type=int); export.add_argument("--output-mode", choices=sorted(OUTPUT_MODES), default="replace")
    export.add_argument("--retry-from", type=Path, help="Previous attempt of this same task; carry its full wall-clock timing, not its CAD result")
    prepared = sub.add_parser("prepare-translations"); prepared.add_argument("--job", type=Path, required=True); prepared.add_argument("--max-source-chars", type=int, default=6000)
    prepared.add_argument("--existing-inline-handles", help="Comma-separated current handles reviewed as complete inline bilingual pairs")
    assembled = sub.add_parser("assemble-translations"); assembled.add_argument("--job", type=Path, required=True); assembled.add_argument("--translated", type=Path, required=True)
    imported = sub.add_parser("import"); imported.add_argument("--job", type=Path, required=True); imported.add_argument("--translations", type=Path, required=True); imported.add_argument("--timeout-seconds", type=int)
    resumed = sub.add_parser("resume"); resumed.add_argument("--job", type=Path, required=True); resumed.add_argument("--timeout-seconds", type=int)
    resumed.add_argument("--translated", type=Path, help="Compact repaired translations; existing valid rows are retained")
    corrected = sub.add_parser("correct"); corrected.add_argument("--job", type=Path, required=True); corrected.add_argument("--corrections", type=Path, required=True); corrected.add_argument("--timeout-seconds", type=int)
    checked = sub.add_parser("check-translations"); checked.add_argument("--job", type=Path, required=True); checked.add_argument("--translations", type=Path, required=True); checked.add_argument("--report", type=Path)
    audited = sub.add_parser("audit-summary"); audited.add_argument("--job", type=Path, required=True)
    diagnosed = sub.add_parser("diagnose"); diagnosed.add_argument("--job", type=Path, required=True)
    diagnosed.add_argument("--window", type=float, nargs=4, action="append", default=[])
    diagnosed.add_argument("--render", action="store_true", help="Compare saved source/intermediate/candidate windows without reimporting")
    stat = sub.add_parser("status"); stat.add_argument("--job", type=Path, required=True); args = parser.parse_args(argv)
    if args.command == "doctor": output, code = doctor(args.source, args.autocad_root), 0
    elif args.command == "host-compat": output, code = host_compat_report(), 0
    elif args.command == "export": code = run_export(args.source, args.job, args.source_language, args.target_language, args.autocad_root, args.timeout_seconds, args.output_mode, args.retry_from); output = {"job": str(absolute(args.job)), "exitCode": code}
    elif args.command == "prepare-translations": output, code = prepare_translation_worklist(args.job, args.max_source_chars, args.existing_inline_handles), 0
    elif args.command == "assemble-translations": output, code = assemble_translations(args.job, args.translated), 0
    elif args.command == "import": code = run_import(args.job, args.translations, args.autocad_root, args.timeout_seconds); output = {"job": str(absolute(args.job)), "exitCode": code}
    elif args.command == "resume":
        output = run_resume(args.job, args.autocad_root, args.timeout_seconds, args.translated)
        code = output.get("exitCode", 1 if output.get("status") in {"failed", "translation_required"} else 0)
    elif args.command == "correct":
        output = run_correct(args.job, args.corrections, args.autocad_root, args.timeout_seconds)
        code = output.get("exitCode", 1 if output.get("status") == "failed" else 0)
    elif args.command == "check-translations":
        job = absolute(args.job)
        output = _bounded_cli_output(check_translations(job / "exchange" / "manifest.input.jsonl", absolute(args.translations), args.report, read_output_mode(job)))
        code = 0 if output["status"] == "passed" else 1
    elif args.command == "audit-summary":
        output = summarize_audit(args.job)
        if output.get("deliveryReady"):
            output["workflowTiming"] = record_timing(absolute(args.job), "delivery-ready")
        code = 0 if output["status"] == "passed" else 1
    elif args.command == "diagnose":
        from job_diagnostics import diagnose
        output = diagnose(args.job, args.window, render=args.render)
        code = 0
    else: output, code = status(args.job), 0
    print(json.dumps(_bounded_cli_output(output), ensure_ascii=False, indent=2)); return code

if __name__ == "__main__": raise SystemExit(main())
