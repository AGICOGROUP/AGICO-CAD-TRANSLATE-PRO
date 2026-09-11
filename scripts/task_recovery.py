"""Resume sealed CAD jobs using their saved candidate and translation bindings."""
import math
from pathlib import Path

from pipeline_io import digest, read_report, read_jsonl, write_report, fresh_path, launch_operation


def identity(config):
    return {key: config[key] for key in
            ("sourceSha256", "outputMode", "sourceLanguage", "targetLanguage", "pipelineVersion")}


def write_binding(job, config, candidate, attempt):
    path = job / "artifacts/candidate-binding.json"
    previous = read_report(path) if path.is_file() else {}
    fallback = {}
    if (not Path(candidate).is_file() and previous.get("translationSha256") == digest(job / "exchange/translations.output.jsonl")
            and all(previous.get(key) == value for key, value in identity(config).items())):
        if previous.get("candidateSha256") and Path(previous["candidatePath"]).is_file():
            fallback = {"fallbackCandidatePath": previous["candidatePath"],
                        "fallbackCandidateSha256": previous["candidateSha256"]}
        elif previous.get("fallbackCandidatePath"):
            fallback = {key: previous[key] for key in ("fallbackCandidatePath", "fallbackCandidateSha256")}
    write_report(path, {
        **identity(config), "manifestSha256": digest(config["manifestPath"]),
        "translationSha256": digest(job / "exchange/translations.output.jsonl"),
        "candidatePath": str(candidate), "attemptConfig": str(attempt),
        "candidateSha256": digest(candidate) if Path(candidate).is_file() else None, **fallback})


def publish_binding(job, candidate):
    path = job / "artifacts/candidate-binding.json"
    binding = read_report(path)
    binding.update(candidatePath=str(candidate), candidateSha256=digest(candidate))
    write_report(path, binding)


def load_job(job, runtime):
    config = read_report(job / "config/export-job.json")
    if config.get("pipelineVersion") != "2.0":
        raise ValueError("Legacy job: create a fresh V2 export; do not mix pipeline artifacts.")
    if runtime.read_output_mode(job) != config["outputMode"]:
        raise ValueError("Job mode changed after export.")
    runtime.verify_export_seal(job, config)
    # Recovery may only write/read candidates inside this job, never the source.
    for key, directory in (("workingPath", "working"), ("manifestPath", "exchange"), ("outputPath", "results")):
        if not Path(config[key]).resolve().is_relative_to(job / directory):
            raise ValueError(f"Recovery {key} must be job-owned.")
    return config


def retained_candidate(job, config):
    binding_path = job / "artifacts/candidate-binding.json"
    translations = job / "exchange/translations.output.jsonl"
    if binding_path.is_file():
        binding = read_report(binding_path)
        expected = {**identity(config), "manifestSha256": digest(config["manifestPath"]),
                    "translationSha256": digest(translations)}
        if any(binding.get(key) != value for key, value in expected.items()):
            raise ValueError("Candidate binding changed (source, mode, languages or translations); create a fresh import candidate.")
        candidate = Path(binding["candidatePath"]).resolve()
        if not candidate.is_file() and binding.get("fallbackCandidatePath"):
            candidate = Path(binding["fallbackCandidatePath"]).resolve()
            binding = {**binding, "candidatePath": str(candidate), "candidateSha256": binding["fallbackCandidateSha256"]}
        if not candidate.is_relative_to(job) or candidate == Path(config["sourcePath"]).resolve():
            raise ValueError("Candidate binding must name a job-owned drawing.")
        if candidate.is_file() and binding.get("candidateSha256") not in (None, digest(candidate)):
            raise ValueError("Candidate changed after its saved hash binding; do not reuse stale review.")
        return candidate if candidate.is_file() else None, binding

    # Older V2 jobs have no receipt. Only an existing import config with the exact
    # owned translations may support fresh native inspection, never direct reuse.
    imported = job / "config/import-job.json"
    if not imported.is_file(): return None, None
    attempt = read_report(imported)
    if any(attempt.get(key) != value for key, value in identity(config).items()):
        raise ValueError("Legacy import config disagrees with sealed job identity.")
    old_translations = Path(attempt.get("translationPath") or "").resolve()
    if not old_translations.is_file() or digest(old_translations) != digest(translations):
        raise ValueError("Legacy import translations differ from the owned translations.")
    final_path = job / "artifacts" / f"{config['outputMode']}-final.json"
    final = read_report(final_path) if final_path.is_file() else {}
    for candidate in (Path(final.get("candidatePath", config["outputPath"])), Path(attempt["outputPath"])):
        candidate = candidate.resolve()
        if not candidate.is_relative_to(job): raise ValueError("Legacy candidate must be job-owned.")
        if candidate.is_file(): return candidate, None
    return None, None


def repair_translations(job, config, runtime):
    """Keep existing rows intact and emit only missing/invalid translation units."""
    translations = job / "exchange/translations.output.jsonl"
    manifest = read_jsonl(Path(config["manifestPath"]))
    rows = read_jsonl(translations) if translations.is_file() else []
    by_id = {}
    invalid = set()
    sources = {r["recordId"]: r for r in manifest}
    for row in rows:
        rid = row.get("recordId")
        if rid in by_id: invalid.add(rid)
        source = sources.get(rid)
        if (not source or row.get("inputHash") != source["inputHash"]
                or not isinstance(row.get("translatedText"), str)
                or row.get("schemaVersion") != "1.0" or not isinstance(row.get("reason"), str)
                or row.get("reviewStatus") not in ("approved", "manual-review")):
            invalid.add(rid)
        by_id[rid] = row
    missing = set(sources) - by_id.keys()
    if translations.is_file():
        validation = runtime.text_validation.validate_batch(Path(config["manifestPath"]), translations)
        write_report(job / "artifacts/text-validation.json", validation)
        invalid.update(error["recordId"] for error in validation["errors"]
                       if error.get("recordId") in by_id)
    if not missing and not invalid: return None
    runtime.prepare_translation_worklist(job)
    work = [row for part in sorted((job / "exchange/translation-worklist").glob("part-*.jsonl"))
            for row in read_jsonl(part)]
    grouped = runtime.job_groups(manifest, runtime.language(config["sourceLanguage"]), job)
    representatives = set()
    recovered = []
    for group in grouped:
        available = {by_id[row["recordId"]]["translatedText"] for row in group
                     if row["recordId"] in by_id and row["recordId"] not in invalid}
        if len(available) == 1:
            recovered.append({"recordId": group[0]["recordId"], "translatedText": available.pop()})
        else:
            representatives.add(group[0]["recordId"])
    repairs = [r for r in work if r["recordId"] in representatives]
    path = job / "exchange/translation-repairs.jsonl"
    runtime._atomic_write_jsonl(path, repairs)
    good = job / "exchange/translations.recovered.jsonl"
    runtime._atomic_write_jsonl(good, recovered)
    if not repairs and not invalid:
        runtime.assemble_translations(job, good)
        return None
    report = {"status": "translation_required", "nextAction": "repair-translations",
              "missingRecordIds": sorted(missing), "invalidRecordIds": sorted(str(r) for r in invalid),
              "repairPath": str(path), "preservedTranslations": str(good), "deliveryReady": False}
    write_report(job / "artifacts/translation-recovery.json", report)
    return report


def merge_repaired(job, translated, runtime):
    """A supplied compact repair overrides its IDs while retaining good owned rows."""
    rows = {}
    previous = job / "exchange/translations.recovered.jsonl"
    if previous.is_file(): rows.update((r["recordId"], r) for r in read_jsonl(previous))
    paths = sorted(translated.glob("*.jsonl")) if translated.is_dir() else [translated]
    for path in paths:
        rows.update((r["recordId"], r) for r in read_jsonl(path))
    merged = fresh_path(job / "exchange/translations.repair-merged.jsonl")
    runtime._atomic_write_jsonl(merged, list(rows.values()))
    return runtime.assemble_translations(job, merged)


def result_status(job, runtime, action):
    summary = runtime.summarize_audit(job)
    summary.update(action=action, nextAction="deliver" if summary.get("deliveryReady") else "review")
    return summary


def resume(job, root, timeout, runtime, translated=None):
    config = load_job(job, runtime)
    candidate, binding = retained_candidate(job, config)
    if translated is not None:
        if candidate is not None:
            raise ValueError("A saved candidate is bound to its translations; use a fresh import for content changes.")
        merge_repaired(job, translated, runtime)
    repaired = repair_translations(job, config, runtime)
    if repaired: return repaired
    if candidate is None:
        code = runtime.run_import(job, job / "exchange/translations.output.jsonl", root, timeout)
        return {"status": "failed", "exitCode": code, "nextAction": "resume"} if code else result_status(job, runtime, "imported")
    mode = config["outputMode"]
    final_path = job / "artifacts" / f"{mode}-final.json"
    final = read_report(final_path) if final_path.is_file() else {}
    if (binding and final.get("status") == "passed" and final.get("sourceSha256") == config["sourceSha256"]
            and final.get("candidateSha256") == digest(candidate)):
        return result_status(job, runtime, "reused")
    runtime.authoritative_preflight(job, Path(config["manifestPath"]), job / "exchange/translations.output.jsonl")
    code, staged = launch_operation(job, root, timeout, config, runtime, "inspect", candidate)
    if code: return {"status": "failed", "exitCode": code, "nextAction": "resume"}
    output = candidate if candidate.parent == job / "results" else fresh_path(Path(config["outputPath"]))
    runtime.get_pipeline(mode).accept_candidate(job, staged, config, runtime, output)
    return result_status(job, runtime, "inspected")


def validate_corrections(corrections, candidate):
    if corrections.get("candidateSha256") != digest(candidate):
        raise ValueError("Corrections candidateSha256 is stale; inspect the current candidate.")
    edits = corrections.get("edits")
    if not isinstance(edits, list) or not edits: raise ValueError("Corrections require a nonempty edits list.")
    handles = set()
    for edit in edits:
        if not isinstance(edit, dict) or set(edit) - {"handle", "expectedText", "width", "height", "x", "y"}:
            raise ValueError("Only handle, expectedText and geometry correction fields are supported.")
        handle = edit.get("handle")
        if not isinstance(handle, str) or not handle or handle.upper() in handles:
            raise ValueError("Corrections require unique target handles.")
        handles.add(handle.upper())
        if not isinstance(edit.get("expectedText"), str): raise ValueError("Correction expectedText is required.")
        geometry = set(edit) & {"width", "height", "x", "y"}
        if not geometry: raise ValueError("Each correction requires geometry edits.")
        if ("x" in geometry) != ("y" in geometry): raise ValueError("Position corrections require both x and y.")
        for key in geometry:
            value = edit[key]
            if type(value) not in (int, float) or not math.isfinite(value) or (key in {"width", "height"} and value <= 0):
                raise ValueError(f"Correction {key} must be finite and dimensions positive.")


def correct(job, corrections_path, root, timeout, runtime):
    config = load_job(job, runtime)
    candidate, binding = retained_candidate(job, config)
    if candidate is None: raise ValueError("No saved candidate to correct; resume the import first.")
    corrections = read_report(corrections_path)
    validate_corrections(corrections, candidate)
    runtime.authoritative_preflight(job, Path(config["manifestPath"]), job / "exchange/translations.output.jsonl")
    code, staged = launch_operation(job, root, timeout, config, runtime, "correct", candidate, corrections)
    if code: return {"status": "failed", "exitCode": code, "nextAction": "resume"}
    output = fresh_path(job / "results" / f"candidate-corrected{candidate.suffix}")
    runtime.get_pipeline(config["outputMode"]).accept_candidate(job, staged, config, runtime, output)
    return result_status(job, runtime, "corrected")
