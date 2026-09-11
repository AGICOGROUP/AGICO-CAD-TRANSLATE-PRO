# CAD Translation Refactor Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development task-by-task. Preserve the user's existing baseline; replace obsolete logic rather than append competing workflows.

**Goal:** Improve technical translation integrity, automatic completion and recovery while reducing repeated CAD work.
**Architecture:** Keep replacement and bilingual mutation/acceptance independent. Centralize pure text validation in Core, give the CLI deterministic recovery and local correction actions, and reuse saved-candidate inspection. Do not lower content/geometry standards to clear failures.
**Tech Stack:** Python stdlib, .NET 8, AutoCAD 2025 native managed API.
**Spec:** `docs/CAD翻译重构评审方案.md`

## Global Constraints

- Work only on `codex/cad-translation-refactor` in repository. Baseline is `c6dd8e7`; do not modify mounted skill or production DLLs.
- Never overwrite source drawings. Native tests use owned copies and branch DLLs.
- Preserve complete technical meaning, numbers, units, source/target association and non-text geometry. Cosmetic warnings remain deliverable after AI review.
- Run focused regression failures before fixes; reuse existing suites and representative drawings. Distinguish native timings from fresh translation timings.
- Use existing artifacts and CLI conventions, not a workflow platform or new MCP server.

### Task 1: Authoritative text validation before CAD

Files: Core/ProtectedText.cs, Core/TranslationValidator.cs, new `src/cad/CadTranslation.TextCli/`, new `scripts/text_validation.py`, focused new tests. Root integrates the Python caller into cad_translate.py after this task.

Interfaces: `text_validation.validate_batch(manifest_path, translations_path)` returns a report with `isValid` and `errors` (code, message, recordId, handle). On invalid content raise a ValueError in the Python integration, preserve exact per-record diagnostics. Core remains authoritative. A built CLI is resolved from environment `CAD_TRANSLATE_TEXTCLI` or repository build artifacts; a developer fallback may build once, never once per drawing. Do not require AutoCAD for text validation.

- [ ] Add a standalone focused C# regression console referencing Core (do not edit Core.Tests/Program.cs concurrently). Assert these real behaviors before implementation:
  ```csharp
  Assert(TranslationValidator.HasSameInvariantTokens("图号09", "Drawing No.09"));
  Assert(!TranslationValidator.HasSameInvariantTokens("图号09", "Drawing No.10"));
  Assert(!TranslationValidator.HasSameInvariantTokens("MDPX150", "MDPX160"));
  Assert(ProtectedText.Parse(@"\T1.001;%%C14@100双层双向").ProtectedTokens[0].Kind == "mtext-code");
  ```
- [ ] Run focused test and confirm failures. Then fix token boundaries, supported CAD codes, and invariant comparison without treating natural-language words as units. Preserve real model codes and numbers. Do not guess ambiguous `δ20,5块`; retain separate context/meaning and reject real quantity changes.
- [ ] Implement pure JSONL validation CLI with arguments `--manifest PATH --translations PATH`, stdout JSON report and exit 0 valid / 1 invalid / 2 execution failure. Use Core `ValidateBatch`. Include actual differing invariant values in mismatch messages where possible.
- [ ] Implement Python bridge and tests for executable invocation, invalid batch reporting and no repeated builds. Use `subprocess` argument lists and bounded timeouts. Missing runtime/build failure is explicit, never silently skip validation.
- [ ] Verify full Core tests and Python bridge tests; validate retained DWG11 r2 batch. Self-review and report exact changes/tests/remaining risks. Commit only owned files.

### Task 2: Saved-drawing structure identity and inspection

Files: DrawingVerifier.cs, NonTextStructureSignaturePolicy.cs, NativeDrawing.cs; new native regression helper under tests/cad/refactor-native. Root owns these changes.

- [ ] Add native no-edit save comparison for DWG11 using source hash, owned output and branch DLLs. Run before changing structure policy; write bounded evidence including source/candidate row differences.
- [ ] Determine whether differences arise from SaveAs cloning/remapping or genuine changes. Match stable identities and block relation, distinguish added/missing/changed objects; do not ignore entire anonymous blocks.
- [ ] If same-database SaveAs mutates anonymous definitions, test a non-destructive alternative (e.g. SaveAs flags) and adopt only with unchanged geometry evidence. Run real movement/deletion negative controls.
- [ ] Reuse saved-candidate open for structure/content inspection and emit useful errors. Expose native `inspect` operation for an existing candidate without translation/placement.

### Task 3: Recovery and local correction

Files: scripts/cad_translate.py, scripts/pipeline_io.py, new scripts/task_recovery.py, mode pipelines, JobContext/Commands, new native LocalCorrectionPipeline.cs. Root integrates after Tasks 1–2.

- [ ] Add failing tests for incomplete-batch recovery preserving good records, retained staged candidate selection, successful-stage reuse, changed source/candidate invalidation and actionable command failure details.
- [ ] Integrate the authoritative pre-CAD validator from Task 1. Remove differing duplicate invariant logic; keep cheap schema/marker checks that aggregate repair context.
- [ ] Add `resume --job PATH` that uses bound source/mode/translations and existing artifacts to run only required work. Never automatically accept a candidate or fabricate a review. Candidate reinspection is distinct from full native import.
- [ ] Add `correct --job PATH --corrections PATH` accepting finite native text edits keyed by target handle with expected current content; preserve original drawing and mode invariants, complete approved translation, and source linkage. Batch edits once, verify resulting candidate and invalidate obsolete review hashes.
  ```json
  {"candidateSha256":"...","edits":[{"handle":"AB","expectedText":"...","width":50,"height":3,"x":10,"y":20}]}
  ```
- [ ] Add branch plugin override `CAD_TRANSLATE_PLUGIN_DIR` for isolated native validation; save DLL fingerprints with native timing/diagnostics. No production deployment required.
- [ ] Run fixtures for both modes/directions, verify repeated bilingual remains idempotent, run reinspection on DWG11 and one local correction with unchanged unaffected source.

### Task 4: Measured efficiency, skill replacement and final validation

Files: layout hot paths only when measurements warrant, render_review.py, SKILL.md, references/{replace,bilingual,visual-audit}-workflow docs and final validation report.

- [ ] Measure topology/layout/save/inspection within existing timing reports. Reuse parsed task state and unchanged candidate reads; eliminate demonstrably repeated work without a large new cache.
- [ ] Make render retries reuse only images bound to the same source/candidate and window; otherwise generate versioned fresh images. Preserve source/candidate pairing and readable review coverage.
- [ ] Replace duplicated skill workflows with standard run, resume, correct, render and delivery instructions. Keep AI semantic/visual judgement and actual supported languages. Do not add approvals or cosmetic-only loops.
- [ ] Run Python/Core/native targeted suites, original-source hashes, DWG11 diagnosis/recovery and representative output renders. Report measured native and end-to-end scope honestly, with unresolved material failures explicit.
- [ ] Whole-branch code review, fix actionable findings, commit branch. Leave production deployment and main unchanged.

## Execution record

- Baseline `c6dd8e7`: saved pre-existing 23-file improvements independently.
- Tasks 1 and 3 share Python interface only: Task 1 owns new text_validation.py; root owns existing cad_translate.py. Task 2 and Task 3 share native verification: root implements sequentially. Task 4 documentation follows final interfaces. No competing mutations.
- Task 1 text tests precede fixes. Task 2 policy depends on native evidence. Task 3 recovery never makes incomplete output deliverable. Task 4 measures native and total work separately.
