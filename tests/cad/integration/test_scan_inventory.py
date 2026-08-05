from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from tests.cad.integration.run_coreconsole import run_coreconsole


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
SAMPLE_DRAWING = REPOSITORY_ROOT / "cad样本1.dwg"


class ScanInventoryTests(unittest.TestCase):
    """The real sample must produce a deterministic, read-only drawing inventory."""

    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory(prefix="cadtrans-scan-")
        self.job_directory = Path(self.temporary_directory.name)
        self.working_path = self.job_directory / "working.dwg"
        shutil.copy2(SAMPLE_DRAWING, self.working_path)
        self.source_hash_before = hashlib.sha256(SAMPLE_DRAWING.read_bytes()).hexdigest()
        self.config_path = self.job_directory / "job.json"
        self.result_path = self.job_directory / "result.json"
        self.artifact_directory = self.job_directory / "artifacts"

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def test_real_sample_scan_has_complete_inventory_without_source_mutation(self) -> None:
        self._write_config()

        proc = run_coreconsole(self.config_path, self.working_path, operation="scan")

        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        scan_path = self.artifact_directory / "scan.json"
        self.assertTrue(scan_path.is_file(), proc.stdout + proc.stderr)
        scan = json.loads(scan_path.read_text(encoding="utf-8"))
        self.assertEqual(scan["drawingVersion"], "AC1032")
        self.assertEqual(scan["counts"]["BlockDefinitions"], 1172)
        self.assertEqual(scan["counts"]["AnonymousBlocks"], 1082)
        self.assertEqual(scan["counts"]["TEXT"], 2357)
        self.assertEqual(scan["counts"]["MTEXT"], 1122)
        self.assertEqual(scan["counts"]["ATTRIB"], 256)
        self.assertEqual(scan["counts"]["ATTDEF"], 35)
        self.assertEqual(scan["counts"]["DIMENSION"], 1065)
        self.assertIn("xrefs", scan)
        self.assertIn("fonts", scan)
        self.assertIn("proxies", scan)
        self.assertIn("layouts", scan)
        self.assertIn("blockers", scan)
        self.assertTrue(any(font["file"].lower() == "simplex.shx" and font["exists"] for font in scan["fonts"]))
        self.assertTrue(any(not font["exists"] for font in scan["fonts"]))
        self.assertEqual(scan["sourceSha256Before"], self.source_hash_before)
        self.assertEqual(scan["sourceSha256After"], self.source_hash_before)
        self.assertEqual(hashlib.sha256(SAMPLE_DRAWING.read_bytes()).hexdigest(), self.source_hash_before)

    def _write_config(self) -> None:
        source_sha256 = hashlib.sha256(self.working_path.read_bytes()).hexdigest()
        config = {
            "schemaVersion": "1.0",
            "jobId": "scan-inventory-test",
            "operation": "scan",
            "sourcePath": str(SAMPLE_DRAWING),
            "workingPath": str(self.working_path),
            "sourceSha256": source_sha256,
            "manifestPath": str(self.job_directory / "exchange" / "manifest.jsonl"),
            "translationPath": None,
            "outputPath": str(self.job_directory / "output.dwg"),
            "resultPath": str(self.result_path),
            "artifactDirectory": str(self.artifact_directory),
            "sourceLanguage": "zh-CN",
            "targetLanguage": "en",
        }
        self.config_path.write_text(json.dumps(config), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
