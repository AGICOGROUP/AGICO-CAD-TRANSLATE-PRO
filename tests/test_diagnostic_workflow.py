import json
import io
from contextlib import redirect_stdout
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import test_cad_translate  # Establish scripts import path.
import job_timing
import job_diagnostics
import cad_translate
from pipeline_io import digest


class DiagnosticWorkflowTests(unittest.TestCase):
    def fixture(self, root):
        (root / 'config').mkdir()
        (root / 'artifacts').mkdir()
        source, candidate, before = [root / name for name in ('source.dwg', 'candidate.dwg', 'artifacts/replace-before-compose.dwg')]
        for path in (source, candidate, before):
            path.write_bytes(path.name.encode())
        def report(name, value):
            (root / name).write_text(json.dumps(value), encoding='utf-8')
        report('config/export-job.json', {'sourcePath': str(source), 'sourceSha256': digest(source),
            'outputPath': str(root / 'obsolete.dwg'), 'outputMode': 'replace'})
        report('artifacts/replace-final.json', {'candidatePath': str(candidate), 'candidateSha256': digest(candidate)})
        report('artifacts/verification.json', {'candidateSha256': digest(before), 'status': 'passed'})
        report('artifacts/replace-layout-audit.json', {'manualReview': [
            {'code': 'text-overlap', 'recordId': str(i), 'definitionName': '*MODEL_SPACE', 'regionId': 'note-1'}
            for i in range(500)]})
        return source, candidate, before

    def test_summary_is_bounded_and_does_not_launch_cad_or_approve(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.fixture(root)
            with patch('render_review.render_many') as render:
                result = job_diagnostics.diagnose(root)
            render.assert_not_called()
            self.assertEqual(500, result['layout']['riskCount'])
            self.assertLessEqual(len(result['layout']['groups'][0]['examples']), 3)
            self.assertLess(len(json.dumps(result)), 10000)
            self.assertNotIn('deliveryReady', result)
            self.assertFalse((root / 'artifacts/replace-visual-review.json').exists())

    def test_one_batch_per_drawing_compares_intermediate_at_same_windows(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source, candidate, before = self.fixture(root)
            windows = [[0, 0, 10, 10], [20, 20, 30, 30]]
            calls = []
            def render(drawing, requests, **kwargs):
                calls.append((drawing, requests))
                return [p.with_stem(p.stem + '-0002') for p, _ in requests]
            with patch('render_review.render_many', side_effect=render):
                result = job_diagnostics.diagnose(root, windows, render=True)
            self.assertEqual([source, before, candidate], [p for p, _ in calls])
            for _, requests in calls:
                self.assertEqual(windows, [w for _, w in requests if w is not None])
            self.assertTrue(all(name.endswith('-0002.png') for names in result['images'].values() for name in names))
            self.assertEqual(3, len(calls))

    def test_audit_cli_bounds_ids_without_changing_full_report(self):
        report = {'status': 'needs_review', 'deliveryReady': False,
                  'layoutReviewRecordIds': [str(i) for i in range(500)]}
        with patch('cad_translate.summarize_audit', return_value=report), redirect_stdout(io.StringIO()) as stdout:
            cad_translate.main(['audit-summary', '--job', 'unused'])
        output = json.loads(stdout.getvalue())
        self.assertNotIn('layoutReviewRecordIds', output)
        self.assertEqual(500, len(report['layoutReviewRecordIds']))
        self.assertLess(len(stdout.getvalue()), 1500)

    def test_changed_candidate_is_reported_and_never_rendered_as_current(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _, candidate, _ = self.fixture(root)
            candidate.write_bytes(b'changed since receipt')
            with patch('render_review.render_many') as render:
                result = job_diagnostics.diagnose(root, [[0, 0, 10, 10]], render=True)
            render.assert_not_called()
            self.assertEqual('stale', result['drawings']['candidate']['status'])
            self.assertTrue(result['issues'])

    def test_stale_intermediate_is_excluded_but_current_pair_is_rendered(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source, candidate, before = self.fixture(root)
            before.write_bytes(b'different intermediate')
            with patch('render_review.render_many', side_effect=lambda p, req, **kw: [x for x, _ in req]) as render:
                result = job_diagnostics.diagnose(root, [[0, 0, 10, 10]], render=True)
            self.assertEqual([source, candidate], [call.args[0] for call in render.call_args_list])
            self.assertNotIn('beforeCompose', result['images'])

    def test_render_requires_a_valid_detail_window(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.fixture(root)
            for windows in ([], [[0, 0, 0, 10]], [[0, 0, float('nan'), 10]]):
                with self.subTest(windows=windows), patch('render_review.render_many') as render:
                    with self.assertRaises(ValueError):
                        job_diagnostics.diagnose(root, windows, render=True)
                    render.assert_not_called()

    def test_retry_summary_keeps_task_wait_separate_from_attempt_and_cad(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'artifacts').mkdir()
            data = {'startedAtEpoch': 100, 'attemptStartedAtEpoch': 500, 'events': [
                {'event': 'export-start', 'atEpoch': 100}, {'event': 'export-finished', 'atEpoch': 102},
                {'event': 'export-start', 'atEpoch': 500}, {'event': 'export-finished', 'atEpoch': 502},
                {'event': 'import-start', 'atEpoch': 510}, {'event': 'import-finished', 'atEpoch': 570},
                {'event': 'delivery-ready', 'atEpoch': 600}]}
            path = root / 'artifacts/workflow-timing.json'
            path.write_text(json.dumps(data))
            before = path.read_bytes()
            result = job_timing.summarize(root)
            self.assertEqual(500, result['recordedTaskSeconds'])
            self.assertEqual(100, result['attemptSeconds'])
            self.assertEqual(64, result['commandSpanSeconds'])
            self.assertEqual(436, result['betweenCommandSeconds'])
            self.assertEqual(before, path.read_bytes())

    def test_repeated_delivery_summary_does_not_extend_completed_timing(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            job_timing.record(root, 'export-start', now=100)
            first = job_timing.record(root, 'delivery-ready', now=200)
            again = job_timing.record(root, 'delivery-ready', now=999)
            self.assertEqual(first, again)


if __name__ == '__main__':
    unittest.main()
