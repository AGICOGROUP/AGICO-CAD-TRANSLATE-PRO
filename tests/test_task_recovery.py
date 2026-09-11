import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from test_cad_translate import cad_translate as cad
import test_runtime_v2
from pipeline_io import read_report, write_report, digest


class RecoveryTests(unittest.TestCase):
    fixture = test_runtime_v2.RuntimeV2Tests.fixture
    native_result = test_runtime_v2.RuntimeV2Tests.native_result

    def test_complete_invalid_batch_repairs_only_bad_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            manifest = Path(config['manifestPath'])
            rows = cad._read_jsonl(manifest) + [dict(recordId='b', inputHash='hb', plainText='阀门20', rawText='阀门20', protectedTokens=[])]
            cad._atomic_write_jsonl(manifest, rows)
            cad.write_export_seal(job, config)
            cad._atomic_write_jsonl(translations, cad._read_jsonl(translations) + [dict(schemaVersion='1.0', recordId='b',
                inputHash='hb', translatedText='Valve 21', reviewStatus='approved', reason='test')])
            with mock.patch.object(cad, 'run_once', side_effect=AssertionError('invalid batch must not launch CAD')):
                report = cad.run_resume(job, root)
            self.assertEqual(['b'], report['invalidRecordIds'])
            self.assertEqual(['b'], [r['recordId'] for r in cad._read_jsonl(Path(report['repairPath']))])
            self.assertEqual(['a'], [r['recordId'] for r in cad._read_jsonl(Path(report['preservedTranslations']))])
            repair = root/'repair.jsonl'
            cad._atomic_write_jsonl(repair, [dict(recordId='b', translatedText='Valve 20')])
            def run(op, path, working, host):
                self.native_result(job, read_report(path))
                return 0
            with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                self.assertEqual('imported', cad.run_resume(job, root, translated=repair)['action'])
            self.assertEqual({'a':'Pump', 'b':'Valve 20'}, {r['recordId']:r['translatedText'] for r in cad._read_jsonl(translations)})

    def test_resume_reuses_final_without_cad_and_rejects_changed_source(self):
        self.assertTrue(hasattr(cad, 'run_resume'), 'resume entry point is required')
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            def run(op, path, working, host):
                self.native_result(job, read_report(path))
                return 0
            with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                cad.run_import(job, translations, root)
            with mock.patch.object(cad, 'run_once', side_effect=AssertionError('must reuse')):
                report = cad.run_resume(job, root)
            self.assertEqual('review', report['nextAction'])
            self.assertFalse(report['deliveryReady'])
            Path(config['sourcePath']).write_bytes(b'changed')
            with self.assertRaisesRegex(RuntimeError, 'Source drawing changed'):
                cad.run_resume(job, root)

    def test_failed_import_retains_candidate_and_resume_inspects_only(self):
        self.assertTrue(hasattr(cad, 'run_resume'), 'resume entry point is required')
        for mode in ('replace', 'bilingual'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                job, config, translations = self.fixture(root, mode)
                attempts = []
                def run(op, path, working, host):
                    stage = read_report(path)
                    attempts.append((op, path, stage['outputPath']))
                    self.native_result(job, stage)
                    if op == 'import':
                        write_report(Path(stage['resultPath']), {'status': 'failed', 'errors': [{'code': 'inspection-failed', 'message': 'Saved candidate available'}]})
                        return 1
                    return 0
                with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                    with self.assertRaisesRegex(RuntimeError, 'inspection-failed'):
                        cad.run_import(job, translations, root)
                    report = cad.run_resume(job, root)
                self.assertEqual(['import', 'inspect'], [a[0] for a in attempts])
                self.assertNotEqual(attempts[0][1], attempts[1][1])
                self.assertEqual(attempts[0][2], attempts[1][2])
                self.assertFalse(report['deliveryReady'])

    def test_partial_batch_keeps_good_row_and_requests_missing_only(self):
        self.assertTrue(hasattr(cad, 'run_resume'), 'resume entry point is required')
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            manifest = Path(config['manifestPath'])
            with manifest.open('a', encoding='utf-8') as stream:
                stream.write(json.dumps({'recordId':'b', 'inputHash':'hb', 'plainText':'阀门', 'rawText':'阀门', 'protectedTokens':[]})+'\n')
            cad.write_export_seal(job, config)
            original = translations.read_bytes()
            with mock.patch.object(cad, 'run_once', side_effect=AssertionError('no CAD for incomplete batch')):
                report = cad.run_resume(job, root)
            self.assertEqual(['b'], report['missingRecordIds'])
            self.assertEqual(original, translations.read_bytes())
            repairs = [json.loads(line) for line in Path(report['repairPath']).read_text(encoding='utf-8').splitlines()]
            self.assertEqual(['b'], [r['recordId'] for r in repairs])

    def test_error_envelope_reports_actual_code_and_message(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = Path(tmp)/'result.json'
            write_report(result, {'status':'failed','errors':[{'code':'stale-hash','message':'Candidate changed','recordId':'a'}]})
            with self.assertRaisesRegex(RuntimeError, 'stale-hash.*Candidate changed'):
                cad._require_succeeded_result(result, 'inspect')

    def test_changed_candidate_or_translations_cannot_reuse_final(self):
        for changed in ('candidate', 'translations'):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                job, config, translations = self.fixture(root)
                def run(op, path, working, host):
                    self.native_result(job, read_report(path))
                    return 0
                with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                    cad.run_import(job, translations, root)
                target = Path(config['outputPath']) if changed == 'candidate' else translations
                target.write_bytes(target.read_bytes() + b' ')
                with mock.patch.object(cad, 'run_once', side_effect=AssertionError('stale input must not run')):
                    with self.assertRaisesRegex(ValueError, 'changed'):
                        cad.run_resume(job, root)

    def test_correct_uses_new_output_preserves_previous_and_requires_new_review(self):
        for mode in ('replace', 'bilingual'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                job, config, translations = self.fixture(root, mode)
                def run(op, path, working, host):
                    stage = read_report(path)
                    self.native_result(job, stage)
                    if op == 'correct':
                        self.assertEqual(config['outputPath'], stage['candidatePath'])
                        Path(stage['outputPath']).write_bytes(b'corrected candidate')
                        native = job / 'artifacts' / f'{mode}-native-check.json'
                        report = read_report(native)
                        report['candidateSha256'] = digest(stage['outputPath'])
                        write_report(native, report)
                    return 0
                with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                    cad.run_import(job, translations, root)
                    test_runtime_v2.RuntimeV2Tests.visual_review(self, job, config, 'passed')
                    source_hash = digest(config['sourcePath'])
                    previous_hash = digest(config['outputPath'])
                    edits = root / 'edits.json'
                    write_report(edits, {'candidateSha256':previous_hash, 'edits':[{'handle':'2', 'expectedText':'Pump', 'height':3}]})
                    result = cad.run_correct(job, edits, root)
                self.assertFalse(result['deliveryReady'])
                self.assertNotEqual(config['outputPath'], result['candidate'])
                self.assertEqual(previous_hash, digest(config['outputPath']))
                self.assertEqual(source_hash, digest(config['sourcePath']))
                self.assertEqual('reused', cad.run_resume(job, root)['action'])

    def test_partial_repair_rejects_changed_number_but_retains_valid_rows(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            manifest = Path(config['manifestPath'])
            rows = [json.loads(manifest.read_text(encoding='utf-8')),
                    {'recordId':'b','inputHash':'hb','plainText':'阀门20','rawText':'阀门20','protectedTokens':[]},
                    {'recordId':'c','inputHash':'hc','plainText':'电机','rawText':'电机','protectedTokens':[]}]
            cad._atomic_write_jsonl(manifest, rows)
            cad.write_export_seal(job, config)
            records = [json.loads(translations.read_text(encoding='utf-8')),
                       {'schemaVersion':'1.0','recordId':'b','inputHash':'hb','translatedText':'Valve 21','reviewStatus':'approved','reason':'test'}]
            cad._atomic_write_jsonl(translations, records)
            report = cad.run_resume(job, root)
            self.assertEqual(['b'], report['invalidRecordIds'])
            preserved = [json.loads(line) for line in Path(report['preservedTranslations']).read_text(encoding='utf-8').splitlines()]
            self.assertEqual(['a'], [r['recordId'] for r in preserved])

    def test_core_errors_are_preserved_before_native_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            manifest = Path(config['manifestPath'])
            row = json.loads(manifest.read_text(encoding='utf-8'))
            row.update(plainText='泵20', rawText='泵20')
            cad._atomic_write_jsonl(manifest, [row])
            cad.write_export_seal(job, config)
            with mock.patch.object(cad, 'run_once', side_effect=AssertionError('Core must block CAD')):
                with self.assertRaisesRegex(ValueError, 'numeric_or_protected_token_mismatch'):
                    cad.run_import(job, translations, root)
            report = read_report(job / 'artifacts/text-validation.json')
            self.assertFalse(report['isValid'])
            self.assertEqual('a', report['errors'][0]['recordId'])

    def test_repair_submission_keeps_good_translation_and_completes_import(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            manifest = Path(config['manifestPath'])
            rows = [json.loads(manifest.read_text(encoding='utf-8')),
                    {'recordId':'b','inputHash':'hb','plainText':'阀门','rawText':'阀门','protectedTokens':[]}]
            cad._atomic_write_jsonl(manifest, rows)
            cad.write_export_seal(job, config)
            self.assertEqual('translation_required', cad.run_resume(job, root)['status'])
            repair = root / 'repair.jsonl'
            cad._atomic_write_jsonl(repair, [{'recordId':'b', 'translatedText':'Valve'}])
            def run(op, path, working, host):
                self.native_result(job, read_report(path))
                return 0
            with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                result = cad.run_resume(job, root, translated=repair)
            self.assertEqual('imported', result['action'])
            combined = {r['recordId']:r['translatedText'] for r in cad._read_jsonl(translations)}
            self.assertEqual({'a':'Pump','b':'Valve'}, combined)

    def test_missing_duplicate_entity_uses_existing_translation_without_retranslation(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            manifest = Path(config['manifestPath'])
            row = json.loads(manifest.read_text(encoding='utf-8'))
            cad._atomic_write_jsonl(manifest, [row, {**row, 'recordId':'b', 'inputHash':'hb'}])
            cad.write_export_seal(job, config)
            def run(op, path, working, host):
                self.native_result(job, read_report(path))
                return 0
            with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                result = cad.run_resume(job, root)
            self.assertEqual('imported', result.get('action'))
            self.assertEqual(['Pump','Pump'], [r['translatedText'] for r in cad._read_jsonl(translations)])

    def test_correction_failure_before_save_can_reuse_prior_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job, config, translations = self.fixture(root)
            def run(op, path, working, host):
                if op == 'correct': return 1
                self.native_result(job, read_report(path))
                return 0
            with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                cad.run_import(job, translations, root)
                edits = root / 'edits.json'
                write_report(edits, {'candidateSha256':digest(config['outputPath']), 'edits':[{'handle':'2','expectedText':'Pump','height':3}]})
                self.assertEqual('failed', cad.run_correct(job, edits, root)['status'])
            with mock.patch.object(cad, 'run_once', side_effect=AssertionError('prior candidate remains usable')):
                self.assertEqual('reused', cad.run_resume(job, root)['action'])

    def test_stale_failed_review_requires_new_review_after_correction(self):
        for mode in ('replace', 'bilingual'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                job, config, translations = self.fixture(root, mode)
                def run(op, path, working, host):
                    self.native_result(job, read_report(path))
                    return 0
                with mock.patch.object(cad, 'require_ready'), mock.patch.object(cad, 'run_once', side_effect=run):
                    cad.run_import(job, translations, root)
                write_report(job / 'artifacts' / f'{mode}-visual-review.json', {
                    'status':'failed', 'sourceSha256':config['sourceSha256'],
                    'candidateSha256':'obsolete', 'blockingIssues':['Old overlap'], 'images':[]})
                result = cad.summarize_audit(job)
                self.assertFalse(result['deliveryReady'])
                self.assertTrue(result['gate']['passed'])
                self.assertEqual([], result['blockingIssues'])

    def test_plugin_override_is_used(self):
        with mock.patch.dict('os.environ', {'CAD_TRANSLATE_PLUGIN_DIR': 'D:/branch/plugin'}):
            self.assertEqual(Path('D:/branch/plugin').resolve(), cad.runtime_plugin_dir(Path('AutoCAD 2025')))


if __name__ == '__main__':
    unittest.main()
