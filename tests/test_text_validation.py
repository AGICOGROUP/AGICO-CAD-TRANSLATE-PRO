"""Core CLI integration and deployment discovery, without AutoCAD."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))


class TextValidationTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("text_validation"), "Python Core validation bridge is required")
        import text_validation
        self.bridge = text_validation
        self.tmp = tempfile.TemporaryDirectory(prefix="text validation ")
        self.addCleanup(self.tmp.cleanup)
        self.manifest = Path(self.tmp.name) / "manifest.jsonl"
        self.translations = Path(self.tmp.name) / "translations.jsonl"
        self.manifest.write_text(json.dumps({"recordId": "r1", "handle": "AB", "inputHash": "hash",
            "rawText": "图号09", "plainText": "图号09", "protectedTokens": []}, ensure_ascii=False) + "\n", encoding="utf-8")
        self.write_translation("Drawing No.09")

    def write_translation(self, text):
        self.translations.write_text(json.dumps({"recordId": "r1", "inputHash": "hash",
            "translatedText": text, "reviewStatus": "approved"}) + "\n", encoding="utf-8")

    def test_real_cli_valid_and_invalid_preserves_record_diagnostics(self):
        self.assertEqual({"isValid": True, "errors": []}, self.bridge.validate_batch(self.manifest, self.translations))
        self.write_translation("Drawing No.10")
        report = self.bridge.validate_batch(self.manifest, self.translations)
        self.assertFalse(report["isValid"])
        error = report["errors"][0]
        self.assertEqual(("numeric_or_protected_token_mismatch", "r1", "AB"),
                         (error["code"], error["recordId"], error["handle"]))
        self.assertIn("09", error["message"])
        self.assertIn("10", error["message"])

    def test_malformed_json_is_execution_failure(self):
        self.translations.write_text("{broken\n", encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "line 1"):
            self.bridge.validate_batch(self.manifest, self.translations)

    def test_missing_override_never_falls_back(self):
        with patch.dict(os.environ, {"CAD_TRANSLATE_TEXTCLI": str(Path(self.tmp.name) / "missing.exe")}):
            with self.assertRaisesRegex(RuntimeError, "CAD_TRANSLATE_TEXTCLI"):
                self.bridge.validate_batch(self.manifest, self.translations)

    def test_explicit_executable_uses_argument_list_and_timeout(self):
        exe = Path(self.tmp.name) / "validator.exe"
        exe.touch()
        result = subprocess.CompletedProcess([], 0, '{"isValid":true,"errors":[]}', "")
        with patch.dict(os.environ, {"CAD_TRANSLATE_TEXTCLI": str(exe)}), patch.object(self.bridge.subprocess, "run", return_value=result) as run:
            self.bridge.validate_batch(self.manifest, self.translations)
        self.assertEqual([str(exe), "--manifest", str(self.manifest), "--translations", str(self.translations)], run.call_args.args[0])
        self.assertGreater(run.call_args.kwargs["timeout"], 0)
        self.assertNotIn("shell", run.call_args.kwargs)

    def test_developer_fallback_builds_once(self):
        repo = Path(self.tmp.name)
        project = repo / "src/cad/CadTranslation.TextCli/CadTranslation.TextCli.csproj"
        project.parent.mkdir(parents=True)
        project.touch()
        built = project.parent / "bin/Release/net8.0/CadTranslation.TextCli.dll"
        calls = []
        def run(args, **kwargs):
            calls.append(args)
            if "build" in args:
                built.parent.mkdir(parents=True, exist_ok=True)
                built.touch()
                built.with_suffix(".runtimeconfig.json").write_text("{}")
                return subprocess.CompletedProcess(args, 0, "build succeeded", "")
            return subprocess.CompletedProcess(args, 0, '{"isValid":true,"errors":[]}', "")
        with patch.dict(os.environ, {}, clear=True), patch.object(self.bridge, "ROOT", repo), patch.object(self.bridge.subprocess, "run", side_effect=run):
            self.bridge.validate_batch(self.manifest, self.translations)
            self.bridge.validate_batch(self.manifest, self.translations)
        self.assertEqual(1, sum("build" in call for call in calls))

    def test_missing_runtime_is_explicit(self):
        with patch.object(self.bridge.subprocess, "run", side_effect=FileNotFoundError("dotnet")):
            with self.assertRaisesRegex(RuntimeError, "text validation"):
                self.bridge.validate_batch(self.manifest, self.translations)

    def test_build_failure_is_explicit(self):
        repo = Path(self.tmp.name)
        project = repo / "src/cad/CadTranslation.TextCli/CadTranslation.TextCli.csproj"
        project.parent.mkdir(parents=True)
        project.touch()
        failure = subprocess.CompletedProcess([], 1, "SDK build failed", "compiler diagnostic")
        with patch.dict(os.environ, {}, clear=True), patch.object(self.bridge, "ROOT", repo), patch.object(self.bridge.subprocess, "run", return_value=failure):
            with self.assertRaisesRegex(RuntimeError, "SDK build failed"):
                self.bridge.validate_batch(self.manifest, self.translations)

    def test_real_dll_override(self):
        dll = ROOT / "src/cad/CadTranslation.TextCli/bin/Release/net8.0/CadTranslation.TextCli.dll"
        # Ensure a fresh checkout has built the CLI before exercising the override.
        self.bridge.validate_batch(self.manifest, self.translations)
        with patch.dict(os.environ, {"CAD_TRANSLATE_TEXTCLI": str(dll)}):
            self.assertTrue(self.bridge.validate_batch(self.manifest, self.translations)["isValid"])


if __name__ == "__main__":
    unittest.main()
