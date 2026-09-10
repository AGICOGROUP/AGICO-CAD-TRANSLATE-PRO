import json
import tempfile
import unittest
from pathlib import Path

from test_cad_translate import cad_translate
from bilingual_work import inline_candidates
from render_review import review_regions


class BilingualWorkTests(unittest.TestCase):
    def test_review_reduces_work_without_losing_exchange_rows_or_markers(self):
        with tempfile.TemporaryDirectory() as tmp:
            job = Path(tmp)
            (job / 'exchange').mkdir()
            (job / 'config').mkdir()
            config = {'outputMode': 'bilingual', 'sourceLanguage': 'zh-CN', 'targetLanguage': 'en', 'sourceSha256': 'drawing'}
            path = job / 'config/export-job.json'
            path.write_text(json.dumps(config), encoding='utf-8')
            rows = [dict(recordId='pair', handle='AB', inputHash='h1', rawText='1 风机\\PFan',
                         plainText='⟦P0001⟧风机 Fan', protectedTokens=[{'marker': '⟦P0001⟧', 'value': '1'}]),
                    dict(recordId='new', handle='CD', inputHash='h2', rawText='水泵', plainText='水泵', protectedTokens=[])]
            # Use a long English word so a short code cannot qualify as a candidate.
            rows[0]['rawText'] = '1 水泵\\PWater Pump'
            rows[0]['plainText'] = '⟦P0001⟧水泵 Water Pump'
            cad_translate._atomic_write_jsonl(job / 'exchange/manifest.input.jsonl', rows)
            self.assertEqual(2, cad_translate.prepare_translation_worklist(job)['translationRecordCount'])
            report = cad_translate.prepare_translation_worklist(job, existing_inline_handles='ab')
            self.assertEqual(1, report['translationRecordCount'])
            self.assertEqual(1, report['reviewedExistingInlineCount'])
            batch = job / 'batch.jsonl'
            cad_translate._atomic_write_jsonl(batch, [{'recordId': 'new', 'translatedText': 'Water Pump'}])
            cad_translate.assemble_translations(job, batch)
            output = cad_translate._read_jsonl(job / 'exchange/translations.output.jsonl')
            self.assertEqual(['h1', 'h2'], [r['inputHash'] for r in output])
            self.assertEqual('⟦P0001⟧ Water Pump', output[0]['translatedText'])
            config['sourceSha256'] = 'changed'
            path.write_text(json.dumps(config), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'different source'):
                cad_translate.prepare_translation_worklist(job)
            config['outputMode'] = 'replace'
            path.write_text(json.dumps(config), encoding='utf-8')
            self.assertEqual(2, cad_translate.prepare_translation_worklist(job)['translationRecordCount'])

    def test_font_names_and_model_codes_do_not_qualify(self):
        rows = [dict(recordId=str(i), handle=str(i), inputHash='h', rawText=s, plainText=s)
                for i, s in enumerate(['{\\fArial|b0|i0;水泵 HCQ2000}', '水泵 20mm', '水泵 Water Pump'])]
        self.assertEqual(['2'], [r['recordId'] for r in inline_candidates(rows)])
        rows[2]['plainText'] = '水泵⟦P0001⟧'
        rows[2]['protectedTokens'] = [{'marker': '⟦P0001⟧', 'raw': '1 Water Pump'}]
        self.assertEqual([], inline_candidates(rows))

    def test_review_keeps_distinct_block_instances_and_rejects_missing_handles(self):
        windows = [dict(sourceHandle='AB', instancePath=f'*Model_Space/Block[{i}]',
                        bounds=dict(left=i * 100, bottom=0, right=i * 100 + 10, top=10)) for i in range(2)]
        regions = review_regions(windows, 'ab')
        self.assertEqual(2, len(regions))
        self.assertEqual([100, 0, 110, 10], regions[1]['window'])
        with self.assertRaisesRegex(ValueError, 'No native review window'):
            review_regions(windows, 'CD')


if __name__ == '__main__':
    unittest.main()
