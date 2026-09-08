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
        for row in read_jsonl(translations_path):
            source = sources[row["recordId"]]
            if needs_translation(source, source_lang):
                requested += 1
                target = visible(row["translatedText"])
                # V2 exchange contains ONLY the added translation; source preservation is native.
                if not (HAN if target_lang == "zh" else LATIN).search(target):
                    invalid.append(row["recordId"])
                if target_lang == "en" and HAN.search(target): invalid.append(row["recordId"])
                if target_lang == "en" and not re.search(r"(?<!\w)[A-Za-z]{2,}(?!\w)", target): invalid.append(row["recordId"])
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
        pre = self.check_translations(Path(config["manifestPath"]), translations,
            job / "artifacts" / "bilingual-preimport.json", runtime.validate_complete_translations)
        if pre["status"] != "passed": raise ValueError("Bilingual target-only translation gate failed.")
        code, staged = launch_import(job, translations, root, timeout, config, runtime)
        if code: return code
        checked = self.check_candidate(job / "artifacts" / "bilingual-candidate.jsonl", job / "artifacts" / "bilingual-language.json")
        native = read_report(job / "artifacts" / "bilingual-native-check.json")
        structure = read_report(job / "artifacts" / "bilingual-structure.json")
        layout = read_report(job / "artifacts" / "bilingual-layout-audit.json")
        if (checked["status"] != "passed" or structure["status"] != "passed" or layout.get("manualReview")
            or native["candidateSha256"] != digest(staged)):
            raise ValueError("Bilingual candidate gate failed; staged drawing retained in artifacts.")
        publish_candidate(staged, config["outputPath"])
        write_report(job / "artifacts" / "bilingual-final.json", {"status": "passed", "outputMode": "bilingual",
            "candidateSha256": native["candidateSha256"], "requiresVisualReview": True,
            "sourceSha256": config["sourceSha256"], "addedCount": checked["addedCount"],
            "skippedExistingCount": checked["skippedExistingCount"]})
        return 0

    def summarize(self, job):
        path = job / "artifacts" / "bilingual-final.json"
        errors = []
        report = read_report(path) if path.is_file() else {}
        if report.get("status") != "passed" or report.get("outputMode") != self.mode: errors.append("bilingual_final_missing_or_failed")
        config = read_report(job / "config" / "export-job.json")
        candidate = Path(config["outputPath"])
        if not candidate.is_file() or digest(candidate) != report.get("candidateSha256"): errors.append("bilingual_candidate_missing_or_changed")
        review_path = job / "artifacts" / "bilingual-visual-review.json"
        review = read_report(review_path) if review_path.is_file() else {}
        visual_passed = (review.get("status") == "passed" and review.get("candidateSha256") == report.get("candidateSha256")
            and review.get("sourceSha256") == config["sourceSha256"] and bool(review.get("images"))
            and all((job / "artifacts" / p).is_file() for p in review.get("images", [])))
        return {"outputMode": self.mode, "status": "failed" if errors else "passed",
            "gate": {"passed": not errors, "errorCodes": errors}, "requiresVisualReview": not visual_passed,
            "deliveryReady": not errors and visual_passed, "candidate": str(candidate),
            "addedCount": report.get("addedCount", 0), "skippedExistingCount": report.get("skippedExistingCount", 0)}
