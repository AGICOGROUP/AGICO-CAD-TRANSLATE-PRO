import importlib.util
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import subprocess
from contextlib import redirect_stdout
import io
import types


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "cad_translate.py"
SPEC = importlib.util.spec_from_file_location("cad_translate", SCRIPT)
cad_translate = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(cad_translate)


class CadTranslateDryTests(unittest.TestCase):
    @staticmethod
    def write_manifest(job, records):
        exchange = job / "exchange"
        exchange.mkdir(parents=True, exist_ok=True)
        manifest = exchange / "manifest.input.jsonl"
        manifest.write_text(
            "".join(json.dumps(record, ensure_ascii=False) + "\n" for record in records),
            encoding="utf-8",
        )
        return manifest

    @staticmethod
    def manifest_record(record_id, text, markers=None):
        return {
            "schemaVersion": "1.0",
            "recordId": record_id,
            "inputHash": f"hash-{record_id}",
            "plainText": text,
            "protectedTokens": [
                {"marker": marker} for marker in (markers or [])
            ],
        }

    def test_prepare_translation_worklist_contains_only_cjk_and_bounded_batches(self):
        with tempfile.TemporaryDirectory() as directory:
            job = Path(directory)
            self.write_manifest(
                job,
                [
                    self.manifest_record("zh-1", "第一段中文"),
                    self.manifest_record("en", "English only"),
                    self.manifest_record("zh-2", "第二段中文"),
                ],
            )

            summary = cad_translate.prepare_translation_worklist(job, max_source_chars=6)

            parts = sorted((job / "exchange" / "translation-worklist").glob("part-*.jsonl"))
            work_records = [
                json.loads(line)
                for part in parts
                for line in part.read_text(encoding="utf-8").splitlines()
                if line.strip()
            ]
            self.assertEqual(2, summary["translationRecordCount"])
            self.assertEqual(1, summary["passthroughRecordCount"])
            self.assertEqual(2, len(parts))
            self.assertEqual(["zh-1", "zh-2"], [record["recordId"] for record in work_records])
            self.assertEqual({"recordId", "sourceText"}, set(work_records[0]))
            self.assertNotIn("English only", "\n".join(part.read_text(encoding="utf-8") for part in parts))

    def test_translation_worklist_includes_cjk_punctuation_and_extension_b(self):
        with tempfile.TemporaryDirectory() as directory:
            job = Path(directory)
            self.write_manifest(
                job,
                [
                    self.manifest_record("punctuation", "：（）、《》。"),
                    self.manifest_record("extension-b", "\U00020000"),
                    self.manifest_record("ascii", "English: (OK)."),
                ],
            )

            summary = cad_translate.prepare_translation_worklist(job)

            self.assertEqual(2, summary["translationRecordCount"])
            self.assertEqual(1, summary["passthroughRecordCount"])

    def test_assemble_translations_fills_passthrough_and_restores_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            job = Path(directory)
            self.write_manifest(
                job,
                [
                    self.manifest_record("zh", "中文 [[0001]]", ["[[0001]]"]),
                    self.manifest_record("en", "Already English"),
                ],
            )
            translated = job / "translated.jsonl"
            translated.write_text(
                json.dumps({"recordId": "zh", "translatedText": "English [[0001]]"}) + "\n",
                encoding="utf-8",
            )

            summary = cad_translate.assemble_translations(job, translated)

            output = [
                json.loads(line)
                for line in (job / "exchange" / "translations.output.jsonl")
                .read_text(encoding="utf-8")
                .splitlines()
            ]
            self.assertEqual("passed", summary["status"])
            self.assertEqual(2, summary["records"])
            self.assertEqual("English [[0001]]", output[0]["translatedText"])
            self.assertEqual("Already English", output[1]["translatedText"])
            self.assertEqual("hash-en", output[1]["inputHash"])
            self.assertEqual("approved", output[1]["reviewStatus"])

    def test_assemble_translations_caps_missing_record_diagnostics(self):
        with tempfile.TemporaryDirectory() as directory:
            job = Path(directory)
            self.write_manifest(
                job,
                [self.manifest_record(f"zh-{index:02d}", "中文") for index in range(30)],
            )
            translated = job / "translated.jsonl"
            translated.write_text("", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "missing_count=30") as raised:
                cad_translate.assemble_translations(job, translated)

            message = str(raised.exception)
            self.assertIn("zh-19", message)
            self.assertNotIn("zh-20", message)

    def test_audit_summary_is_bounded_and_omits_full_maps(self):
        with tempfile.TemporaryDirectory() as directory:
            job = Path(directory)
            artifacts = job / "artifacts"
            artifacts.mkdir(parents=True)
            (artifacts / "layout-audit.json").write_text(
                json.dumps(
                    {
                        "riskCounts": {"low": 5, "medium": 3, "high": 2},
                        "noteColumnCount": 4,
                        "tableCellCount": 12,
                        "missingBlockInstancePaths": [f"block-{i}" for i in range(30)],
                        "handleMap": {f"handle-{i}": {"large": "payload"} for i in range(100)},
                        "texts": list(range(200)),
                        "risks": list(range(40)),
                        "manualReview": list(range(35)),
                    }
                ),
                encoding="utf-8",
            )
            (artifacts / "logical-flow-report.json").write_text(
                json.dumps(
                    {
                        "replacedRecords": 40,
                        "composedObjects": 8,
                        "rows": [
                            {"actualHeight": 11, "availableHeight": 10},
                            {"actualHeight": 9, "availableHeight": 10},
                            {"kind": "existing-note-column-preserved", "actualHeight": 20, "availableHeight": 10},
                        ],
                    }
                ),
                encoding="utf-8",
            )

            summary = cad_translate.summarize_audit(job)

            encoded = json.dumps(summary)
            self.assertLess(len(encoded), 2048)
            self.assertNotIn("handleMap", encoded)
            self.assertEqual(30, summary["layout"]["missingBlockInstanceCount"])
            self.assertEqual(20, len(summary["layout"]["missingBlockInstanceExamples"]))
            self.assertEqual(1, summary["logicalFlow"]["segmentOverflowCount"])
            self.assertEqual("failed", summary["status"])
            self.assertIn("segment_overflow", summary["gate"]["errorCodes"])
            self.assertIn("visualReviewTargets", summary)
            self.assertTrue((artifacts / "visual-review-targets.json").is_file())

    def test_skill_contract_covers_verified_large_note_panel_flow(self):
        skill_text = (
            Path(__file__).resolve().parents[1] / "SKILL.md"
        ).read_text(encoding="utf-8")

        self.assertIn("prepare-translations", skill_text)
        self.assertIn("assemble-translations", skill_text)
        self.assertIn("audit-summary", skill_text)
        self.assertIn("Never open or print the complete", skill_text)
        self.assertIn("Import performs the pre-import language gate internally", skill_text)
        self.assertIn("changed, composed, or high-risk", skill_text)
        self.assertIn("artifacts/postcomposition-language-check.json", skill_text)
        self.assertIn("Treat any printable-frame overflow as a hard failure", skill_text)
        self.assertIn("Reject the candidate if any changed text crosses it", skill_text)
        self.assertLess(len(skill_text.split()), 700)
        self.assertNotIn("complete coverage", skill_text)
        self.assertIn("DWG", skill_text)
        self.assertIn("DXF", skill_text)

        visual_audit = (
            Path(__file__).resolve().parents[1] / "references" / "visual-audit.md"
        ).read_text(encoding="utf-8")
        self.assertIn("accoreconsole.exe", visual_audit)
        self.assertIn("changed, composed, or high-risk", visual_audit)
        self.assertIn("contact sheet", visual_audit)
        self.assertIn("newSevereOverlapObserved", visual_audit)
        self.assertIn("keepOutIntrusionCount", visual_audit)
        self.assertIn("shortLabelCompositionCount", visual_audit)

        exchange_format = (
            Path(__file__).resolve().parents[1] / "references" / "exchange-format.md"
        ).read_text(encoding="utf-8")
        self.assertIn("model-facing worklist", exchange_format)
        self.assertNotIn("automatic second scan", exchange_format)

    def test_skill_contract_uses_single_pass_layout_v2(self):
        skill_root = Path(__file__).resolve().parents[1]
        skill_text = (skill_root / "SKILL.md").read_text(encoding="utf-8")
        importer = (
            skill_root
            / "src"
            / "cad"
            / "CadTranslation.AutoCAD2025"
            / "Importer.cs"
        ).read_text(encoding="utf-8")
        note_layout = (
            skill_root
            / "src"
            / "cad"
            / "CadTranslation.AutoCAD2025"
            / "NoteColumnLayout.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("one source-derived allowed region", skill_text)
        self.assertIn("one fit and one audit", skill_text)
        self.assertIn("wrap, compress width, then reduce height", skill_text)
        self.assertLess(len(skill_text.split()), 450)
        self.assertIn("LayoutOptimizerV2.Optimize", importer)
        self.assertNotIn("while (layoutAudit.PassIndex", importer)
        self.assertIn(
            "topology[0].Region is null && allowedOverride is null",
            note_layout,
        )

    def test_cement_glossary_is_packaged_for_authoritative_term_selection(self):
        skill_root = Path(__file__).resolve().parents[1]
        glossary_path = skill_root / "references" / "cement-industry-glossary.md"

        self.assertTrue(glossary_path.is_file())
        glossary = glossary_path.read_text(encoding="utf-8")
        self.assertIn("共整理 1293 条中英对应记录", glossary)
        self.assertIn("| 水泥粉磨 | Cement Mill |", glossary)
        self.assertIn("| 袋式收尘器 | Bag Filter |", glossary)

        skill_text = (skill_root / "SKILL.md").read_text(encoding="utf-8")
        self.assertIn("cement-industry-glossary.md", skill_text)
        self.assertIn("longest complete Chinese term", skill_text)
        self.assertIn("user-supplied project glossary", skill_text)
        self.assertIn("ASCII word boundary", skill_text)

    def test_fixed_labels_scale_before_any_move(self):
        skill_root = Path(__file__).resolve().parents[1]
        source = (
            skill_root
            / "src"
            / "cad"
            / "CadTranslation.AutoCAD2025"
            / "FixedLabelLayout.cs"
        ).read_text(encoding="utf-8")

        self.assertNotIn("allowed.Contains(bounds) || TryMoveInside", source)
        self.assertGreater(
            source.index("moved = TryMoveInside"),
            source.rindex("foreach (double scale"),
        )

    def test_packaged_plugin_is_present_and_matches_release_build_when_available(self):
        skill_root = Path(__file__).resolve().parents[1]
        source_root = skill_root / "src" / "cad"
        autocad_root = Path(r"D:\AutoCAD 2027\AutoCAD 2027")
        framework = "net10.0-windows" if autocad_root.is_dir() else "net8.0-windows"
        self.assertTrue((source_root / "CadTranslation.AutoCAD2025" / "Importer.cs").is_file())
        self.assertTrue((source_root / "CadTranslation.Core" / "TranslationValidator.cs").is_file())
        release_root = (
            source_root
            / "CadTranslation.AutoCAD2025"
            / "bin"
            / "x64"
            / "Release"
            / framework
        )
        if not release_root.is_dir():
            command = [
                "dotnet", "build",
                str(source_root / "CadTranslation.AutoCAD2025" / "CadTranslation.AutoCAD2025.csproj"),
                "-c", "Release", "-p:Platform=x64",
            ]
            if autocad_root.is_dir():
                command.extend(["-p:AutoCADRelease=2027", f"-p:AutoCADDir={autocad_root}"])
            completed = subprocess.run(
                command,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
            )
            self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertTrue(release_root.is_dir())
        packaged_root = skill_root / "assets" / "plugin"

        for name in (
            "CadTranslation.AutoCAD2025.dll",
            "CadTranslation.Contracts.dll",
            "CadTranslation.Core.dll",
        ):
            with self.subTest(name=name):
                packaged_path = packaged_root / name
                self.assertGreater(packaged_path.stat().st_size, 1024)
                built = hashlib.sha256((release_root / name).read_bytes()).hexdigest()
                packaged = hashlib.sha256(packaged_path.read_bytes()).hexdigest()
                self.assertEqual(built, packaged)

    @staticmethod
    def write_translation_fixture(root, source_text, translated_text):
        manifest = root / "manifest.jsonl"
        manifest.write_text(
            json.dumps(
                {
                    "recordId": "a",
                    "inputHash": "h",
                    "plainText": source_text,
                    "protectedTokens": [{"marker": "⟦P0001⟧"}],
                },
                ensure_ascii=False,
            )
            + "\n",
            encoding="utf-8",
        )
        translations = root / "translations.jsonl"
        translations.write_text(
            json.dumps(
                {
                    "schemaVersion": "1.0",
                    "recordId": "a",
                    "inputHash": "h",
                    "translatedText": translated_text,
                    "reviewStatus": "approved",
                    "reason": "test",
                },
                ensure_ascii=False,
            )
            + "\n",
            encoding="utf-8",
        )
        return manifest, translations

    def test_check_translations_reports_each_chinese_residual(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest, translations = self.write_translation_fixture(
                root,
                "基础详图⟦P0001⟧",
                "Foundation 基础 Detail ⟦P0001⟧",
            )
            report = cad_translate.check_translations(manifest, translations)
            self.assertEqual(report["status"], "failed")
            self.assertEqual(report["chineseResidualCount"], 1)
            self.assertEqual(report["chineseResidualRecordIds"], ["a"])

    def test_postcomposition_language_check_scans_plain_and_raw_text(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.jsonl"
            manifest.write_text(
                "\n".join(
                    (
                        json.dumps({"recordId": "plain", "plainText": "中文", "rawText": "English"}, ensure_ascii=False),
                        json.dumps({"recordId": "raw", "plainText": "English", "rawText": "残留"}, ensure_ascii=False),
                    )
                )
                + "\n",
                encoding="utf-8",
            )
            report_path = root / "report.json"

            report = cad_translate.check_exported_candidate_language(manifest, report_path)

            self.assertEqual("failed", report["status"])
            self.assertEqual(["plain"], report["plainTextChineseResidualRecordIds"])
            self.assertEqual(["raw"], report["rawTextChineseResidualRecordIds"])
            self.assertTrue(report_path.is_file())

    def test_language_gate_rejects_fullwidth_punctuation_and_extension_b(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.jsonl"
            manifest.write_text(
                "\n".join(
                    (
                        json.dumps({"recordId": "punct", "plainText": "English：OK", "rawText": "English"}, ensure_ascii=False),
                        json.dumps({"recordId": "ext-b", "plainText": "English", "rawText": "\U00020000"}, ensure_ascii=False),
                    )
                )
                + "\n",
                encoding="utf-8",
            )

            report = cad_translate.check_exported_candidate_language(manifest, root / "report.json")

            self.assertEqual("failed", report["status"])
            self.assertEqual(["punct"], report["plainTextChineseResidualRecordIds"])
            self.assertEqual(["ext-b"], report["rawTextChineseResidualRecordIds"])

    def test_check_translations_passes_english_and_ignores_protected_markers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest, translations = self.write_translation_fixture(
                root,
                "基础详图⟦P0001⟧",
                "Foundation Detail ⟦P0001⟧",
            )
            report = cad_translate.check_translations(manifest, translations)
            self.assertEqual(report["status"], "passed")
            self.assertEqual(report["chineseResidualCount"], 0)
            self.assertEqual(report["translatedSourceRecords"], 1)

    def test_bilingual_translation_gate_requires_chinese_and_english(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.jsonl"
            manifest.write_text(
                json.dumps({
                    "recordId": "a", "inputHash": "h", "plainText": "基础详图",
                    "protectedTokens": [],
                }, ensure_ascii=False) + "\n",
                encoding="utf-8",
            )
            translations = root / "translations.jsonl"

            def write(text):
                translations.write_text(json.dumps({
                    "schemaVersion": "1.0", "recordId": "a", "inputHash": "h",
                    "translatedText": text, "reviewStatus": "approved", "reason": "test",
                }, ensure_ascii=False) + "\n", encoding="utf-8")

            write(r"基础详图\PFoundation Detail")
            passed = cad_translate.check_translations(manifest, translations, output_mode="bilingual")
            self.assertEqual("passed", passed["status"])
            self.assertEqual(1, passed["bilingualRecordCount"])

            write("基础详图")
            failed = cad_translate.check_translations(manifest, translations, output_mode="bilingual")
            self.assertEqual("failed", failed["status"])
            self.assertEqual(["a"], failed["missingEnglishRecordIds"])

    def test_output_mode_defaults_to_replace_and_is_sealed_in_job_config(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"; source.write_bytes(b"drawing")
            job = root / "job"

            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")

            self.assertEqual("replace", config["outputMode"])
            self.assertEqual("replace", cad_translate.read_output_mode(job))

    def test_legacy_english_output_mode_normalizes_to_replace(self):
        with tempfile.TemporaryDirectory() as directory:
            job = Path(directory)
            (job / "config").mkdir()
            (job / "config" / "output-mode.json").write_text(
                json.dumps({"schemaVersion": "1.0", "outputMode": "english"}),
                encoding="utf-8",
            )

            self.assertEqual("replace", cad_translate.read_output_mode(job))

    def test_output_modes_dispatch_to_distinct_pipeline_implementations(self):
        replace = cad_translate.get_pipeline("replace")
        bilingual = cad_translate.get_pipeline("bilingual")

        self.assertEqual("ReplacePipeline", type(replace).__name__)
        self.assertEqual("BilingualPipeline", type(bilingual).__name__)
        self.assertIsNot(type(replace), type(bilingual))

    def test_translation_gate_calls_only_selected_pipeline(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest, translations = self.write_translation_fixture(root, "Name", "Equipment Name")
            replace = cad_translate.get_pipeline("replace")
            bilingual = cad_translate.get_pipeline("bilingual")
            with mock.patch.object(replace, "check_translations", wraps=replace.check_translations) as replace_gate, \
                 mock.patch.object(bilingual, "check_translations", wraps=bilingual.check_translations) as bilingual_gate, \
                 mock.patch.object(cad_translate, "validate_complete_translations", return_value=1):
                cad_translate.check_translations(manifest, translations, output_mode="replace")
                replace_gate.assert_called_once()
                bilingual_gate.assert_not_called()

    def test_bilingual_mode_is_stored_in_strict_autocad_job_config(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"; source.write_bytes(b"drawing")
            job = root / "job"

            config = cad_translate.prepare_export_job(
                source, job, "zh-CN", "en", output_mode="bilingual"
            )

            self.assertEqual("bilingual", config["outputMode"])
            self.assertEqual("bilingual", cad_translate.read_output_mode(job))
            self.assertTrue((job / "config" / "output-mode.json").is_file())

    def test_check_translations_command_writes_failure_report_and_nonzero_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            job = root / "job"
            (job / "exchange").mkdir(parents=True)
            manifest, translations = self.write_translation_fixture(
                root,
                "基础详图⟦P0001⟧",
                "基础详图⟦P0001⟧",
            )
            manifest.replace(job / "exchange" / "manifest.input.jsonl")
            report_path = job / "artifacts" / "language-check.json"
            with redirect_stdout(io.StringIO()):
                code = cad_translate.main(
                    [
                        "check-translations",
                        "--job",
                        str(job),
                        "--translations",
                        str(translations),
                        "--report",
                        str(report_path),
                    ]
                )
            self.assertEqual(code, 1)
            self.assertEqual(
                json.loads(report_path.read_text(encoding="utf-8"))[
                    "chineseResidualCount"
                ],
                1,
            )

    def test_decode_accepts_utf16_coreconsole_output(self):
        self.assertEqual(cad_translate.decode_output("完成".encode("utf-16")), "完成")

    def test_decode_accepts_gb18030_coreconsole_output(self):
        self.assertEqual(cad_translate.decode_output("完成".encode("gb18030")), "完成")

    def test_timeout_terminates_process_tree_then_kills_parent_if_needed(self):
        class Process:
            pid = 4321
            killed = False
            waits = 0
            def poll(self): return None
            def wait(self, timeout):
                self.waits += 1
                if self.waits == 1: raise subprocess.TimeoutExpired("accoreconsole", timeout)
            def kill(self): self.killed = True
        process = Process()
        with mock.patch.object(cad_translate.subprocess, "run") as taskkill:
            cad_translate.terminate_process_tree(process)
        taskkill.assert_called_once_with(["taskkill", "/pid", "4321", "/t", "/f"], check=False, capture_output=True)
        self.assertTrue(process.killed)

    def test_run_once_retains_timeout_log_without_starting_cad(self):
        class Process:
            pid = 99
            returncode = -9
            def communicate(self, timeout=None):
                if timeout is not None: raise subprocess.TimeoutExpired("accoreconsole", timeout)
                return "超时".encode("utf-16"), b"stderr"
            def poll(self): return None
            def wait(self, timeout): return None
            def kill(self): raise AssertionError("taskkill should have stopped this fake")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_dir = root / "job" / "config"; config_dir.mkdir(parents=True)
            (root / "job" / "artifacts").mkdir()
            working = root / "job" / "working.dwg"; working.write_bytes(b"x")
            config = config_dir / "export-job.json"; config.write_text("{}", encoding="utf-8")
            with mock.patch.object(cad_translate.subprocess, "Popen", return_value=Process()), mock.patch.object(cad_translate.subprocess, "run"), mock.patch.object(cad_translate.time, "monotonic", side_effect=[0, 121]):
                code = cad_translate.run_once("export", config, working, root, timeout_seconds=120)
            self.assertEqual(code, -9)
            self.assertIn("超时", (root / "job" / "artifacts" / "export-timeout.log").read_text(encoding="utf-8"))
            self.assertIn("_.NETLOAD", (config_dir / "export.scr").read_text(encoding="utf-8"))

    def test_run_once_accepts_success_envelope_after_autocad_quit_timeout(self):
        class Process:
            pid = 100
            returncode = -9
            calls = 0
            def communicate(self, timeout=None):
                self.calls += 1
                if self.calls == 1: raise subprocess.TimeoutExpired("accoreconsole", timeout)
                return b"", b""
            def poll(self): return None
            def wait(self, timeout): return None
            def kill(self): raise AssertionError("taskkill should have stopped this fake")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_dir = root / "job" / "config"; config_dir.mkdir(parents=True)
            artifacts = root / "job" / "artifacts"; artifacts.mkdir()
            working = root / "job" / "working.dwg"; working.write_bytes(b"x")
            result = artifacts / "import-result.json"
            result.write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
            config = config_dir / "import-job.json"
            config.write_text(json.dumps({"resultPath": str(result)}), encoding="utf-8")
            with mock.patch.object(cad_translate.subprocess, "Popen", return_value=Process()), mock.patch.object(cad_translate.subprocess, "run"):
                code = cad_translate.run_once("import", config, working, root)
            self.assertEqual(0, code)

    def test_run_once_polls_result_envelope_instead_of_waiting_full_stage_timeout(self):
        timeouts = []
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_dir = root / "job" / "config"; config_dir.mkdir(parents=True)
            artifacts = root / "job" / "artifacts"; artifacts.mkdir()
            working = root / "job" / "working.dwg"; working.write_bytes(b"x")
            result = artifacts / "import-result.json"

            class Process:
                pid = 102
                returncode = -9
                calls = 0
                def communicate(self, timeout=None):
                    self.calls += 1
                    timeouts.append(timeout)
                    if self.calls == 1:
                        result.write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
                        raise subprocess.TimeoutExpired("accoreconsole", timeout)
                    return b"", b""
                def poll(self): return None
                def wait(self, timeout): return None
                def kill(self): return None

            config = config_dir / "import-job.json"
            config.write_text(json.dumps({"resultPath": str(result)}), encoding="utf-8")
            with mock.patch.object(cad_translate.subprocess, "Popen", return_value=Process()), mock.patch.object(cad_translate.subprocess, "run"):
                code = cad_translate.run_once("import", config, working, root)

            self.assertEqual(0, code)
            self.assertLessEqual(timeouts[0], 1)

    def test_large_import_is_not_terminated_at_old_two_minute_limit(self):
        class Process:
            pid = 101
            returncode = 0
            calls = 0

            def communicate(self, timeout=None):
                self.calls += 1
                if self.calls == 1 and timeout < 300:
                    raise subprocess.TimeoutExpired("accoreconsole", timeout)
                return b"completed", b""

            def poll(self):
                return None

            def wait(self, timeout):
                return None

            def kill(self):
                return None

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_dir = root / "job" / "config"
            config_dir.mkdir(parents=True)
            (root / "job" / "artifacts").mkdir()
            working = root / "job" / "working.dwg"
            working.write_bytes(b"x")
            config = config_dir / "import-job.json"
            config.write_text("{}", encoding="utf-8")

            with mock.patch.object(
                cad_translate.subprocess, "Popen", return_value=Process()
            ), mock.patch.object(cad_translate.subprocess, "run"):
                code = cad_translate.run_once("import", config, working, root)

            self.assertEqual(0, code)
            self.assertFalse((root / "job" / "artifacts" / "import-timeout.log").exists())

    def test_run_once_accepts_configurable_stage_timeout(self):
        observed = []

        class Process:
            pid = 103
            returncode = 0
            def communicate(self, timeout=None):
                observed.append(timeout)
                return b"completed", b""
            def poll(self): return 0

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_dir = root / "job" / "config"; config_dir.mkdir(parents=True)
            (root / "job" / "artifacts").mkdir()
            working = root / "job" / "working.dwg"; working.write_bytes(b"x")
            config = config_dir / "import-job.json"; config.write_text("{}", encoding="utf-8")
            with mock.patch.object(cad_translate.subprocess, "Popen", return_value=Process()) as popen:
                code = cad_translate.run_once("import", config, working, root, timeout_seconds=1800)

        self.assertEqual(0, code)
        self.assertLessEqual(observed[0], 0.5)
        self.assertNotIn("/language", popen.call_args.args[0])

    def test_profile_prefers_current_autocad_profile(self):
        class Key:
            def __init__(self, path): self.path = path
            def __enter__(self): return self
            def __exit__(self, *_): return False

        def query_value(key, name):
            if name == "CurVer":
                return "ACAD-8101:409", 1
            if name == "" and key.path.endswith("\\Profiles"):
                return "CurrentProfile", 1
            raise OSError("value not found")

        fake = types.SimpleNamespace(
            HKEY_CURRENT_USER=object(),
            OpenKey=lambda _root, path: Key(path),
            QueryValueEx=query_value,
            EnumKey=lambda _key, _index: "FirstProfile",
        )
        with mock.patch.dict("sys.modules", {"winreg": fake}):
            report = cad_translate.profile()

        self.assertEqual("CurrentProfile", report["name"])
        self.assertEqual("current", report["selection"])

    def test_profile_reads_autocad_2027_registry_release(self):
        opened = []

        class Key:
            def __init__(self, path): self.path = path
            def __enter__(self): return self
            def __exit__(self, *_): return False

        def open_key(_root, path):
            opened.append(path)
            return Key(path)

        def query_value(key, name):
            if name == "CurVer":
                return "ACAD-A101:804", 1
            if name == "" and key.path.endswith("\\Profiles"):
                return "<<未命名配置>>", 1
            raise OSError("value not found")

        fake = types.SimpleNamespace(
            HKEY_CURRENT_USER=object(),
            OpenKey=open_key,
            QueryValueEx=query_value,
            EnumKey=lambda _key, _index: "FirstProfile",
        )
        with mock.patch.dict("sys.modules", {"winreg": fake}):
            report = cad_translate.profile("R26.0")

        self.assertEqual("<<未命名配置>>", report["name"])
        self.assertEqual("R26.0", report["release"])
        self.assertEqual(
            [
                r"Software\Autodesk\AutoCAD\R26.0",
                r"Software\Autodesk\AutoCAD\R26.0\ACAD-A101:804\Profiles",
            ],
            opened,
        )

    def test_autocad_release_maps_2027_install_root(self):
        self.assertEqual(
            "R26.0",
            cad_translate.autocad_release(Path(r"D:\AutoCAD 2027\AutoCAD 2027")),
        )

    def test_autocad_2027_uses_trusted_application_plugins_directory(self):
        directory = cad_translate.runtime_plugin_dir(
            Path(r"D:\AutoCAD 2027\AutoCAD 2027")
        )
        self.assertEqual(
            Path(r"C:\Program Files\Autodesk\ApplicationPlugins\CadTranslation2027.bundle\Contents\Windows"),
            directory,
        )

    def test_doctor_is_read_only_and_reports_missing_components(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"
            source.write_bytes(b"dwg")
            report = cad_translate.doctor(source=source, autocad_root=root / "absent")
            self.assertEqual(report["status"], "blocked")
            self.assertTrue(report["source"]["exists"])
            self.assertFalse(report["autocad"]["coreConsoleExists"])
            self.assertFalse(report["profile"]["writable"])
            self.assertEqual(sorted(path.name for path in root.iterdir()), ["source.dwg"])

    def test_export_preparation_creates_only_job_owned_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "原图.dwg"
            source.write_bytes(b"original")
            job = root / "job"
            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")
            self.assertEqual(source.read_bytes(), b"original")
            self.assertEqual((job / "working" / "source.dwg").read_bytes(), b"original")
            self.assertEqual(Path(config["outputPath"]), (job / "results" / "candidate.dwg").resolve())
            self.assertTrue((job / "config" / "export-job.json").is_file())
            self.assertTrue((job / "artifacts").is_dir())
            self.assertTrue((job / "exchange").is_dir())

    def test_export_rejects_unsupported_language_direction(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"; source.write_bytes(b"original")
            with self.assertRaisesRegex(ValueError, "Chinese-to-English"):
                cad_translate.prepare_export_job(source, root / "job", "fr", "en")

    def test_export_preflight_blocks_before_creating_job(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"; source.write_bytes(b"original")
            job = root / "must-not-exist"
            with self.assertRaisesRegex(RuntimeError, "preflight blocked"):
                cad_translate.run_export(source, job, "zh-CN", "en", root / "no-autocad")
            self.assertFalse(job.exists())

    def test_translations_must_cover_each_manifest_record_once(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.jsonl"
            manifest.write_text(json.dumps({"recordId": "a", "inputHash": "h", "plainText": "x"}) + "\n", encoding="utf-8")
            translations = root / "translations.jsonl"
            translations.write_text("", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "missing"):
                cad_translate.validate_complete_translations(manifest, translations)

    def test_translations_reject_invalid_contract_and_protected_marker_order(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "manifest.jsonl"
            manifest.write_text(json.dumps({"recordId": "a", "inputHash": "h", "plainText": "x", "protectedTokens": [{"marker": "[[0001]]"}, {"marker": "[[0002]]"}]}) + "\n", encoding="utf-8")
            translations = root / "translations.jsonl"
            valid = {"schemaVersion": "1.0", "recordId": "a", "inputHash": "h", "translatedText": "A [[0001]] B [[0002]]", "reviewStatus": "approved", "reason": "translated"}
            for changed, message in [
                ({**valid, "schemaVersion": "2.0"}, "schemaVersion"),
                ({**valid, "reviewStatus": "draft"}, "reviewStatus"),
                ({**valid, "reason": 1}, "reason"),
                ({**valid, "translatedText": "A [[0002]] B [[0001]]"}, "protected marker"),
                ({**valid, "translatedText": "A [[0099]] B [[0002]]"}, "protected marker"),
                ({**valid, "translatedText": "A [[0001]]"}, "protected marker"),
            ]:
                translations.write_text(json.dumps(changed) + "\n", encoding="utf-8")
                with self.assertRaisesRegex(ValueError, message):
                    cad_translate.validate_complete_translations(manifest, translations)

    def test_powershell_wrapper_parses_without_running_cad(self):
        wrapper = Path(__file__).resolve().parents[1] / "scripts" / "run.ps1"
        escaped = str(wrapper).replace("'", "''")
        command = f"$tokens=$null;$errors=$null;[Management.Automation.Language.Parser]::ParseFile('{escaped}',[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count){{exit 1}}"
        completed = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True, check=False)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        wrapper_text = wrapper.read_text(encoding="utf-8")
        self.assertIn("CAD_TRANSLATE_PYTHON", wrapper_text)
        self.assertNotIn("codex-primary-runtime", wrapper_text)

    def test_export_writes_seal_only_after_success_envelope_and_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / "source.dwg"; source.write_bytes(b"source"); job = root / "job"
            def successful_export(operation, config_path, working, autocad_root):
                config = json.loads(config_path.read_text(encoding="utf-8"))
                Path(config["manifestPath"]).write_text(json.dumps({"recordId": "a", "inputHash": "h", "plainText": "x"}) + "\n", encoding="utf-8")
                Path(config["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
                return 0
            with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(cad_translate, "run_once", side_effect=successful_export):
                self.assertEqual(cad_translate.run_export(source, job, "zh-CN", "en", root), 0)
            seal = json.loads((job / "artifacts" / "export-seal.json").read_text(encoding="utf-8"))
            self.assertEqual(seal["recordCount"], 1)
            self.assertEqual(seal["jobId"], job.name)

    def test_failed_export_does_not_write_seal(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / "source.dwg"; source.write_bytes(b"source"); job = root / "job"
            with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(cad_translate, "run_once", return_value=1):
                self.assertEqual(cad_translate.run_export(source, job, "zh-CN", "en", root), 1)
            self.assertFalse((job / "artifacts" / "export-seal.json").exists())

    def test_import_rejects_tampered_working_copy_before_runner(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / "source.dwg"; source.write_bytes(b"source"); job = root / "job"
            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")
            manifest = Path(config["manifestPath"]); manifest.write_text(json.dumps({"recordId": "a", "inputHash": "h", "plainText": "x", "protectedTokens": []}) + "\n", encoding="utf-8")
            Path(config["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
            cad_translate.write_export_seal(job, config)
            translations = job / "exchange" / "translations.output.jsonl"; translations.write_text(json.dumps({"schemaVersion": "1.0", "recordId": "a", "inputHash": "h", "translatedText": "y", "reviewStatus": "approved", "reason": "ok"}) + "\n", encoding="utf-8")
            Path(config["workingPath"]).write_bytes(b"tampered")
            with mock.patch.object(cad_translate, "run_once") as runner:
                with self.assertRaisesRegex(RuntimeError, "working copy"):
                    cad_translate.run_import(job, translations, root)
            runner.assert_not_called()

    def test_import_rejects_replaced_manifest_before_runner(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / "source.dwg"; source.write_bytes(b"source"); job = root / "job"
            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")
            manifest = Path(config["manifestPath"]); manifest.write_text(json.dumps({"recordId": "a", "inputHash": "h", "plainText": "x", "protectedTokens": []}) + "\n", encoding="utf-8")
            Path(config["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
            cad_translate.write_export_seal(job, config)
            manifest.write_text(json.dumps({"recordId": "b", "inputHash": "other", "plainText": "changed", "protectedTokens": []}) + "\n", encoding="utf-8")
            translations = job / "exchange" / "translations.output.jsonl"; translations.write_text("", encoding="utf-8")
            with mock.patch.object(cad_translate, "run_once") as runner:
                with self.assertRaisesRegex(RuntimeError, "manifest"):
                    cad_translate.run_import(job, translations, root)
            runner.assert_not_called()

    def test_import_rejects_chinese_residual_before_runner(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"
            source.write_bytes(b"source")
            job = root / "job"
            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")
            manifest = Path(config["manifestPath"])
            manifest.write_text(
                json.dumps(
                    {
                        "recordId": "a",
                        "inputHash": "h",
                        "plainText": "基础详图",
                        "protectedTokens": [],
                    },
                    ensure_ascii=False,
                )
                + "\n",
                encoding="utf-8",
            )
            Path(config["resultPath"]).write_text(
                json.dumps({"status": "succeeded"}), encoding="utf-8"
            )
            cad_translate.write_export_seal(job, config)
            translations = job / "exchange" / "translations.output.jsonl"
            translations.write_text(
                json.dumps(
                    {
                        "schemaVersion": "1.0",
                        "recordId": "a",
                        "inputHash": "h",
                        "translatedText": "Foundation 基础 Detail",
                        "reviewStatus": "approved",
                        "reason": "incomplete",
                    },
                    ensure_ascii=False,
                )
                + "\n",
                encoding="utf-8",
            )
            with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(
                cad_translate, "run_once"
            ) as runner:
                with self.assertRaisesRegex(
                    ValueError, "chinese_residual_count=1"
                ):
                    cad_translate.run_import(job, translations, root)
            runner.assert_not_called()

    def test_failed_import_quarantines_partial_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"; source.write_bytes(b"source")
            job = root / "job"
            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")
            manifest = Path(config["manifestPath"])
            manifest.write_text(
                json.dumps({"recordId": "a", "inputHash": "h", "plainText": "source", "protectedTokens": []}) + "\n",
                encoding="utf-8",
            )
            Path(config["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
            cad_translate.write_export_seal(job, config)
            translations = job / "exchange" / "translations.output.jsonl"
            translations.write_text(
                json.dumps({"schemaVersion": "1.0", "recordId": "a", "inputHash": "h", "translatedText": "target", "reviewStatus": "approved", "reason": "ok"}) + "\n",
                encoding="utf-8",
            )

            def failed_import(_operation, config_path, _working, _autocad_root):
                stage = json.loads(config_path.read_text(encoding="utf-8"))
                Path(stage["outputPath"]).write_bytes(b"partial")
                return 1

            with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(
                cad_translate, "run_once", side_effect=failed_import
            ):
                self.assertEqual(1, cad_translate.run_import(job, translations, root))

            self.assertFalse((job / "results" / "candidate.dwg").exists())
            self.assertEqual(
                b"partial",
                (job / "artifacts" / "failed-import-candidate.dwg").read_bytes(),
            )

    def test_successful_import_runs_guarded_composition_and_publishes_composed_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"
            source.write_bytes(b"source")
            job = root / "job"
            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")
            manifest = Path(config["manifestPath"])
            manifest.write_text(
                json.dumps({"recordId": "a", "inputHash": "h", "plainText": "source", "protectedTokens": []}) + "\n",
                encoding="utf-8",
            )
            Path(config["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
            cad_translate.write_export_seal(job, config)
            translations = root / "external-translations.jsonl"
            translations.write_text(
                json.dumps({"schemaVersion": "1.0", "recordId": "a", "inputHash": "h", "translatedText": "target", "reviewStatus": "approved", "reason": "ok"}) + "\n",
                encoding="utf-8",
            )
            operations = []

            def successful_stages(operation, config_path, working, autocad_root):
                operations.append(operation)
                stage = json.loads(config_path.read_text(encoding="utf-8"))
                if operation == "import":
                    self.assertTrue(Path(stage["translationPath"]).resolve().is_relative_to(job.resolve()))
                    Path(stage["outputPath"]).write_bytes(b"imported")
                    (job / "artifacts" / "layout-audit.json").write_text(
                        json.dumps({"texts": []}), encoding="utf-8"
                    )
                elif operation == "compose":
                    self.assertTrue(Path(stage["translationPath"]).resolve().is_relative_to(job.resolve()))
                    self.assertEqual(cad_translate.sha256(Path(stage["workingPath"])), stage["sourceSha256"])
                    self.assertTrue(Path(stage["workingPath"]).resolve().is_relative_to(job.resolve()))
                    Path(stage["outputPath"]).write_bytes(b"composed")
                    (job / "artifacts" / "logical-flow-report.json").write_text(
                        json.dumps({"composedObjects": 0, "rows": []}), encoding="utf-8"
                    )
                else:
                    self.assertEqual("zh-CN", stage["sourceLanguage"])
                    self.assertEqual("en", stage["targetLanguage"])
                    Path(stage["manifestPath"]).write_text(
                        json.dumps({"recordId": "final", "plainText": "English", "rawText": "English"}) + "\n",
                        encoding="utf-8",
                    )
                Path(stage["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
                return 0

            with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(
                cad_translate, "run_once", side_effect=successful_stages
            ):
                self.assertEqual(cad_translate.run_import(job, translations, root), 0)

            self.assertEqual(["import", "compose", "export"], operations)
            self.assertEqual(b"composed", (job / "results" / "candidate.dwg").read_bytes())
            self.assertEqual(b"imported", (job / "artifacts" / "pre-composition-candidate.dwg").read_bytes())
            final_check = json.loads(
                (job / "artifacts" / "postcomposition-language-check.json").read_text(encoding="utf-8")
            )
            self.assertEqual("passed", final_check["status"])
            self.assertEqual(0, final_check["plainTextChineseResidualCount"])
            self.assertEqual(0, final_check["rawTextChineseResidualCount"])

    def test_import_rejects_composed_segment_overflow_before_publish(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.dwg"; source.write_bytes(b"source")
            job = root / "job"
            config = cad_translate.prepare_export_job(source, job, "zh-CN", "en")
            manifest = Path(config["manifestPath"])
            manifest.write_text(
                json.dumps({"recordId": "a", "inputHash": "h", "plainText": "source", "protectedTokens": []}) + "\n",
                encoding="utf-8",
            )
            Path(config["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
            cad_translate.write_export_seal(job, config)
            translations = job / "exchange" / "translations.output.jsonl"
            translations.write_text(
                json.dumps({"schemaVersion": "1.0", "recordId": "a", "inputHash": "h", "translatedText": "target", "reviewStatus": "approved", "reason": "ok"}) + "\n",
                encoding="utf-8",
            )

            def stages(operation, config_path, working, autocad_root):
                stage = json.loads(config_path.read_text(encoding="utf-8"))
                if operation == "import":
                    Path(stage["outputPath"]).write_bytes(b"imported")
                    (job / "artifacts" / "layout-audit.json").write_text(json.dumps({"texts": []}), encoding="utf-8")
                elif operation == "compose":
                    Path(stage["outputPath"]).write_bytes(b"overflowed")
                    (job / "artifacts" / "logical-flow-report.json").write_text(
                        json.dumps({"composedObjects": 1, "rows": [{"actualHeight": 11, "availableHeight": 10}]}),
                        encoding="utf-8",
                    )
                Path(stage["resultPath"]).write_text(json.dumps({"status": "succeeded"}), encoding="utf-8")
                return 0

            with mock.patch.object(cad_translate, "require_ready"), mock.patch.object(cad_translate, "run_once", side_effect=stages):
                with self.assertRaisesRegex(RuntimeError, "segment overflow"):
                    cad_translate.run_import(job, translations, root)

            self.assertFalse((job / "results" / "candidate.dwg").exists())


if __name__ == "__main__":
    unittest.main()
