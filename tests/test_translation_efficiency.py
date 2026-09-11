import json
import tempfile
import unittest
from pathlib import Path

from test_cad_translate import cad_translate
from translation_work import groups
from replacement_quality import review, layout_review
from job_timing import record


class TranslationEfficiencyTests(unittest.TestCase):
    def test_worklist_omits_duplicate_context_but_keeps_real_variants(self):
        with tempfile.TemporaryDirectory() as tmp:
            job = Path(tmp)
            rows = [dict(recordId=str(i), plainText='水泵', protectedTokens=[],
                         properties={'height': 2}) for i in range(2)]
            path = job / 'exchange/manifest.input.jsonl'
            cad_translate._atomic_write_jsonl(path, rows)
            cad_translate.prepare_translation_worklist(job)
            work = job / 'exchange/translation-worklist/part-0001.jsonl'
            request = cad_translate._read_jsonl(work)[0]
            self.assertEqual(2, request['occurrences'])
            self.assertIn('context', request)
            self.assertNotIn('contextVariants', request)
            rows[1]['properties']['height'] = 4
            cad_translate._atomic_write_jsonl(path, rows)
            cad_translate.prepare_translation_worklist(job)
            self.assertEqual(2, len(cad_translate._read_jsonl(work)[0]['contextVariants']))

    def test_retry_clock_includes_failed_attempt_without_changing_its_record(self):
        with tempfile.TemporaryDirectory() as tmp:
            parent, child = Path(tmp) / 'first', Path(tmp) / 'retry'
            config = dict(sourceSha256='abc', outputMode='bilingual', sourceLanguage='zh-CN', targetLanguage='fr')
            for job in (parent, child):
                (job / 'config').mkdir(parents=True)
                (job / 'config/export-job.json').write_text(json.dumps(config))
            original = record(parent, 'export-start', started=100, now=100)
            report = record(child, 'export-start', started=400, now=400, retry_from=parent)
            self.assertEqual(300, report['elapsedSeconds'])
            self.assertEqual(450, record(child, 'delivery-ready', now=550)['elapsedSeconds'])
            self.assertEqual(original, json.loads((parent / 'artifacts/workflow-timing.json').read_text()))
            config['targetLanguage'] = 'en'
            (child / 'config/export-job.json').write_text(json.dumps(config))
            with self.assertRaisesRegex(ValueError, 'same source.*mode.*language'):
                record(child, 'export-start', now=600, retry_from=parent)

    def test_format_variants_share_full_translation_but_context_does_not(self):
        rows = [{"recordId": str(i), "plainText": "半成品仓", "protectedTokens": [],
                 "objectType": "AcDbText", "textRole": "text",
                 "properties": {"height": i + 1, "layer": "equipment" if i < 3 else "legend"}}
                for i in range(4)]
        self.assertEqual(4, len(groups(rows, "zh")))
        self.assertEqual([3, 1], [len(g) for g in groups(rows, "zh", semantic=True)])
        rows[1]["protectedTokens"] = [{"marker": "⟦P0001⟧", "value": "25t"}]
        self.assertEqual(3, len(groups(rows, "zh", semantic=True)))

    def test_worklist_exposes_protected_values_and_assembly_preserves_entity_hashes(self):
        with tempfile.TemporaryDirectory() as tmp:
            job = Path(tmp)
            (job / "exchange").mkdir()
            rows = [{"recordId": str(i), "inputHash": "hash" + str(i),
                     "plainText": "产量⟦P0001⟧", "protectedTokens": [{"marker": "⟦P0001⟧", "value": "25t"}],
                     "properties": {"height": i + 1}} for i in range(2)]
            manifest = job / "exchange/manifest.input.jsonl"
            cad_translate._atomic_write_jsonl(manifest, rows)
            report = cad_translate.prepare_translation_worklist(job)
            self.assertEqual(1, report["translationRecordCount"])
            request = cad_translate._read_jsonl(job / "exchange/translation-worklist/part-0001.jsonl")[0]
            self.assertEqual("25t", request["protectedTokens"][0]["value"])
            batch = job / "compact.jsonl"
            cad_translate._atomic_write_jsonl(batch, [{"recordId": "0", "translatedText": "Output ⟦P0001⟧"}])
            cad_translate.assemble_translations(job, batch)
            output = cad_translate._read_jsonl(job / "exchange/translations.output.jsonl")
            self.assertEqual(["hash0", "hash1"], [r["inputHash"] for r in output])

    def test_all_bad_markers_are_reported_in_one_pass(self):
        with tempfile.TemporaryDirectory() as tmp:
            job = Path(tmp)
            (job / "exchange").mkdir()
            manifest = job / "exchange/manifest.input.jsonl"
            translations = job / "exchange/translations.output.jsonl"
            cad_translate._atomic_write_jsonl(manifest, [
                {"recordId": str(i), "inputHash": "h", "plainText": "泵⟦P0001⟧",
                 "protectedTokens": [{"marker": "⟦P0001⟧", "value": "123"}]} for i in range(3)])
            cad_translate._atomic_write_jsonl(translations, [
                {"recordId": str(i), "inputHash": "h", "translatedText": "Pump", "schemaVersion": "1.0",
                 "reviewStatus": "approved", "reason": "test"} for i in range(3)])
            with self.assertRaisesRegex(ValueError, "count=3"):
                cad_translate.validate_complete_translations(manifest, translations)
            self.assertEqual(3, len(cad_translate._read_jsonl(job / "artifacts/translation-marker-repairs.jsonl")))

    def test_quality_flags_information_loss_without_rejecting_full_terms(self):
        for source, bad, good in [
            ("半成品仓", "Buffer", "Intermediate Product Silo"),
            ("生石灰筛分冷却机组", "Screen/Cooler", "Quicklime Screening and Cooling Unit"),
            ("河南中材水泥装备有限公司", "Henan Sinoma", "Henan Sinoma Cement Equipment Co., Ltd."),
            ("专业负责人", "PIC", "Discipline Lead")]:
            self.assertTrue(review({"plainText": source}, bad))
            self.assertFalse(review({"plainText": source}, good))

    def test_wall_clock_includes_translation_wait_and_does_not_restart(self):
        with tempfile.TemporaryDirectory() as tmp:
            record(tmp, "export-start", started=100, now=100)
            record(tmp, "worklist-ready", now=110)
            report = record(tmp, "translations-ready", started=800, now=900)
            self.assertEqual(800, report["elapsedSeconds"])
            self.assertEqual(400, report["remainingSeconds"])
            self.assertTrue(record(tmp, "delivery-ready", now=1401)["budgetExceeded"])

    def test_readability_review_reports_shrink_not_wrapping_at_original_size(self):
        report = layout_review({"adjustments": [
            {"recordId": "wrapped", "originalHeight": 5, "newHeight": 5},
            {"recordId": "tiny", "originalHeight": 5, "newHeight": 1},
            {"recordId": "unknown", "originalHeight": None, "newHeight": 1}]})
        self.assertEqual(1, report["count"])
        self.assertEqual("tiny", report["records"][0]["recordId"])


if __name__ == "__main__":
    unittest.main()
