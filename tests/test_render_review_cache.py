import json
import re
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import test_cad_translate  # Establish the repository scripts import path.
import render_review as renderer


class RenderCacheTests(unittest.TestCase):
    def fixture(self, directory):
        root = Path(directory)
        drawing = root / 'source.dwg'
        drawing.write_bytes(b'original drawing')
        host = root / 'host'
        host.mkdir()
        (host / 'accoreconsole.exe').write_bytes(b'host v1')
        starts = []

        class RenderProcess:
            def __init__(self, command, **kwargs):
                starts.append(command)
                script = Path(command[command.index('/s') + 1]).read_text(encoding='utf-8')
                for name in re.findall(r'_.PNGOUT\n"([^"]+)"', script):
                    Path(name).write_bytes(b'complete mocked PNG' * 100)

            def poll(self): return 0

        return root, drawing, host, starts, RenderProcess

    def test_identical_render_reuses_verified_png_without_cad(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, drawing, host, starts, process = self.fixture(tmp)
            with patch.object(renderer, 'discover_autocad', return_value=host), patch.object(renderer.subprocess, 'Popen', process):
                first = renderer.render(drawing, root / 'review.png', [0, 0, 10, 10])
                second = renderer.render(drawing, root / 'review.png', [0, 0, 10, 10])
            self.assertEqual(first, second)
            self.assertEqual(1, len(starts))

    def test_changed_inputs_version_output_and_repeated_request_finds_versioned_cache(self):
        for changed in ('drawing', 'window', 'paper', 'host'):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as tmp:
                root, drawing, host, starts, process = self.fixture(tmp)
                with patch.object(renderer, 'discover_autocad', return_value=host), patch.object(renderer.subprocess, 'Popen', process):
                    first = renderer.render(drawing, root / 'review.png', [0, 0, 10, 10])
                    original = first.read_bytes()
                    if changed == 'drawing': drawing.write_bytes(b'updated drawing')
                    if changed == 'host': (host / 'accoreconsole.exe').write_bytes(b'host v2')
                    window = [0, 0, 20, 20] if changed == 'window' else [0, 0, 10, 10]
                    second = renderer.render(drawing, root / 'review.png', window, paper=changed == 'paper')
                    third = renderer.render(drawing, root / 'review.png', window, paper=changed == 'paper')
                self.assertNotEqual(first, second)
                self.assertEqual(second, third)
                self.assertEqual(original, first.read_bytes())
                self.assertEqual(2, len(starts))
                self.assertNotEqual(starts[0][starts[0].index('/i')+1], starts[1][starts[1].index('/i')+1])

    def test_partial_or_altered_image_is_never_reused_or_overwritten(self):
        for invalid in ('unreceipted', 'altered', 'truncated'):
            with self.subTest(invalid=invalid), tempfile.TemporaryDirectory() as tmp:
                root, drawing, host, starts, process = self.fixture(tmp)
                output = root / 'review.png'
                with patch.object(renderer, 'discover_autocad', return_value=host), patch.object(renderer.subprocess, 'Popen', process):
                    if invalid == 'unreceipted': output.write_bytes(b'unreceipted PNG' * 100)
                    else:
                        renderer.render(drawing, output)
                        output.write_bytes(b'altered data' * 100 if invalid == 'altered' else b'partial')
                    before = output.read_bytes()
                    result = renderer.render(drawing, output)
                self.assertNotEqual(result, output)
                self.assertEqual(before, output.read_bytes())

    def test_mixed_cache_renders_only_missing_windows_in_one_session(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, drawing, host, starts, process = self.fixture(tmp)
            overview, detail = root / 'overview.png', root / 'detail.png'
            with patch.object(renderer, 'discover_autocad', return_value=host), patch.object(renderer.subprocess, 'Popen', process):
                renderer.render(drawing, overview)
                results = renderer.render_many(drawing, [(overview, None), (detail, [0, 0, 10, 10])])
            self.assertEqual([overview, detail], results)
            script = Path(starts[-1][starts[-1].index('/s')+1]).read_text(encoding='utf-8')
            self.assertNotIn(overview.as_posix(), script)
            self.assertEqual(1, script.count('_.PNGOUT'))
            self.assertEqual(2, len(starts))

    def test_incomplete_native_output_gets_no_receipt_and_retry_uses_fresh_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, drawing, host, starts, process = self.fixture(tmp)
            output = root / 'review.png'

            class IncompleteProcess(process):
                def __init__(self, command, **kwargs):
                    super().__init__(command, **kwargs)
                    output.write_bytes(b'incomplete PNG')

            with patch.object(renderer, 'discover_autocad', return_value=host):
                with patch.object(renderer.subprocess, 'Popen', IncompleteProcess):
                    with self.assertRaisesRegex(RuntimeError, 'complete review images'):
                        renderer.render(drawing, output)
                self.assertFalse(renderer.receipt_path(output).exists())
                with patch.object(renderer.subprocess, 'Popen', process):
                    result = renderer.render(drawing, output)
            self.assertNotEqual(output, result)
            self.assertEqual(b'incomplete PNG', output.read_bytes())
            self.assertTrue(renderer.receipt_path(result).is_file())

    def test_changed_renderer_binding_invalidates_receipt(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, drawing, host, starts, process = self.fixture(tmp)
            output = root / 'review.png'
            with patch.object(renderer, 'discover_autocad', return_value=host), patch.object(renderer.subprocess, 'Popen', process):
                renderer.render(drawing, output)
                path = renderer.receipt_path(output)
                receipt = json.loads(path.read_text(encoding='utf-8'))
                self.assertEqual(output.stat().st_size, receipt['pngSize'])
                self.assertEqual(renderer.digest(output), receipt['pngSha256'])
                receipt['binding']['rendererSha256'] = 'older-renderer'
                path.write_text(json.dumps(receipt), encoding='utf-8')
                result = renderer.render(drawing, output)
            self.assertNotEqual(output, result)
            self.assertEqual(2, len(starts))

    def test_render_job_uses_corrected_final_and_records_actual_paired_names(self):
        with tempfile.TemporaryDirectory() as tmp:
            root, drawing, host, starts, process = self.fixture(tmp)
            (root / 'config').mkdir()
            artifacts = root / 'artifacts'
            artifacts.mkdir()
            old, corrected = root / 'old.dwg', root / 'corrected.dwg'
            old.write_bytes(b'old candidate')
            corrected.write_bytes(b'corrected candidate')
            (root / 'config/export-job.json').write_text(json.dumps({
                'sourcePath':str(drawing), 'outputPath':str(old), 'outputMode':'bilingual'}))
            (artifacts / 'bilingual-final.json').write_text(json.dumps({'candidatePath':str(corrected)}))
            (artifacts / 'bilingual-review-windows.json').write_text(json.dumps({'windows':[]}))
            with patch.object(renderer, 'discover_autocad', return_value=host), patch.object(renderer.subprocess, 'Popen', process):
                original = json.loads(renderer.render_job(root, '').read_text(encoding='utf-8'))
                corrected.write_bytes(b'candidate changed again')
                plan = json.loads(renderer.render_job(root, '').read_text(encoding='utf-8'))
            self.assertEqual(original[0]['images'][0], plan[0]['images'][0])
            self.assertNotEqual(original[0]['images'][1], plan[0]['images'][1])
            self.assertEqual(3, len(starts))
            native_inputs = [Path(command[command.index('/i')+1]).read_bytes() for command in starts]
            self.assertEqual([b'original drawing', b'corrected candidate', b'candidate changed again'], native_inputs)
            self.assertTrue(all((artifacts / name).is_file() for name in plan[0]['images']))


if __name__ == '__main__':
    unittest.main()
