# CAD Translation Layout v2 Design

## Decision

Keep the verified extraction, translation batching, terminology, bilingual cleanup, source sealing, and CJK residue gates. Replace only the layout decision layer. Do not rewrite the AutoCAD transport or translation workflow.

## Problem

Layout v1 routes text through independent table, note-column, fixed-label, and global-correction heuristics. Each stage can derive a different allowed rectangle, and later correction passes can undo an earlier fit. New drawing styles therefore trigger sample-specific patches and inconsistent results.

## Core invariants

1. Translate every Chinese-only text object; preserve an existing equivalent English object and remove only its paired Chinese.
2. Never modify unchanged English, geometry, dimensions, tables, or title-block structure.
3. Assign every changed text object exactly one source-derived allowed region before mutating any text.
4. Allowed regions for changed objects must not overlap hard keep-outs or each other in the same local row or panel.
5. Fit in this order: preserve source height, wrap, compress width, reduce height. Move only inside the assigned region.
6. Do not reduce below 55% of source height. A candidate that cannot fit fails for review instead of triggering global beautification.
7. Publish only when CJK residue, printable-frame overflow, missing layout coverage, and severe new text overlap are all zero.

## Architecture

`LayoutV2Planner` creates immutable `LayoutV2Decision` records from the pre-import topology. It classifies each changed object as table cell, narrative panel, or fixed label, derives one allowed rectangle, subtracts the nearest vertically relevant hard keep-out, and partitions peer rectangles before any AutoCAD entity is changed.

`LayoutV2Optimizer` applies decisions exactly once. Table cells and fixed labels use the existing low-level fit executors. Narrative text is forced through an explicit MText width equal to its planned rectangle, preventing geometric bounds and MText wrapping width from disagreeing.

The importer performs one audit after the v2 fit. It does not run `GlobalCollisionCorrection` for v2. Unresolved high risks produce a review candidate and a hard failure.

## Reuse boundaries

- Reuse: manifests, protected tokens, bilingual policies, topology capture, geometry primitives, font/height preservation, language gates, audit models.
- Replace in the active path: `LayoutOptimizer.Optimize` orchestration and repeated global collision correction.
- Retain v1 source files on the branch for comparison and rollback, but do not call them from the v2 import path.

## Test strategy

- Unit tests prove one decision per changed record, mutually exclusive peer regions, right-side table keep-outs, printable-frame containment, source-height-first fitting, and explicit narrative MText width.
- Existing C# and Python suites must remain green.
- A package integrity test must prove bundled DLLs match the release build.
- Real DWG validation is deliberately separate: the branch produces a candidate for user comparison against v1 without replacing the installed skill.

## Non-goals for the first v2

- No automatic visual beautification.
- No arbitrary text movement into distant empty space.
- No support expansion beyond the current AutoCAD 2025 DWG path.
- No attempt to solve XREF, proxy-object, native-table, or MLeader editing automatically.
