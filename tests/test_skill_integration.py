import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import test_cad_translate
from test_cad_translate import cad_translate as cad
from bilingual_work import inline_candidates

class IntegrationTests(unittest.TestCase):
    def test_formatted_dwg_number_is_a_review_candidate_but_codes_are_not(self):
        texts = [r"{\fSimSun;图}{\fArial;号}\P{\fArial;DWG.}\PNo.",
                 "图号 DWG", "设备 ABCD-123", "水泵 20mm"]
        rows = [dict(recordId=str(i), handle=str(i), inputHash='h', rawText=s,
                     plainText=s) for i,s in enumerate(texts)]
        self.assertEqual(['0'], [r['recordId'] for r in inline_candidates(rows)])

    def test_current_bundle_is_deployed_once_without_overwriting_default(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); bundle=root/'bundle'; installed=root/'trusted'; bundle.mkdir(); installed.mkdir()
            for name in cad.PLUGIN_FILES:
                (bundle/name).write_bytes(('current-'+name).encode())
                (installed/name).write_bytes(b'old')
            with patch.object(cad,'PLUGIN_DIR',bundle), patch.object(cad,'AUTOCAD_2025_PLUGIN_DIR',installed), patch.dict('os.environ',{},clear=True):
                selected=cad.runtime_plugin_dir(Path('AutoCAD 2025'))
                self.assertNotEqual(installed,selected)
                self.assertFalse(selected.exists(), 'selection must be read-only')
                cad.ensure_runtime_plugin(Path('AutoCAD 2025'))
                self.assertEqual((bundle/cad.PLUGIN_FILES[0]).read_bytes(),(selected/cad.PLUGIN_FILES[0]).read_bytes())
                self.assertEqual(b'old',(installed/cad.PLUGIN_FILES[0]).read_bytes())
                with patch('shutil.copy2',side_effect=AssertionError('unchanged runtime recopied')):
                    cad.ensure_runtime_plugin(Path('AutoCAD 2025'))
                (bundle/cad.PLUGIN_FILES[1]).write_bytes(b'new core')
                self.assertNotEqual(selected,cad.runtime_plugin_dir(Path('AutoCAD 2025')))

    def test_explicit_runtime_override_is_preserved(self):
        with tempfile.TemporaryDirectory() as tmp, patch.dict('os.environ',{'CAD_TRANSLATE_PLUGIN_DIR':tmp}):
            for name in cad.PLUGIN_FILES: (Path(tmp)/name).write_bytes(b'override')
            self.assertEqual(Path(tmp).resolve(),cad.ensure_runtime_plugin(Path('AutoCAD 2025')))
