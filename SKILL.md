---
name: translate-cad-files
description: Use when translating Chinese text to English in AutoCAD 2025 DWG drawings, or DXF drawings that can be manually verified, including technical notes, labels, title blocks, tables, diagrams, and bilingual cleanup.
---

# Translate CAD Files

Use the bundled runner. Never overwrite the source. This v2 release uses one source-derived allowed region for each changed label or narrative group, followed by one fit and one audit.

## Workflow

1. Run `scripts/run.ps1 doctor --source <drawing>` unless AutoCAD 2025 was verified in this task.
2. Run `scripts/run.ps1 export --source <drawing> --job <new-job-dir>`.
3. Run `scripts/run.ps1 prepare-translations --job <job-dir> --max-source-chars 6000`.
4. Read one `exchange/translation-worklist/part-*.jsonl` at a time. Write matching files under `exchange/translated-batches/` containing only `recordId` and `translatedText`. Preserve protected markers exactly.
5. Run `scripts/run.ps1 assemble-translations --job <job-dir> --translated <job-dir>/exchange/translated-batches`.
6. Run `scripts/run.ps1 import --job <job-dir> --translations <job-dir>/exchange/translations.output.jsonl`. Import performs the pre-import language gate internally and writes `artifacts/postcomposition-language-check.json`. Do not retry automatically or close AutoCAD manually.
7. Run `scripts/run.ps1 audit-summary --job <job-dir>`. Treat any printable-frame overflow as a hard failure. If `requiresVisualReview` is true, use `references/visual-audit.md` only for changed, composed, or high-risk areas.
8. Run `scripts/run.ps1 status --job <job-dir>` and deliver only `results/candidate.dwg` or `.dxf`.

Never open or print the complete manifest, translation output, `layout-audit.json`, or `logical-flow-report.json`.

## Translation contract

Use terminology in this order: user-supplied project glossary, `references/cement-industry-glossary.md` for cement drawings, then established engineering usage. Match the longest complete Chinese term. Preserve models, standards, quantities, and markers; insert an ASCII word boundary after restored alphanumeric markers when needed.

If equivalent English already exists in the same object or a nearby same-row/stacked pair, preserve that English and remove only Chinese. Color and layer differences do not break a pair. Do not treat technical codes as translations.

## Layout v2 contract

Classify changed text once as table cell, narrative panel, or fixed label. Plan its allowed region from source geometry before changing text. Tables, diagrams, dimensions, title blocks, unchanged English, neighboring regions, and the innermost printable frame are hard keep-outs.

Preserve font, alignment, source height, and title/body/annotation hierarchy. Fit in this order: wrap, compress width, then reduce height. Move only inside the planned region; never reduce below 55% source height. Reject the candidate if any changed text crosses it, enters a hard keep-out, has an unresolved severe new overlap, or lacks a layout decision. Do not globally beautify or run iterative collision correction.

Chinese-to-English DWG is verified. DXF requires manual verification. MLeader, native Table, XREF, and proxy objects require review. Runtime DLL hashes must match the bundled `src/cad` Release build.
