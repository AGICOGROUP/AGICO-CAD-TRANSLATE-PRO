# CAD Translation Layout v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a testable v2 branch that retains the stable CAD translation pipeline and replaces multi-pass layout heuristics with one deterministic source-derived layout decision per changed text object.

**Architecture:** Add a pure core planner that produces immutable layout decisions, then add a single AutoCAD optimizer that applies those decisions once. Switch the importer to v2 and remove its global correction loop while retaining the final hard audit.

**Tech Stack:** C#/.NET 8, AutoCAD 2025 managed API, Python 3 `unittest`, PowerShell runner.

## Global Constraints

- Never overwrite a source drawing.
- Preserve the existing translation, terminology, bilingual cleanup, and language-gate behavior.
- Preserve source text height first; use wrap, width compression, then height reduction with a 55% floor.
- Changed text must stay inside one source-derived region and outside hard keep-outs and the printable frame.
- v2 performs one fit and one audit; unresolved high risk fails instead of invoking global correction.

---

### Task 1: Deterministic layout decisions

**Files:**
- Create: `src/cad/CadTranslation.Core/LayoutV2Planner.cs`
- Modify: `src/cad/CadTranslation.Core.Tests/Program.cs`

**Interfaces:**
- Consumes: `CadLayoutText`, `CadDefinitionTopology`, `Rect2`, and existing region kinds.
- Produces: `LayoutV2Decision` and `LayoutV2Planner.Plan(...)`.

- [ ] Add failing tests for table-cell containment, narrative keep-out clamping, peer exclusivity, and complete record coverage.
- [ ] Run the C# suite and confirm failures are caused by missing v2 types.
- [ ] Implement the smallest pure planner that makes those tests pass.
- [ ] Run the C# suite and confirm all tests pass.
- [ ] Commit the planner and tests.

### Task 2: Single-pass AutoCAD optimizer

**Files:**
- Create: `src/cad/CadTranslation.AutoCAD2025/LayoutOptimizerV2.cs`
- Modify: `src/cad/CadTranslation.AutoCAD2025/Importer.cs`
- Modify: `src/cad/CadTranslation.AutoCAD2025/TableCellLayout.cs`
- Modify: `src/cad/CadTranslation.Core.Tests/Program.cs`

**Interfaces:**
- Consumes: `LayoutV2Decision[]` from the pure planner and `LayoutTargetSnapshot[]`.
- Produces: the existing `LayoutOptimizationResult` audit contract.

- [ ] Add failing tests for source-height-first priority, explicit narrative wrap width, and disabled multi-pass correction.
- [ ] Run the focused C# tests and observe the expected failures.
- [ ] Implement one-pass routing through the v2 planner and fit executor.
- [ ] Run all C# tests and fix only v2 regressions.
- [ ] Commit the optimizer switch.

### Task 3: Skill contract and package integrity

**Files:**
- Modify: `SKILL.md`
- Modify: `tests/test_cad_translate.py`
- Modify: `agents/openai.yaml` only if its description is stale.
- Update: `assets/plugin/*.dll`

**Interfaces:**
- Consumes: the existing runner commands.
- Produces: a concise v2 workflow and DLL package matching the branch source.

- [ ] Add failing Python contract tests that reject global correction language and require one-fit/one-audit wording.
- [ ] Run the Python suite and confirm the new contract test fails.
- [ ] Reduce the skill body to core execution steps and four layout invariants.
- [ ] Build Release DLLs and copy them to `assets/plugin`.
- [ ] Run Python and C# suites, including package hash verification.
- [ ] Commit the skill and binaries.

### Task 4: Branch delivery

**Files:**
- Verify all changed files in this branch.

**Interfaces:**
- Produces: remote branch `codex/cad-layout-v2` for real DWG testing.

- [ ] Run a clean Release build.
- [ ] Run all C# tests.
- [ ] Run all Python tests with UTF-8 mode enabled.
- [ ] Inspect `git diff --check` and branch status.
- [ ] Push `codex/cad-layout-v2` to `origin`.
