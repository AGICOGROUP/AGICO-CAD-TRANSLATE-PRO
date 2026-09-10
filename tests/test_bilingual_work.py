import json
import tempfile
import unittest
from pathlib import Path

from test_cad_translate import cad_translate
from bilingual_work import inline_candidates
from render_review import review_regions


class BilingualWorkTests(unittest.TestCase):
    def test_french_bilingual_uses_complete_native_term_and_rejects_chinese_target(self):
        with tempfile.TemporaryDirectory() as tmp:
            job = Path(tmp)
            for folder in ('exchange', 'config', 'artifacts'):
                (job / folder).mkdir()
            config = dict(outputMode='bilingual', sourceLanguage='zh-CN', targetLanguage='fr', sourceSha256='drawing')
            (job/'config/export-job.json').write_text(json.dumps(config), encoding='utf-8')
            rows = [dict(recordId=str(i), handle=str(i+10), inputHash='h'+str(i), rawText=s,
                         plainText=s, protectedTokens=[]) for i,s in enumerate('硬脂酸')]
            cad_translate._atomic_write_jsonl(job/'exchange/manifest.input.jsonl', rows)
            (job/'artifacts/bilingual-term-groups.json').write_text(json.dumps(dict(sourceSha256='drawing',
                groups=[dict(sourceText='硬脂酸', recordIds=['0','1','2'])])), encoding='utf-8')
            self.assertEqual(1, cad_translate.prepare_translation_worklist(job)['translationRecordCount'])
            cad_translate._atomic_write_jsonl(job/'batch.jsonl',[dict(recordId='0',translatedText='Acide stéarique')])
            cad_translate.assemble_translations(job,job/'batch.jsonl')
            output=job/'exchange/translations.output.jsonl'
            self.assertEqual('passed',cad_translate.check_translations(job/'exchange/manifest.input.jsonl',output,job/'ok.json','bilingual')['status'])
            bad=cad_translate._read_jsonl(output)
            for row in bad: row['translatedText']='硬脂酸'
            cad_translate._atomic_write_jsonl(job/'bad.jsonl',bad)
            self.assertEqual('failed',cad_translate.check_translations(job/'exchange/manifest.input.jsonl',job/'bad.jsonl',job/'bad-report.json','bilingual')['status'])

    def test_native_term_group_is_one_request_and_keeps_all_source_records(self):
        with tempfile.TemporaryDirectory() as tmp:
            job = Path(tmp)
            for folder in ('exchange', 'config', 'artifacts'):
                (job / folder).mkdir()
            config = dict(outputMode='bilingual', sourceLanguage='zh-CN', targetLanguage='en', sourceSha256='drawing')
            (job/'config/export-job.json').write_text(json.dumps(config), encoding='utf-8')
            rows = [dict(recordId=str(i), handle=str(i+10), inputHash='h'+str(i), rawText=s,
                         plainText=s, protectedTokens=[]) for i,s in enumerate('硬脂酸')]
            cad_translate._atomic_write_jsonl(job/'exchange/manifest.input.jsonl',rows)
            (job/'artifacts/bilingual-term-groups.json').write_text(json.dumps(dict(sourceSha256='drawing',
                groups=[dict(sourceText='硬脂酸', recordIds=['0','1','2'])])),encoding='utf-8')
            result=cad_translate.prepare_translation_worklist(job)
            self.assertEqual(1,result['translationRecordCount'])
            work=cad_translate._read_jsonl(job/'exchange/translation-worklist/part-0001.jsonl')
            self.assertEqual('硬脂酸',work[0]['sourceText'])
            cad_translate._atomic_write_jsonl(job/'batch.jsonl',[dict(recordId='0',translatedText='Stearic Acid')])
            cad_translate.assemble_translations(job,job/'batch.jsonl')
            out=cad_translate._read_jsonl(job/'exchange/translations.output.jsonl')
            self.assertEqual(['Stearic Acid']*3,[r['translatedText'] for r in out])
            self.assertEqual(['h0','h1','h2'],[r['inputHash'] for r in out])
            for row,value in zip(out,('Ste','aric','Acid')):
                row['translatedText']=value
            cad_translate._atomic_write_jsonl(job/'split.jsonl',out)
            checked=cad_translate.check_translations(job/'exchange/manifest.input.jsonl',job/'split.jsonl',job/'split-check.json','bilingual')
            self.assertEqual('failed',checked['status'])
            config['outputMode']='replace'
            (job/'config/export-job.json').write_text(json.dumps(config),encoding='utf-8')
            self.assertEqual(3,cad_translate.prepare_translation_worklist(job)['translationRecordCount'])

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
