# CAD translation runtime V2

The mounted skill and repository must use the same maintained entrypoint. User intent selects a sealed `replace` or `bilingual` job; language direction is explicit. Sharing file I/O and host launch is allowed, sharing mode-specific mutation or acceptance decisions is not.

## Changes

1. Replace the mixed importer with an explicit dispatcher and independent mutation implementations. Replacement retains the proven fitter. Bilingual creates target-only MText beside an unchanged source and records source-to-target handles, including existing pairs.
2. Capture topology once for replacement. Reopen and extract the final candidate inside the import process; avoid extra compose/export processes. The replacement-only composition stage runs in that same process and changes only eligible content, with its own output verification.
3. Deduplicate translation requests only for identical protected text and compatible context. Restore complete per-entity coverage locally. Expose compact layout hints to the translator.
4. Independent, fail-closed final gates. Bilingual verifies original entities/properties, target presence, duplicates and placement; replacement verifies target language, coverage, layout and structure. Pending visual review is not delivery success.
5. Fix stale result envelopes, mode/direction tampering, host discovery and measure stage times. Keep source/output hashes in delivery evidence.
6. Deploy the tracked skill resources to the mounted root with recoverable backup and a deployment receipt.

## Verification

Run Python and .NET regression suites; build net8.0-windows/x64 against the installed AutoCAD 2025. Use the HCQ2000 drawing and its existing reviewed English translations for old/new runtime comparison, then test additive bilingual output and reverse direction on a small native fixture. Report unsupported cases and pending visual checks explicitly. Do not claim a speedup from incomparable runs or count model translation time as measured CAD time.

## Recorded validation — 2026-09-08

- AutoCAD 2025 on E:, .NET 8.0.424, x64 build: 0 errors; 3 existing Autodesk assembly warnings.
- Python: 55 tests passed. Core: 154 tests passed. Native fixtures: 6/6 automatic checks passed (both directions, both modes, repeated bilingual).
- Native repeated bilingual fixtures: first pass adds 12 and reuses 1; second pass adds 0 and reuses 13. Original entities and non-text structure retained. Equivalent requests deduplicate 13 source instances to 1 work item.
- HCQ2000 replacement: original baseline local export/assembly/import sequence 8.0018 s; final sequence 5.54 s (~31% less). Same original DWG, local assembly from previously reviewed translations; final includes three space/terminology refinements (Qty., Std., Tanker Silo). Single-run reference, not a statistical benchmark or model/visual-review speed claim. Host launches reduced from 4 to 2. Source SHA256 unchanged: `5cd601f53013afe3e497f1cde800b094db7ffc0568a24736a643eac342b42ef2`.
- Fixed stale MText extents, title-block merged-cell recognition and attribute-coordinate handling. Source/candidate title images were inspected: company name wraps inside its own cell; neighboring title text remains separate.
- HCQ2000 strict bilingual stress test: 169 originals retained, 54 additions, 26 labels cannot be placed safely after recognizing their actual cell boundaries. This is a rejected/staged diagnostic, NOT a finished bilingual deliverable. Earlier more permissive placement counts were not evidence of safe layout. Do not bypass the gate to increase completion counts.
- Fixture bilingual overviews in both directions were inspected; no extra duplicate labels. Full-sheet visual acceptance for arbitrary user drawings remains required.
- Independent skill scenarios verified routing, target-only batches, sealed jobs, reverse direction and non-delivery without visual evidence. Review also exposed and led to fixes for code-only pseudo-translations and missing final Chinese-target checks.

## Maintenance and deployment

Maintain this repository; use `python scripts/deploy_skill.py --target "D:/AGICO-CAD-TRANSLATE-PRO"` to synchronize the mounted copy. Only Git-listed/unignored maintained resource files are deployed. Each differing destination file is backed up under `deploy-backups/<UTC timestamp>` with SHA256 receipt. Jobs, runs and unrelated files are not touched. Build/deploy native DLLs only after native changes, not once per translation.

Independent entrypoints: `scripts/pipelines/replace.py` + `ReplaceImportPipeline.cs` / `ReplaceDrawingImporter.cs`; `scripts/pipelines/bilingual.py` + `BilingualDrawingImporter.cs`. Shared topology, JSON and host transport do not select mode or share final acceptance decisions.
