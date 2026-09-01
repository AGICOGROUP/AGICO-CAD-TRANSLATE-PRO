# Independent Translation Pipelines Implementation Plan

> Execute on `codex/cad-layout-v2`. Keep local `main` fixed at `c0559ed`.

**Goal:** Provide separately managed replace and bilingual CAD translation pipelines, with independent composition, layout, gates, and reports.

**Architecture:** Python performs a single early dispatch to a mode-specific workflow. AutoCAD receives the normalized mode in sealed job configuration and dispatches to a dedicated C# pipeline. The pipelines may use neutral I/O and geometry helpers, but own all translation decisions and pass/fail gates.

**Runtime:** Python standard library, C#/.NET 10 for AutoCAD 2027, NUnit-free executable C# tests, AutoCAD Core Console.

---

## Task 1: Normalize the public output-mode contract

**Files:**
- Modify: `scripts/cad_translate.py`
- Modify: `tests/test_cad_translate.py`
- Modify: `src/cad/CadTranslation.Contracts/JobConfig.cs`
- Modify: `src/cad/CadTranslation.Contracts/JobConfigParser.cs`
- Modify: corresponding contract tests under `src/cad/CadTranslation.Core.Tests/`

1. Add failing Python tests proving the default is `replace`, legacy `english` reads as `replace`, new metadata writes `replace`, and unknown modes fail.
2. Add failing C# tests proving `outputMode` is required/normalized in new sealed import jobs and included in configuration integrity.
3. Implement `normalize_output_mode()` with `english -> replace` migration and `replace|bilingual` canonical values.
4. Write the canonical mode into job configuration instead of relying on an unsealed sidecar for new jobs; retain sidecar read compatibility only for legacy jobs.
5. Run targeted Python and C# contract tests.
6. Commit: `feat: normalize translation output modes`.

## Task 2: Introduce an early Python pipeline dispatcher

**Files:**
- Create: `scripts/pipelines/__init__.py`
- Create: `scripts/pipelines/replace.py`
- Create: `scripts/pipelines/bilingual.py`
- Create: `scripts/pipeline_io.py`
- Modify: `scripts/cad_translate.py`
- Modify: `tests/test_cad_translate.py`

1. Add failing spy-based tests proving every command dispatches once to the selected pipeline.
2. Add isolation tests proving replace does not call bilingual checks and bilingual does not call replace checks.
3. Move only neutral file/process/hash helpers into `pipeline_io.py`.
4. Give each pipeline its own export preparation, translation check, candidate-language check, audit aggregation, and final-decision functions. Duplicate current rules where necessary rather than creating a shared business gate.
5. Keep the CLI thin: normalize, dispatch, and return the selected pipeline result.
6. Run the Python suite and commit: `refactor: split python translation pipelines`.

## Task 3: Lock independent Python gates with behavioral tests

**Files:**
- Modify: `scripts/pipelines/replace.py`
- Modify: `scripts/pipelines/bilingual.py`
- Modify: `tests/test_cad_translate.py`

1. Add replace-gate tests for source-language residue, invalid/protected tokens, stale artifacts, and replace-specific summary fields.
2. Add bilingual-gate tests for retained source text, required target text, valid existing pairs, technical-code false positives, duplicate companions, stale artifacts, and bilingual-specific summary fields.
3. Implement the two gate suites separately, including separate report schemas and pass/fail aggregation.
4. Add a regression test that deliberately changes/injects one gate result and proves the other pipeline is unaffected.
5. Run the Python suite and commit: `feat: isolate mode-specific translation gates`.

## Task 4: Split AutoCAD import into two pipelines

**Files:**
- Create: `src/cad/CadTranslation.AutoCAD2025/ReplaceImportPipeline.cs`
- Create: `src/cad/CadTranslation.AutoCAD2025/BilingualImportPipeline.cs`
- Create: `src/cad/CadTranslation.AutoCAD2025/ImportInfrastructure.cs`
- Modify: `src/cad/CadTranslation.AutoCAD2025/Importer.cs`
- Modify: `src/cad/CadTranslation.Core.Tests/Program.cs`

1. Add failing C# routing tests proving `replace` and legacy `english` select only `ReplaceImportPipeline`, while `bilingual` selects only `BilingualImportPipeline`.
2. Extract neutral database opening, manifest resolution, atomic output, and JSON writing into infrastructure without pass/fail policy.
3. Move stable pre-merge composition and correction behavior into `ReplaceImportPipeline`, using `LayoutOptimizer` and its replace-owned audit loop.
4. Move the last-tested preserve/add/deduplicate behavior into `BilingualImportPipeline`, using `BilingualFixedLabelPolicy` and `LayoutOptimizerV2`.
5. Reduce `Importer.Run` to validation plus one pipeline dispatch.
6. Build for AutoCAD 2027 and run the executable C# tests.
7. Commit: `refactor: split AutoCAD import pipelines`.

## Task 5: Give each AutoCAD pipeline its own final gates and artifacts

**Files:**
- Create: `src/cad/CadTranslation.AutoCAD2025/ReplaceImportGate.cs`
- Create: `src/cad/CadTranslation.AutoCAD2025/BilingualImportGate.cs`
- Modify: both import pipeline files
- Modify: `src/cad/CadTranslation.Core.Tests/Program.cs`

1. Add failing tests proving each gate independently evaluates coverage, topology, overlap, fit, language outcome, and audit integrity.
2. Implement replace-owned artifact names and final decision; do not call bilingual gate code.
3. Implement bilingual-owned artifact names and final decision; do not call replace gate code.
4. Record mode, language pair, pipeline version, config/source/output hashes, and gate results in each mode's summary.
5. Add stale cross-mode artifact tests.
6. Build/test and commit: `feat: add independent AutoCAD gate suites`.

## Task 6: Update the skill workflow and operator documentation

**Files:**
- Modify: `SKILL.md`
- Modify: relevant files under `references/`
- Modify: `tests/test_cad_translate.py`

1. Add failing documentation/CLI contract assertions for mode selection and defaults.
2. Document `replace` as default and `bilingual` as explicit preserve-and-add behavior.
3. Document independent artifacts, gates, failure handling, and the verified `zh-CN -> en` scope.
4. State that a user request for bilingual translation must never run the replace pipeline, and vice versa.
5. Run the full automated suite and commit: `docs: publish dual translation workflows`.

## Task 7: End-to-end AutoCAD 2027 verification

**Files/artifacts:**
- Use separate fresh job directories outside the source drawing directory.
- Do not overwrite either source DWG.

1. Build/install the AutoCAD 2027 plugin with .NET SDK 10.0.400.
2. Run a fresh replace job against the established HCQ2000 fixture and verify its replace-owned gates and AutoCAD `AUDIT` result.
3. Run a fresh bilingual job against the last-tested fixture and verify source retention, target presence, no duplicate existing pairs, layout V2 results, and AutoCAD `AUDIT`.
4. Render/review the title block and dense regions that previously overlapped.
5. Confirm changing mode cannot reuse the other run's artifacts.
6. Run `git diff --check`, Python tests, C# tests, both builds, and inspect `git status`.
7. Commit any fixture-driven corrections separately, then report exact output paths and verification evidence. Do not merge into `main` without explicit user approval.
