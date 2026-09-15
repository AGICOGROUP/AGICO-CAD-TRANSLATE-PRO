from pathlib import Path
import re
from pipeline_io import read_jsonl, write_report, read_report, digest, launch_import, publish_candidate
from translation_work import direction, needs_translation, visible, HAN, LATIN

class BilingualPipeline:
    mode = "bilingual"

    def check_translations(self, manifest_path, translations_path, report_path, validate_complete):
        count = validate_complete(manifest_path, translations_path)
        source_lang, target_lang = direction(manifest_path.parent.parent)
        sources = {r["recordId"]: r for r in read_jsonl(manifest_path)}
        invalid, requested = [], 0
        rows = read_jsonl(translations_path)
        targets = {r['recordId']:r['translatedText'] for r in rows}
        from bilingual_work import term_groups
        for group in term_groups(list(sources.values()),manifest_path.parent.parent):
            if len({targets[r['recordId']] for r in group}) != 1:
                invalid.extend(r['recordId'] for r in group)
        for row in rows:
            source = sources[row["recordId"]]
            if needs_translation(source, source_lang):
                requested += 1
                target = visible(row["translatedText"])
                # V2 exchange contains ONLY the added translation; source preservation is native.
                if not (HAN if target_lang == "zh" else LATIN).search(target):
                    invalid.append(row["recordId"])
                if target_lang != "zh" and HAN.search(target): invalid.append(row["recordId"])
                if target_lang != "zh" and not re.search(r"(?<!\w)[A-Za-zÀ-ÖØ-öø-ÿ]{2,}(?!\w)", target): invalid.append(row["recordId"])
            if "\ufffd" in row["translatedText"] or (source.get("plainText") and not row["translatedText"].strip()):
                invalid.append(row["recordId"])
        invalid = sorted(set(invalid))
        report = {"schemaVersion": "1.0", "pipeline": "bilingual-v2", "outputMode": self.mode,
            "status": "passed" if not invalid else "failed", "records": count,
            "translatedSourceRecords": requested, "bilingualRecordCount": requested,
            "chineseResidualCount": 0, "chineseResidualRecordIds": [],
            "invalidTranslationCount": len(invalid), "invalidTranslationRecordIds": invalid}
        write_report(report_path, report)
        return report

    def check_candidate(self, manifest_path, report_path):
        job = manifest_path.parent.parent
        native_path = job / "artifacts" / "bilingual-native-check.json"
        native = read_report(native_path) if native_path.is_file() else {}
        # A Chinese original next to English is valid; a Latin code alone is not evidence.
        pairs_path = job / "artifacts" / "bilingual-pairs.json"
        associations = read_report(pairs_path) if pairs_path.is_file() else {}
        records = read_jsonl(manifest_path)
        handles = {r.get("handle"): r for r in records}
        missing = [p["recordId"] for p in associations.get("pairs", []) if p["targetHandle"] not in handles]
        passed = (native.get("status") == "passed" and native.get("outputMode") == self.mode
            and not native.get("changedSources") and not native.get("missingTargets")
            and not associations.get("unresolved") and pairs_path.is_file() and not missing)
        report = {"schemaVersion": "1.0", "pipeline": "bilingual-v2", "outputMode": self.mode,
            "status": "passed" if passed else "failed", "records": len(records),
            "sourceRetainedCount": native.get("sourceRetainedCount", 0),
            "addedCount": native.get("addedCount", 0), "skippedExistingCount": native.get("skippedExistingCount", 0),
            "missingTargetRecordIds": missing, "chineseResidualCount": 0, "invalidTranslationCount": len(missing)}
        write_report(report_path, report)
        return report

    def run_import(self, job, translations, root, timeout, config, runtime):
        # Layout retries operate on the retained drawing. Re-running placement for
        # unchanged text costs a full import and can discard reviewed local edits.
        if (job / 'artifacts/candidate-binding.json').is_file():
            from task_recovery import retained_candidate, resume
            owned = job / 'exchange/translations.output.jsonl'
            if digest(translations) != digest(owned):
                raise ValueError('Translations changed; use a fresh job for a new bound import.')
            candidate, _ = retained_candidate(job, config)
            if candidate is not None:
                report = resume(job, root, timeout, runtime)
                report['layoutCorrection'] = {
                    'command': 'correct',
                    'instruction': 'For observed layout defects, batch edits to existing target text and '
                                   'review the affected regions plus an overview. Use a fresh job only when '
                                   'content or importer changes require full regression.'}
                write_report(job / 'artifacts/import-recovery.json', report)
                return report.get('exitCode', 0)
        pre = self.check_translations(Path(config["manifestPath"]), translations,
            job / "artifacts" / "bilingual-preimport.json", runtime.validate_complete_translations)
        if pre["status"] != "passed": raise ValueError("Bilingual target-only translation gate failed.")
        code, staged = launch_import(job, translations, root, timeout, config, runtime)
        if code: return code
        return self.accept_candidate(job, staged, config, runtime)

    def accept_candidate(self, job, staged, config, runtime, output=None):
        output = Path(output or config["outputPath"])
        checked = self.check_candidate(job / "artifacts" / "bilingual-candidate.jsonl", job / "artifacts" / "bilingual-language.json")
        native = read_report(job / "artifacts" / "bilingual-native-check.json")
        structure = read_report(job / "artifacts" / "bilingual-structure.json")
        layout = read_report(job / "artifacts" / "bilingual-layout-audit.json")
        if (checked["status"] != "passed" or structure["status"] != "passed" or layout.get("manualReview")
            or native["candidateSha256"] != digest(staged)):
            raise ValueError("Bilingual candidate gate failed; staged drawing retained in artifacts.")
        publish_candidate(staged, output)
        from task_recovery import publish_binding
        publish_binding(job, output)
        write_report(job / "artifacts" / "bilingual-final.json", {"status": "passed", "outputMode": "bilingual",
            "candidateSha256": native["candidateSha256"], "candidatePath": str(output), "requiresVisualReview": True,
            "sourceSha256": config["sourceSha256"], "addedCount": checked["addedCount"],
            "skippedExistingCount": checked["skippedExistingCount"]})
        return 0

    def summarize(self, job):
        path = job / "artifacts" / "bilingual-final.json"
        errors = []
        report = read_report(path) if path.is_file() else {}
        if report.get("status") != "passed" or report.get("outputMode") != self.mode: errors.append("bilingual_final_missing_or_failed")
        config = read_report(job / "config" / "export-job.json")
        candidate = Path(report.get("candidatePath", config["outputPath"]))
        if not candidate.is_file() or digest(candidate) != report.get("candidateSha256"): errors.append("bilingual_candidate_missing_or_changed")
        review_path = job / "artifacts" / "bilingual-visual-review.json"
        review = read_report(review_path) if review_path.is_file() else {}
        if (review.get("candidateSha256") != report.get("candidateSha256")
                or review.get("sourceSha256") != config["sourceSha256"]):
            review = {}
        # Bilingual acceptance is independent: source/target integrity and unresolved
        # additions still fail native import; only reviewed cosmetic defects are warnings.
        warnings = review.get("warnings", [])
        warnings_valid = isinstance(warnings, list) and all(isinstance(w, str) and w.strip() for w in warnings)
        visual_passed = (review.get("status") in {"passed", "passed_with_warnings"}
            and not review.get("blockingIssues") and warnings_valid
            and (review.get("status") != "passed_with_warnings" or bool(warnings))
            and review.get("candidateSha256") == report.get("candidateSha256")
            and review.get("sourceSha256") == config["sourceSha256"] and bool(review.get("images"))
            and all((job / "artifacts" / p).is_file() for p in review.get("images", [])))
        if review.get("status") == "failed" or review.get("blockingIssues"):
            errors.append("bilingual_visual_blocking_issues")
        ready = not errors and visual_passed
        blocked = bool(errors or review.get("status") == "failed" or review.get("blockingIssues"))
        layout_path = job / "artifacts" / "bilingual-layout-audit.json"
        layout = read_report(layout_path) if layout_path.is_file() else {}
        return {"outputMode": self.mode, "status": "failed" if blocked else "passed",
            "gate": {"passed": not errors, "errorCodes": errors}, "requiresVisualReview": not visual_passed,
            "deliveryReady": ready, "candidate": str(candidate),
            "deliveryStatus": "ready_with_warnings" if ready and warnings else "ready" if ready else "blocked" if blocked else "needs_review",
            "warnings": warnings if ready else [], "blockingIssues": review.get("blockingIssues", []),
            "layoutReviewRecordIds": layout.get("reviewRecordIds", []), "layoutRiskCounts": layout.get("riskCounts", {}),
            "addedCount": report.get("addedCount", 0), "skippedExistingCount": report.get("skippedExistingCount", 0)}
