import importlib.util
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("deploy_skill", Path(__file__).resolve().parents[1] / "scripts" / "deploy_skill.py")

class DeploymentTests(unittest.TestCase):
    def test_changed_files_are_backed_up_and_unrelated_jobs_preserved(self):
        module = importlib.util.module_from_spec(SPEC)
        SPEC.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source, target = root / "source", root / "mounted"
            source.mkdir(); target.mkdir()
            (source / "SKILL.md").write_text("new", encoding="utf-8")
            (target / "SKILL.md").write_text("old", encoding="utf-8")
            (target / "jobs").mkdir()
            (target / "jobs" / "drawing.dwg").write_bytes(b"original")
            receipt = module.deploy(source, target, ["SKILL.md"], [])
            self.assertEqual("new", (target / "SKILL.md").read_text(encoding="utf-8"))
            self.assertEqual("old", (Path(receipt["backup"]) / "SKILL.md").read_text(encoding="utf-8"))
            self.assertEqual(b"original", (target / "jobs" / "drawing.dwg").read_bytes())
            with self.assertRaises(ValueError): module.deploy(source, target, ["../outside"], [])
