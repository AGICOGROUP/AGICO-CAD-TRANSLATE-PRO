# AutoCAD 2027 Bilingual Translation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a verified Chinese-English version of the supplied DWG through AutoCAD 2027 without changing the source file.

**Architecture:** Extend the Python runner with release-aware profile lookup and a job-owned bilingual sidecar so the existing sealed AutoCAD command contract remains backward compatible. Bilingual translations use the existing importer and layout optimizer by writing source Chinese followed by an English companion into each translated text slot; mode-specific gates require both languages and retain the existing collision/frame audit.

**Tech Stack:** Python 3 stdlib, unittest, PowerShell, AutoCAD 2027 Core Console, existing .NET 8 AutoCAD plugin.

**Spec:** `docs/superpowers/specs/2026-08-31-autocad-2027-bilingual-translation-design.md`

## Global Constraints

- Never overwrite the source DWG.
- AutoCAD 2027 uses registry release `R26.0` and root `D:\AutoCAD 2027\AutoCAD 2027`.
- Retain AutoCAD 2025 defaults and existing English-only behavior.
- Bilingual records retain Chinese and add English separated by an AutoCAD-compatible line break.
- Existing protected-marker, invariant-token, printable-frame, collision, and minimum-height gates remain mandatory.

---

### Task 1: Release-aware AutoCAD profile lookup

**Files:**
- Modify: `scripts/cad_translate.py`
- Test: `tests/test_cad_translate.py`

**Interfaces:**
- Produces: `autocad_release(autocad_root: Path) -> str`
- Produces: `profile(release: str = "R25.0") -> dict[str, object]`
- Changes: `doctor`, `require_ready`, and `run_once` pass the selected release through.

- [ ] Add a failing registry-path test for `profile("R26.0")` and a root mapping test for AutoCAD 2027.
- [ ] Run the targeted tests and confirm the missing argument/mapping failures.
- [ ] Implement release mapping and thread the release through preflight and execution.
- [ ] Run the targeted tests and the full Python suite.

### Task 2: Backward-compatible bilingual job mode

**Files:**
- Modify: `scripts/cad_translate.py`
- Test: `tests/test_cad_translate.py`

**Interfaces:**
- Produces: `write_output_mode(job: Path, mode: str) -> Path`
- Produces: `read_output_mode(job: Path) -> str`
- Changes: `check_translations(..., output_mode="english")` and `check_exported_candidate_language(..., output_mode="english")`.

- [ ] Add failing tests proving bilingual mode requires original Chinese plus non-Chinese English, while English-only mode still rejects Chinese.
- [ ] Add a failing test proving the sidecar is outside the strict .NET `JobConfig` JSON.
- [ ] Implement the sidecar and mode-specific gates with `english` and `bilingual` as the only allowed values.
- [ ] Pass `--output-mode bilingual` through export/import CLI handling without adding unmapped fields to AutoCAD job JSON.
- [ ] Run targeted tests and the full Python suite.

### Task 3: AutoCAD 2027 integration probe and export

**Files:**
- Runtime artifacts only: a new job directory under `D:\AGICO-CAD-TRANSLATE-PRO\jobs`

**Interfaces:**
- Consumes: release-aware runner and existing plugin bundle.
- Produces: sealed manifest and bounded translation worklist.

- [ ] Run doctor against the 2027 root and supplied DWG; require ready status.
- [ ] Run one export into a new isolated bilingual job; do not retry automatically.
- [ ] Prepare worklist parts with at most 6000 source characters each.
- [ ] Confirm source SHA-256 still matches the pre-export value.

### Task 4: Translation, import, and delivery

**Files:**
- Create: one translated JSONL per worklist part under the job's `exchange/translated-batches` directory.
- Deliver: `D:\测试文件\25-YX-1526 HCQ2000 工艺流程图2026.08.21-中英双语版.dwg`

**Interfaces:**
- Each translated batch contains only `recordId` and `translatedText`.
- For Chinese records, `translatedText` is the exact source Chinese, an AutoCAD paragraph break, then concise engineering English with protected markers retained exactly once and in order.

- [ ] Translate one bounded worklist part at a time using the cement glossary only if the drawing content is cement-process related.
- [ ] Assemble translations and require complete record/marker coverage.
- [ ] Run import once through AutoCAD 2027, including composition and post-composition export.
- [ ] Run audit summary; require no overflow, unreported collision, missing coverage, or marker failures.
- [ ] If visual review is required, inspect only reported changed/high-risk regions and reject unsafe output.
- [ ] Run status, verify the candidate exists, verify the source hash is unchanged, and copy the candidate to the bilingual delivery name.
