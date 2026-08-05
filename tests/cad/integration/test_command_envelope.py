from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

from tests.cad.integration.run_coreconsole import (
    build_coreconsole_command,
    ensure_autocad_profile,
    run_coreconsole,
    terminate_process_tree,
)


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
SAMPLE_DRAWING = REPOSITORY_ROOT / "cad样本1.dwg"


class CommandEnvelopeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory(prefix="cadtrans-envelope-")
        self.job_directory = Path(self.temporary_directory.name)
        self.working_path = self.job_directory / "working.dwg"
        shutil.copy2(SAMPLE_DRAWING, self.working_path)
        self.result_path = self.job_directory / "results" / "command-result.json"
        self.config_path = self.job_directory / "job.json"

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def test_successful_scan_result_envelope(self) -> None:
        self._write_config()
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        result = self._read_result(proc)
        self.assertEqual(result["status"], "succeeded")
        self.assertEqual(result["operation"], "scan")
        self.assertEqual(result["jobId"], "envelope-test")
        self.assertTrue((self.job_directory / "artifacts").is_dir())

    def test_all_declared_commands_write_success_envelopes(self) -> None:
        for operation in ("scan", "export", "import", "verify"):
            with self.subTest(operation=operation):
                self._write_config(operation=operation)
                proc = run_coreconsole(self.config_path, self.working_path, operation=operation)
                self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
                result = self._read_result(proc)
                self.assertEqual(result["status"], "succeeded")
                self.assertEqual(result["operation"], operation)

    def test_missing_config_is_not_an_interactive_prompt(self) -> None:
        missing_config = self.job_directory / "missing-job.json"
        collision_path = missing_config.with_suffix(".result.json")
        collision_path.write_text("must-not-overwrite", encoding="utf-8")
        proc = run_coreconsole(missing_config, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertEqual(collision_path.read_text(encoding="utf-8"), "must-not-overwrite")
        self.assertIn("CADTRANS_DIAGNOSTIC=", proc.stdout + proc.stderr)

    def test_absent_config_environment_writes_safe_diagnostic(self) -> None:
        proc = run_coreconsole(None, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertIn("CADTRANS_DIAGNOSTIC=", proc.stdout + proc.stderr)

    def test_missing_result_path_writes_safe_diagnostic(self) -> None:
        config = self._config()
        del config["resultPath"]
        self.config_path.write_text(json.dumps(config), encoding="utf-8")
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertIn("CADTRANS_DIAGNOSTIC=", proc.stdout + proc.stderr)

    def test_relative_contract_path_writes_failed_envelope(self) -> None:
        self._write_config(manifestPath="relative-manifest.jsonl")
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        result = self._read_result(proc)
        self.assertEqual(result["errors"][0]["code"], "invalid_config_path")

    def test_uppercase_source_hash_is_rejected_before_comparison(self) -> None:
        config = self._config()
        config["sourceSha256"] = str(config["sourceSha256"]).upper()
        self.config_path.write_text(json.dumps(config), encoding="utf-8")
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        result = self._read_result(proc)
        self.assertEqual(result["errors"][0]["code"], "invalid_source_hash")

    def test_runner_does_not_require_a_specific_language_pack(self) -> None:
        command = build_coreconsole_command(Path("C:/temp/run.scr"))
        self.assertEqual(command[1:3], ["/product", "ACAD"])
        self.assertNotIn("/language", command)

    def test_real_user_profile_preflight_finds_a_registered_profile(self) -> None:
        self.assertTrue(ensure_autocad_profile())

    def test_unwritable_diagnostic_never_crashes_command_host(self) -> None:
        config = self._config()
        del config["resultPath"]
        self.config_path.write_text(json.dumps(config), encoding="utf-8")
        diagnostic_file = self.job_directory / "not-a-directory"
        diagnostic_file.write_text("file", encoding="utf-8")
        proc = run_coreconsole(self.config_path, self.working_path, diagnostic_directory=diagnostic_file)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)

    def test_timeout_cleanup_terminates_a_real_child_process_tree(self) -> None:
        pid_directory = tempfile.TemporaryDirectory(prefix="cadtrans-timeout-")
        pid_path = Path(pid_directory.name) / "child.pid"
        child_script = "import time; time.sleep(60)"
        parent_script = (
            "import os,subprocess,sys,time; "
            f"child=subprocess.Popen([sys.executable, '-c', {child_script!r}], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL); "
            "open(sys.argv[1], 'w', encoding='ascii').write(str(child.pid)); time.sleep(60)"
        )
        parent = subprocess.Popen(
            [sys.executable, "-c", parent_script, str(pid_path)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
        deadline = time.monotonic() + 2
        while not pid_path.exists() and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertTrue(pid_path.exists())
        child_pid = int(pid_path.read_text(encoding="ascii"))
        terminate_process_tree(parent)
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            tasklist = subprocess.run(["tasklist", "/fi", f"PID eq {child_pid}"], capture_output=True, text=True, check=False)
            if parent.poll() is not None and str(child_pid) not in tasklist.stdout:
                break
            time.sleep(0.05)
        self.assertLess(time.monotonic(), deadline)
        pid_directory.cleanup()

    def test_operation_mismatch_writes_failed_result_envelope(self) -> None:
        self._write_config(operation="export")
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        result = self._read_result(proc)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["operation"], "scan")

    def test_changed_source_hash_writes_failed_result_envelope(self) -> None:
        config = self._config()
        config["sourceSha256"] = "0" * 64
        self.config_path.write_text(json.dumps(config), encoding="utf-8")
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        result = self._read_result(proc)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["errors"][0]["code"], "source_hash_mismatch")

    def test_strict_json_failure_writes_failed_result_envelope(self) -> None:
        config = self._config()
        config["unexpected"] = True
        self.config_path.write_text(json.dumps(config), encoding="utf-8")
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        result = self._read_result(proc)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["errors"][0]["code"], "invalid_config")

    def test_schema_mismatch_writes_failed_result_envelope(self) -> None:
        self._write_config(schemaVersion="2.0")
        proc = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        result = self._read_result(proc)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["errors"][0]["code"], "unsupported_schema")

    def test_command_has_no_interactive_parameters(self) -> None:
        self._write_config()
        first = run_coreconsole(self.config_path, self.working_path)
        second = run_coreconsole(self.config_path, self.working_path)
        self.assertEqual(first.returncode, 0, first.stdout + first.stderr)
        self.assertEqual(second.returncode, 0, second.stdout + second.stderr)
        self.assertGreater(self._read_result(second)["processedRecords"], 0)

    def _write_config(self, **changes: object) -> None:
        config = self._config()
        config.update(changes)
        self.config_path.write_text(json.dumps(config), encoding="utf-8")

    def _config(self) -> dict[str, object]:
        source_sha256 = hashlib.sha256(self.working_path.read_bytes()).hexdigest()
        return {
            "schemaVersion": "1.0",
            "jobId": "envelope-test",
            "operation": "scan",
            "sourcePath": str(self.working_path),
            "workingPath": str(self.working_path),
            "sourceSha256": source_sha256,
            "manifestPath": str(self.job_directory / "exchange" / "manifest.jsonl"),
            "translationPath": None,
            "outputPath": str(self.job_directory / "output.dwg"),
            "resultPath": str(self.result_path),
            "artifactDirectory": str(self.job_directory / "artifacts"),
            "sourceLanguage": "zh-CN",
            "targetLanguage": "en",
        }

    def _read_result(self, proc: object) -> dict[str, object]:
        return self._read_result_at(self.result_path, proc)

    def _read_result_at(self, result_path: Path, proc: object) -> dict[str, object]:
        self.assertTrue(result_path.is_file(), getattr(proc, "stdout", "") + getattr(proc, "stderr", ""))
        return json.loads(result_path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
