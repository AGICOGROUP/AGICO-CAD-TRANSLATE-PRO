import re
from pathlib import Path
from pipeline_io import read_jsonl, write_report, read_report, digest, launch_import, publish_candidate
from translation_work import direction, needs_translation, visible, HAN, LATIN
from replacement_quality import review, layout_review

SOURCE_TEXT_RESIDUE = re.compile(r"[\u2e80-\u2fff\u31c0-\u31ef\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U0002fa1f\U00030000-\U000323af]")
MTEXT_FONT_CODE = re.compile(r"\\[Ff][^;]*;")

class ReplacePipeline:
    mode = "replace"

    def check_translations(self, manifest_path, translations_path, report_path, validate_complete):
        count = validate_complete(manifest_path, translations_path)
        source_lang, target_lang = direction(manifest_path.parent.parent)
        manifest = {r["recordId"]: r for r in read_jsonl(manifest_path)}
        residual, invalid = [], []
        requested = 0
        quality = []
        for row in read_jsonl(translations_path):
            source = manifest[row["recordId"]]
            target = visible(row["translatedText"])
            needed = needs_translation(source, source_lang)
            requested += needed
            findings = review(source, row["translatedText"]) if target_lang == "en" else []
            if findings:
                quality.append({"recordId": row["recordId"], "sourceText": source.get("plainText"),
                    "translatedText": row["translatedText"], "findings": findings})
            if target_lang == "en" and SOURCE_TEXT_RESIDUE.search(target): residual.append(row["recordId"])
            if target_lang == "en" and HAN.search(visible(source.get("plainText", ""))) and not re.search(r"(?<!\w)[A-Za-z]{2,}(?!\w)", target): invalid.append(row["recordId"])
            if target_lang == "zh" and needed and not HAN.search(target): invalid.append(row["recordId"])
            if (source.get("plainText") and not row["translatedText"].strip()) or "\ufffd" in row["translatedText"]:
                invalid.append(row["recordId"])
        report = {"schemaVersion": "1.0", "pipeline": "replace-v2", "outputMode": self.mode,
            "status": "passed" if not residual and not invalid else "failed", "records": count,
            "translatedSourceRecords": requested, "chineseResidualCount": len(residual),
            "chineseResidualRecordIds": residual, "invalidTranslationCount": len(invalid),
            "invalidTranslationRecordIds": invalid}
        write_report(report_path, report)
        write_report(manifest_path.parent.parent / "artifacts" / "replace-semantic-review.json",
            {"status": "review-needed" if quality else "no-heuristic-findings",
             "note": "Heuristics identify review candidates, not an automatic semantic verdict.",
             "count": len(quality), "records": quality})
        report["semanticReviewCount"] = len(quality)
        return report

    def check_candidate(self, manifest_path, report_path):
        records = read_jsonl(manifest_path)
        # V2 candidate snapshots live directly in the job artifacts directory.
        job = manifest_path.parent.parent
        source_lang, target_lang = direction(job)
        residual = [r["recordId"] for r in records if target_lang == "en" and SOURCE_TEXT_RESIDUE.search(visible(r.get("plainText", "")))]
        raw = [r["recordId"] for r in records if target_lang == "en"
            and SOURCE_TEXT_RESIDUE.search(MTEXT_FONT_CODE.sub("", r.get("rawText", "")))]
        invalid = []
        if target_lang == "zh":
            original = read_jsonl(job / "exchange" / "manifest.input.jsonl")
            layout_path = job / "artifacts" / "replace-layout-audit.json"
            layout = read_report(layout_path) if layout_path.is_file() else {}
            handles = {r["recordId"]: r.get("newHandle") for r in layout.get("texts", [])}
            by_handle = {r.get("handle", r["recordId"]): r for r in records}
            for source in original:
                if not needs_translation(source, source_lang): continue
                current = by_handle.get(handles.get(source["recordId"]) or source.get("handle", source["recordId"]), {})
                if not HAN.search(visible(current.get("plainText", ""))): invalid.append(source["recordId"])
        report = {"schemaVersion": "1.0", "pipeline": "replace-v2", "outputMode": self.mode,
            "status": "passed" if not residual and not raw and not invalid else "failed", "records": len(records),
            "plainTextChineseResidualCount": len(residual), "plainTextChineseResidualRecordIds": residual,
            "rawTextChineseResidualCount": len(raw), "rawTextChineseResidualRecordIds": raw,
            "invalidTranslationCount": len(invalid), "invalidTranslationRecordIds": invalid}
        write_report(report_path, report)
        return report

    def run_import(self, job, translations, root, timeout, config, runtime):
        pre = self.check_translations(Path(config["manifestPath"]), translations,
            job / "artifacts" / "replace-preimport.json", runtime.validate_complete_translations)
        if pre["status"] != "passed": raise ValueError(f"Replacement translation gate failed: chinese_residual_count={pre['chineseResidualCount']}, invalid_translation_count={pre['invalidTranslationCount']}")
        code, staged = launch_import(job, translations, root, timeout, config, runtime)
        if code: return code
        adjustment_path = job / "artifacts" / "replace-layout-adjustments.json"
        if adjustment_path.is_file():
            write_report(job / "artifacts" / "replace-readability-review.json",
                layout_review(read_report(adjustment_path)))
        language_report = self.check_candidate(job / "artifacts" / "replace-candidate.jsonl",
            job / "artifacts" / "replace-language.json")
        native = read_report(job / "artifacts" / "replace-native-check.json")
        structure = read_report(job / "artifacts" / "replace-structure.json")
        layout = read_report(job / "artifacts" / "replace-layout-audit.json")
        logical = read_report(job / "artifacts" / "logical-flow-report.json")
        if (language_report["status"] != "passed" or native["status"] != "passed" or structure["status"] != "passed"
            or layout.get("missingBlockInstancePaths") or native["candidateSha256"] != digest(staged)):
            raise ValueError("Replacement candidate gate failed; staged drawing retained in artifacts.")
        review_ids = sorted({value for risk in layout.get("manualReview", [])
            for value in (risk.get("recordId"), risk.get("otherRecordId")) if value})
        overflow = runtime._segment_overflow_count(logical)
        publish_candidate(staged, config["outputPath"])
        write_report(job / "artifacts" / "replace-final.json", {"status": "passed", "outputMode": "replace",
            "candidateSha256": native["candidateSha256"], "requiresVisualReview": True,
            "layoutReviewRequired": bool(layout.get("manualReview") or overflow),
            "layoutReviewRecordIds": review_ids, "segmentOverflowCount": overflow,
            "sourceSha256": config["sourceSha256"]})
        return 0

    def summarize(self, job):
        path = job / "artifacts" / "replace-final.json"
        errors = []
        report = read_report(path) if path.is_file() else {}
        if report.get("status") != "passed" or report.get("outputMode") != self.mode: errors.append("replace_final_missing_or_failed")
        config = read_report(job / "config" / "export-job.json")
        candidate = Path(config["outputPath"])
        if not candidate.is_file() or digest(candidate) != report.get("candidateSha256"): errors.append("replace_candidate_missing_or_changed")
        review_path = job / "artifacts" / "replace-visual-review.json"
        review = read_report(review_path) if review_path.is_file() else {}
        warnings = review.get("warnings", [])
        warnings_valid = isinstance(warnings, list) and all(isinstance(w, str) and w.strip() for w in warnings)
        # One final visual decision covers both fitting and readability; layout IDs
        # remain review targets, not a second machine-enforced approval checklist.
        visual_passed = (review.get("status") in {"passed", "passed_with_warnings"}
            and not review.get("blockingIssues") and warnings_valid
            and (review.get("status") != "passed_with_warnings" or bool(warnings))
            and review.get("candidateSha256") == report.get("candidateSha256")
            and review.get("sourceSha256") == config["sourceSha256"] and bool(review.get("images"))
            and all((job / "artifacts" / p).is_file() for p in review.get("images", [])))
        quality_counts = {}
        for name in ("semantic", "readability"):
            detail = job / "artifacts" / f"replace-{name}-review.json"
            quality_counts[name] = read_report(detail).get("count", 0) if detail.is_file() else None
        if review.get("status") == "failed" or review.get("blockingIssues"):
            errors.append("replace_visual_blocking_issues")
        ready = not errors and visual_passed
        blocked = bool(errors or review.get("status") == "failed" or review.get("blockingIssues"))
        return {"outputMode": self.mode, "status": "failed" if blocked else "review_required" if report.get("layoutReviewRequired") and not visual_passed else "passed",
            "qualityReviewCounts": quality_counts,
            "gate": {"passed": not errors, "errorCodes": errors}, "requiresVisualReview": not visual_passed,
            "deliveryReady": ready, "candidate": str(candidate),
            "deliveryStatus": "ready_with_warnings" if ready and warnings else "ready" if ready else "blocked" if blocked else "needs_review",
            "warnings": warnings if ready else [], "blockingIssues": review.get("blockingIssues", []),
            "layoutReviewRecordIds": report.get("layoutReviewRecordIds", []), "segmentOverflowCount": report.get("segmentOverflowCount", 0)}
