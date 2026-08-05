from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from tests.cad.integration.run_coreconsole import run_coreconsole


ROOT = Path(__file__).resolve().parents[3]
SAMPLE = next(ROOT.glob("*.dwg"))


class ExportMvpSmokeTests(unittest.TestCase):
    """One read-only real-sample export validates the MVP exchange boundary."""

    def test_export_is_nonempty_utf8_and_keeps_source_hash(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cadtrans-export-") as temporary:
            job = Path(temporary)
            working = job / "working.dwg"
            shutil.copy2(SAMPLE, working)
            source_hash = hashlib.sha256(SAMPLE.read_bytes()).hexdigest()
            manifest = job / "exchange" / "manifest.input.jsonl"
            config = {
                "schemaVersion": "1.0", "jobId": "mvp-export-smoke", "operation": "export",
                "sourcePath": str(SAMPLE), "workingPath": str(working), "sourceSha256": source_hash,
                "manifestPath": str(manifest), "translationPath": None, "outputPath": str(job / "output.dwg"),
                "resultPath": str(job / "result.json"), "artifactDirectory": str(job / "artifacts"),
                "sourceLanguage": "zh-CN", "targetLanguage": "en",
            }
            config_path = job / "job.json"
            config_path.write_text(json.dumps(config), encoding="utf-8")
            proc = run_coreconsole(config_path, working, operation="export")
            self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
            self.assertTrue(manifest.is_file(), proc.stdout + proc.stderr)
            payload = manifest.read_bytes()
            self.assertFalse(payload.startswith(b"\xef\xbb\xbf"))
            records = [json.loads(line) for line in payload.decode("utf-8").splitlines()]
            self.assertTrue(records)
            self.assertEqual(len(records), len({record["recordId"] for record in records}))
            self.assertTrue(any("/BLOCK/" in record["ownerPath"] for record in records))
            self.assertEqual(hashlib.sha256(SAMPLE.read_bytes()).hexdigest(), source_hash)


if __name__ == "__main__":
    unittest.main()
