import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from test_cad_translate import cad_translate
from pipeline_io import digest, write_report


class RuntimeV2Tests(unittest.TestCase):
    def fixture(self, root, mode="replace", source_language="zh-CN", target_language="en"):
        source = root / "source.dwg"
        source.write_bytes(b"sealed source")
        job = root / "job"
        config = cad_translate.prepare_export_job(source, job, source_language, target_language, mode)
        row = {"recordId": "a", "inputHash": "h", "plainText": "泵" if source_language == "zh-CN" else "Pump", "protectedTokens": []}
        row["rawText"] = row["plainText"]
        Path(config["manifestPath"]).write_text(json.dumps(row, ensure_ascii=False) + "\n", encoding="utf-8")
        write_report(Path(config["resultPath"]), {"status": "succeeded"})
        cad_translate.write_export_seal(job, config)
        translated = job / "exchange" / "translations.output.jsonl"
        translated.write_text(json.dumps({"schemaVersion": "1.0", "recordId": "a", "inputHash": "h",
            "translatedText": "Pump" if target_language == "en" else "泵", "reviewStatus": "approved", "reason": "test"}) + "\n", encoding="utf-8")
        return job, config, translated

    def native_result(self, job, stage, overflow=False):
        output = Path(stage["outputPath"])
        output.write_bytes(b"native candidate")
        mode = stage["outputMode"]
        artifacts = job / "artifacts"
        write_report(Path(stage["resultPath"]), {"status": "succeeded"})
        write_report(artifacts / f"{mode}-native-check.json", {"status": "passed", "outputMode": mode,
            "sourceRetainedCount": 1, "addedCount": 1, "skippedExistingCount": 0, "candidateSha256": digest(output)})
        write_report(artifacts / f"{mode}-structure.json", {"status": "passed"})
        write_report(artifacts / f"{mode}-layout-audit.json", {"texts": [], "manualReview": [], "missingBlockInstancePaths": []})
        write_report(artifacts / "logical-flow-report.json", {"rows": [{"actualHeight": 11, "availableHeight": 10}] if overflow else []})
        (artifacts / f"{mode}-candidate.jsonl").write_text(json.dumps({"recordId": "new", "handle": "2", "plainText": "Pump", "rawText": "Pump"}) + "\n", encoding="utf-8")
        if mode == "bilingual":
            write_report(artifacts / "bilingual-pairs.json", {"pairs": [{"recordId": "a", "targetHandle": "2"}], "unresolved": []})

    def test_each_mode_uses_one_import_process_and_requires_visual_evidence(self):
        for mode in ("replace", "bilingual"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                job, config, translations = self.fixture(root, mode)
                def run(operation, path, working, host):
                    self.assertEqual("import", operation)
                    self.native_result(job, json.loads(path.read_text(encoding="utf-8")))
                    return 0
                with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(cad_translate, "run_once", side_effect=run) as process:
                    self.assertEqual(0, cad_translate.run_import(job, translations, root))
                process.assert_called_once()
                report = cad_translate.summarize_audit(job)
                self.assertEqual("passed", report["status"])
                self.assertFalse(report["deliveryReady"])
                Path(config["outputPath"]).write_bytes(b"tampered")
                self.assertEqual("failed", cad_translate.summarize_audit(job)["status"])

    def test_estimated_overflow_uses_final_visual_review_without_duplicate_assessment(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            def run(op, path, working, host):
                self.native_result(job, json.loads(path.read_text(encoding="utf-8")), overflow=True)
                return 0
            with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(cad_translate, "run_once", side_effect=run):
                self.assertEqual(0, cad_translate.run_import(job, translations, root))
            self.assertTrue(Path(config["outputPath"]).exists())
            self.assertFalse(cad_translate.summarize_audit(job)["deliveryReady"])
            self.visual_review(job, config, "passed_with_warnings")
            summary = cad_translate.summarize_audit(job)
            self.assertTrue(summary["deliveryReady"])
            self.assertEqual(1, summary["segmentOverflowCount"])

    def visual_review(self, job, config, status):
        for name in ("source.png", "candidate.png"):
            (job / "artifacts" / name).write_bytes(b"test evidence placeholder")
        review = {"status": status, "sourceSha256": config["sourceSha256"],
            "candidateSha256": digest(config["outputPath"]), "images": ["source.png", "candidate.png"],
            "warnings": ["One readable label has a cosmetic line break."] if status == "passed_with_warnings" else [],
            "blockingIssues": []}
        write_report(job / "artifacts" / f"{config['outputMode']}-visual-review.json", review)
        return review

    def test_both_modes_deliver_disclosed_minor_issues_but_not_explicit_blockers(self):
        for mode in ("replace", "bilingual"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp)
                job, config, translations=self.fixture(root,mode)
                def run(op,path,working,host):
                    self.native_result(job,json.loads(path.read_text(encoding="utf-8")))
                    return 0
                with mock.patch.object(cad_translate,"require_ready"), mock.patch.object(cad_translate,"run_once",side_effect=run):
                    self.assertEqual(0,cad_translate.run_import(job,translations,root))
                if mode == "bilingual":
                    write_report(job / "artifacts/bilingual-layout-audit.json", {
                        "reviewRecordIds": ["a"], "riskCounts": {"medium": 1, "high": 0},
                        "risks": [{"code": "saved-text-overlap", "recordId": "a"}], "manualReview": []})
                review=self.visual_review(job,config,"passed_with_warnings")
                summary=cad_translate.summarize_audit(job)
                if mode == "bilingual":
                    self.assertEqual(["a"], summary["layoutReviewRecordIds"])
                    self.assertEqual(1, summary["layoutRiskCounts"]["medium"])
                self.assertTrue(summary["deliveryReady"])
                self.assertEqual("ready_with_warnings",summary["deliveryStatus"])
                self.assertEqual(review["warnings"],summary["warnings"])
                review["warnings"]=[]
                write_report(job / "artifacts" / f"{mode}-visual-review.json",review)
                self.assertFalse(cad_translate.summarize_audit(job)["deliveryReady"])
                review["status"]="passed"
                review["blockingIssues"]=["A capacity value is hidden by another label."]
                write_report(job / "artifacts" / f"{mode}-visual-review.json",review)
                self.assertFalse(cad_translate.summarize_audit(job)["deliveryReady"])
                self.assertEqual("blocked",cad_translate.summarize_audit(job)["deliveryStatus"])

    def test_layout_flags_share_final_review_and_still_block_known_defects_or_stale_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            job,config,translations=self.fixture(root)
            def run(op,path,working,host):
                self.native_result(job,json.loads(path.read_text(encoding="utf-8")))
                write_report(job / "artifacts" / "replace-layout-audit.json", {"texts":[],"missingBlockInstancePaths":[],
                    "manualReview":[{"code":"text-overlap","recordId":"a","otherRecordId":"b","level":"high"}]})
                return 0
            with mock.patch.object(cad_translate,"require_ready"), mock.patch.object(cad_translate,"run_once",side_effect=run):
                self.assertEqual(0,cad_translate.run_import(job,translations,root))
            self.assertFalse(cad_translate.summarize_audit(job)["deliveryReady"])
            review=self.visual_review(job,config,"passed")
            summary=cad_translate.summarize_audit(job)
            self.assertTrue(summary["deliveryReady"])
            self.assertEqual(["a","b"],summary["layoutReviewRecordIds"])
            review["blockingIssues"]=["Confirmed label overlap hides a dimension."]
            write_report(job / "artifacts" / "replace-visual-review.json",review)
            self.assertFalse(cad_translate.summarize_audit(job)["deliveryReady"])
            review["blockingIssues"]=[]
            write_report(job / "artifacts" / "replace-visual-review.json",review)
            Path(config["outputPath"]).write_bytes(b"changed after review")
            self.assertFalse(cad_translate.summarize_audit(job)["deliveryReady"])

    def test_english_with_cjk_punctuation_is_not_untranslated_chinese(self):
        with tempfile.TemporaryDirectory() as tmp:
            job,config,translations=self.fixture(Path(tmp))
            row=json.loads(translations.read_text(encoding="utf-8"))
            for target in ("Pump（standby）", "Pump，standby。", "Pump － standby"):
                with self.subTest(target=target):
                    row["translatedText"]=target
                    translations.write_text(json.dumps(row,ensure_ascii=False),encoding="utf-8")
                    result=cad_translate.check_translations(Path(config["manifestPath"]),translations,output_mode="replace")
                    self.assertEqual("passed",result["status"])
            row["translatedText"]="Pump（备用泵）"
            translations.write_text(json.dumps(row,ensure_ascii=False),encoding="utf-8")
            self.assertEqual("failed",cad_translate.check_translations(Path(config["manifestPath"]),translations,output_mode="replace")["status"])

    def test_warning_review_cannot_override_native_integrity_failure(self):
        for mode in ("replace","bilingual"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp)
                job,config,translations=self.fixture(root,mode)
                def run(op,path,working,host):
                    self.native_result(job,json.loads(path.read_text(encoding="utf-8")))
                    write_report(job / "artifacts" / f"{mode}-structure.json",{"status":"failed"})
                    return 0
                with mock.patch.object(cad_translate,"require_ready"), mock.patch.object(cad_translate,"run_once",side_effect=run):
                    with self.assertRaises(ValueError): cad_translate.run_import(job,translations,root)
                self.assertFalse(Path(config["outputPath"]).exists())
                self.assertTrue((job / "artifacts" / f"{mode}-staged.dwg").exists())

    def test_sealed_mode_and_direction_cannot_be_changed(self):
        for field, value in (("outputMode", "bilingual"), ("targetLanguage", "zh"), ("pipelineVersion", "1.0")):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as tmp:
                job, config, _ = self.fixture(Path(tmp))
                config[field] = value
                with self.assertRaisesRegex(RuntimeError, "changed after export"):
                    cad_translate.verify_export_seal(job, config)

    def test_final_replacement_detects_lost_chinese_and_code_is_not_translation(self):
        with tempfile.TemporaryDirectory() as tmp:
            job, config, translations = self.fixture(Path(tmp), "replace", "en", "zh-CN")
            candidate = job / "artifacts" / "replace-candidate.jsonl"
            candidate.write_text(json.dumps({"recordId": "a", "plainText": "Pump", "rawText": "Pump"}), encoding="utf-8")
            self.assertEqual("failed", cad_translate.get_pipeline("replace").check_candidate(candidate, None)["status"])
        for mode in ("replace", "bilingual"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                job, config, translations = self.fixture(Path(tmp), mode)
                value = json.loads(translations.read_text(encoding="utf-8"))
                value["translatedText"] = "HCQ2000"
                translations.write_text(json.dumps(value), encoding="utf-8")
                self.assertEqual("failed", cad_translate.check_translations(Path(config["manifestPath"]), translations, output_mode=mode)["status"])

    def test_reverse_direction_requires_chinese_translation(self):
        for mode in ("replace", "bilingual"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                job, config, translations = self.fixture(Path(tmp), mode, "en", "zh-CN")
                self.assertEqual("passed", cad_translate.check_translations(Path(config["manifestPath"]), translations, output_mode=mode)["status"])
                value = json.loads(translations.read_text(encoding="utf-8"))
                value["translatedText"] = "Pump"
                translations.write_text(json.dumps(value), encoding="utf-8")
                self.assertEqual("failed", cad_translate.check_translations(Path(config["manifestPath"]), translations, output_mode=mode)["status"])

    def test_bilingual_cannot_pass_using_only_a_latin_code_or_unverified_snapshot(self):
        with tempfile.TemporaryDirectory() as tmp:
            job, config, translations = self.fixture(Path(tmp), "bilingual")
            snapshot = job / "artifacts" / "bilingual-candidate.jsonl"
            snapshot.write_text(json.dumps({"recordId": "a", "plainText": "泵 HCQ2000"}) + "\n", encoding="utf-8")
            report = cad_translate.check_exported_candidate_language(snapshot, job / "artifacts" / "check.json", "bilingual")
            self.assertEqual("failed", report["status"])

    def test_duplicate_requests_are_fanned_out_without_merging_different_contexts(self):
        with tempfile.TemporaryDirectory() as tmp:
            job, config, translations = self.fixture(Path(tmp))
            rows = [{"recordId": str(i), "inputHash": str(i), "plainText": "泵", "protectedTokens": [],
                "properties": {"layer": "equipment" if i < 9 else "legend"}} for i in range(10)]
            Path(config["manifestPath"]).write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows), encoding="utf-8")
            report = cad_translate.prepare_translation_worklist(job)
            self.assertEqual(2, report["translationRecordCount"])
            self.assertEqual(8, report["deduplicatedRecordCount"])
            compact = job / "exchange" / "compact.jsonl"
            compact.write_text(' {"recordId":"0","translatedText":"Pump"}\n{"recordId":"9","translatedText":"Pump"}\n', encoding="utf-8")
            report = cad_translate.assemble_translations(job, compact)
            self.assertEqual(10, report["records"])

    def test_stale_result_is_rejected_before_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, _ = self.fixture(root)
            with mock.patch.object(cad_translate.subprocess, "Popen") as process:
                with self.assertRaises(FileExistsError):
                    cad_translate.run_once("export", job / "config" / "export-job.json", Path(config["workingPath"]), root)
            process.assert_not_called()

    def test_invalid_timeout_is_rejected_before_launch(self):
        with mock.patch.object(cad_translate.subprocess, "Popen") as process:
            with self.assertRaises(ValueError):
                cad_translate.run_once("export", Path("missing"), Path("missing"), Path("missing"), 0)
            process.assert_not_called()
