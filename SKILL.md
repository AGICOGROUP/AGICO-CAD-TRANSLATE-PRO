---
name: translate-cad-files
description: Translate Chinese text in AutoCAD DWG or DXF drawings with professional terminology, complete coverage, preserved tables and diagrams, dense-note layout, and source-relative fidelity.
---

# Translate CAD Files

Use the bundled AutoCAD runner. Never overwrite the source.

## Context budget

Never open or print the complete manifest, translation output, `layout-audit.json`, or `logical-flow-report.json`. Use bounded batches and `audit-summary.json`; full diagnostics remain on disk.

## Workflow

1. Run `powershell -ExecutionPolicy Bypass -File scripts/run.ps1 doctor --source <drawing>` unless AutoCAD 2025 was verified in this task.
2. Run `scripts/run.ps1 export --source <drawing> --job <new-job-dir>` to create an isolated, sealed job.
3. Run `scripts/run.ps1 prepare-translations --job <job-dir> --max-source-chars 6000`. Read one `exchange/translation-worklist/part-*.jsonl` at a time; non-Chinese records stay out of context.
4. Translate each batch to a matching file in `exchange/translated-batches/` with only `recordId` and `translatedText`. For cement-process drawings, use `references/cement-industry-glossary.md` only for current terms. Preserve protected markers exactly and in order. Put no commentary in JSONL.
5. Run `scripts/run.ps1 assemble-translations --job <job-dir> --translated <job-dir>/exchange/translated-batches`. It restores fixed fields, fills non-Chinese records locally, validates coverage and markers, and writes `exchange/translations.output.jsonl`.
6. Run `scripts/run.ps1 import --job <job-dir> --translations <job-dir>/exchange/translations.output.jsonl`. Import performs the pre-import language gate internally; do not run `check-translations` separately. It imports once, composes eligible dense prose, re-exports once, and publishes only after `artifacts/postcomposition-language-check.json` has zero CJK in `plainText` and `rawText`. Each terminal result ends that Core Console stage; never wait for or close AutoCAD manually.
7. Run `scripts/run.ps1 audit-summary --job <job-dir>`. Require zero residue and segment overflow. Treat any printable-frame overflow as a hard failure. If `requiresVisualReview` is true, follow `references/visual-audit.md` only for changed, composed, or high-risk areas.
8. Run `scripts/run.ps1 status --job <job-dir>`; deliver only `results/candidate.dwg` or `.dxf`.

Do not retry export or import automatically.

## Terminology contract

Precedence: user-supplied project glossary, bundled cement glossary, established engineering usage. Match the longest complete Chinese term first and use one equivalent consistently. Preserve models, tags, standards, quantities, and markers. In narrow title-block cells, shorten only if the full term cannot fit one line: omit a head term already supplied by adjacent unchanged English; use standard abbreviations such as `PROC.` or `PROP.` last. Insert an ASCII word boundary between restored alphanumeric markers and following English. Do not load the cement glossary for unrelated drawings.

## Layout contract

Preserve divisions, titles, tables, diagrams, dimensions, title blocks, and paragraph regions. Compose only high-confidence prose in source-derived note columns; keep short labels, table cells, dimensions, attributes, symbols, and captions separate.

If one object contains equivalent English, remove its Chinese and preserve the English. For separate objects, pair Chinese and English one-to-one from the repeated local layout: same-row left/right or stacked top/bottom, nearby, and similar in height; different colors or layers do not break a pair. Use clear equivalent pairs to learn the local offset so nonliteral pairs such as `燃料` beside `Petroleum Coke` are also recognized. Remove only Chinese. Recognize common title-block pairs: approve, check, design, project, item, title, and stage. Preserve the English object's text, font, height, width factor, alignment, and position. Codes such as `C30` and `HRB400` are not translations.

Preserve source height and the title/subtitle/body/annotation hierarchy. Repeated equivalent diagram labels keep their shared source height. Tables, diagrams, dimensions, title blocks, unchanged English, and neighboring columns are hard keep-outs. The innermost printable sheet frame, not outer block extents, is a hard boundary. Reject the candidate if any changed text crosses it. For fixed/table text: keep source height; move only inside its source-derived slot; then compress width; reduce height only if still impossible. Padding must never exclude source text. Do not go below 55% source height or globally beautify/realign.

Read `references/exchange-format.md` only for batch, marker, or contract failures. DXF is supported but sample verification is pending. MLeader, native Table, XREF, and proxy objects require manual review.
