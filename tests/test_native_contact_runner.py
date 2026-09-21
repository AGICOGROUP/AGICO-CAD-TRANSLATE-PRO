import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    'native_contact_runner', Path(__file__).parent / 'cad/native-contact/run_near_label.py')
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class NativeContactRunnerTests(unittest.TestCase):
    def test_missing_review_job_fails_before_starting_autocad(self):
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ, {}, clear=True), \
                patch.object(runner.subprocess, 'Popen') as launch:
            plugin = Path(root) / 'NativeContactTests.dll'
            plugin.touch()
            with self.assertRaisesRegex(ValueError, 'job|CAD_LAYOUT_REVIEW_JOB'):
                runner.run(plugin, Path(root) / 'out', 'CAD_BILINGUAL_REVIEW_TEST')
            launch.assert_not_called()

    def test_review_job_requires_its_input_artifacts(self):
        with tempfile.TemporaryDirectory() as root, patch.object(runner.subprocess, 'Popen') as launch:
            plugin = Path(root) / 'NativeContactTests.dll'
            plugin.touch()
            job = Path(root) / 'empty-job'
            job.mkdir()
            with self.assertRaisesRegex(ValueError, 'config/export-job.json'):
                runner.run(plugin, Path(root) / 'out', 'CAD_BILINGUAL_REVIEW_TEST', job=job)
            launch.assert_not_called()

    def test_host_exit_without_report_is_structured_failure(self):
        with tempfile.TemporaryDirectory() as root, \
                patch.object(runner, 'discover_autocad', return_value=Path(root)), \
                patch.object(runner.subprocess, 'Popen') as launch:
            plugin = Path(root) / 'NativeContactTests.dll'
            plugin.touch()
            launch.return_value.poll.return_value = -1
            output = Path(root) / 'out'
            self.assertEqual(1, runner.run(plugin, output, 'CAD_NEAR_LABEL_TEST'))
            result = json.loads((output / 'report.json').read_text())
            self.assertEqual('failed', result['status'])
            self.assertEqual('host-exited-without-report', result['code'])

    def test_valid_review_job_is_forwarded_and_report_preserves_risks(self):
        with tempfile.TemporaryDirectory() as root, \
                patch.object(runner, 'discover_autocad', return_value=Path(root)), \
                patch.object(runner.subprocess, 'Popen') as launch:
            plugin = Path(root) / 'NativeContactTests.dll'
            plugin.touch()
            job, output = Path(root) / 'job', Path(root) / 'out'
            for name in runner.JOB_INPUTS['CAD_BILINGUAL_REVIEW_TEST']:
                file = job / name
                file.parent.mkdir(parents=True, exist_ok=True)
                file.touch()
            def start(*args, **kwargs):
                self.assertEqual(str(job.resolve()), kwargs['env']['CAD_LAYOUT_REVIEW_JOB'])
                Path(kwargs['env']['CAD_CONTACT_TEST_REPORT']).write_text(json.dumps(
                    {'status': 'passed', 'risks': [{'code': 'saved-text-overlap'}]}))
                launch.return_value.poll.return_value = 0
                return launch.return_value
            launch.side_effect = start
            self.assertEqual(0, runner.run(plugin, output, 'CAD_BILINGUAL_REVIEW_TEST', job=job))
            result = json.loads((output / 'report.json').read_text())
            self.assertEqual([{'code': 'saved-text-overlap'}], result['risks'])


if __name__ == '__main__':
    unittest.main()
