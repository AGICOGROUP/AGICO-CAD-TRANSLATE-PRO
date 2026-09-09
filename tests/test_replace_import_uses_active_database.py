import unittest
from pathlib import Path


class ReplaceImportDatabaseTests(unittest.TestCase):
    def test_replace_import_uses_autocad_active_working_database(self):
        source = (
            Path(__file__).resolve().parents[1]
            / "src"
            / "cad"
            / "CadTranslation.AutoCAD2025"
            / "ReplaceDrawingImporter.cs"
        ).read_text(encoding="utf-8")

        run_body = source.split("internal static int Run(JobContext context)", 1)[1].split(
            "private static ResolvedWrite[] ResolveAll", 1
        )[0]
        self.assertIn("HostApplicationServices.WorkingDatabase", run_body)
        self.assertIn("LockDocument()", run_body)
        self.assertIn("File.Copy(temporaryOutput, auditOutput", run_body)
        self.assertNotIn("workingDatabase.Wblock()", run_body)
        self.assertNotIn("new Database(false, true)", run_body)
        self.assertNotIn("ReadWorkingDrawing(sideDatabase", run_body)

    def test_block_walker_skips_unresolved_legacy_block_references(self):
        source = (
            Path(__file__).resolve().parents[1]
            / "src"
            / "cad"
            / "CadTranslation.AutoCAD2025"
            / "BlockInstanceWalker.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("reference.BlockTableRecord.IsNull", source)

    def test_structure_signature_ignores_sub_nanounit_save_rounding(self):
        source = (
            Path(__file__).resolve().parents[1]
            / "src"
            / "cad"
            / "CadTranslation.AutoCAD2025"
            / "DrawingVerifier.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("Math.Round(value, 9)", source)
        self.assertIn("Number(extents.MinPoint.X)", source)
        self.assertIn("StableOwnerPath(item.OwnerPath)", source)
        self.assertIn("record.IsAnonymous", source)


if __name__ == "__main__":
    unittest.main()
