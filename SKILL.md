---
name: translate-cad-files
description: Use when translating text in AutoCAD 2027 DWG drawings, including single-language replacement and bilingual preserve-and-add output for technical notes, labels, title blocks, tables, and diagrams.
---

# Translate CAD Files

Use the bundled runner and never overwrite the source. Select one output pipeline from the user's request before export. Do not switch pipelines or reuse another pipeline's gate artifacts.

## Select the pipeline

- Use `replace` by default for single-language output: Chinese-to-English, English-to-Chinese, or another registered direction. It replaces source-language text and uses the stable replace layout and replace gates.
- Use `bilingual` only when the user requests bilingual output. It preserves source text, skips valid existing translations, adds missing target text beside or below the source, and uses layout V2 plus bilingual gates.
- Legacy `english` job metadata maps to `replace`; never write `english` for a new job.

Only `zh-CN -> en` is currently verified end to end. Do not advertise another direction as verified without its registered language policy and fixture.

## Workflow

1. Run `scripts/run.ps1 doctor --source <drawing>` unless AutoCAD 2027 was verified in this task.
2. Run `scripts/run.ps1 export --source <drawing> --job <new-job-dir> --output-mode <replace|bilingual>`.
3. Run `scripts/run.ps1 prepare-translations --job <job-dir> --max-source-chars 6000`.
4. Process one worklist part at a time. Preserve protected markers exactly. In `replace`, write target text only. In `bilingual`, retain source text and add the target text unless an equivalent target already exists.
5. Run `assemble-translations`, then import. Import runs only the selected pipeline's translation, composition, layout, and final gates.
6. Run `scripts/run.ps1 audit-summary --job <job-dir>`. Reject printable-frame overflow, missing layout coverage, wrong language outcome, duplicate bilingual companions, topology failure, or AutoCAD audit failure.
7. If visual review is required, inspect only changed or high-risk regions using `references/visual-audit.md`.
8. Deliver only `results/candidate.dwg` or `.dxf` after the selected pipeline passes.

Never open or print complete manifests, translations, detailed layout audits, or logical-flow reports.

## Independent gates

`replace` owns its export, translation, composition, residue, layout, topology, overlap, fit, and AutoCAD audit decisions. Its layout artifact is `replace-layout-audit.json`.

`bilingual` owns separate source-retention, target-presence, existing-pair, duplicate, composition, layout, topology, overlap, fit, and AutoCAD audit decisions. Its layout artifact is `bilingual-layout-audit.json`.

Even when two rules currently match, do not merge their gate implementations.

## Translation and layout rules

Use the user glossary first, then `references/cement-industry-glossary.md`, then established engineering usage. Match the longest complete Chinese term. Preserve models, standards, quantities, formatting controls, and protected markers. Technical codes, units, and font names do not count as an existing translation.

For bilingual layout V2, assign one source-derived allowed region to each changed label or narrative group, then perform one fit and one audit. Preserve font, alignment, hierarchy, and practical readability. Fit in this order: wrap, compress width, then reduce height. Tables, diagrams, dimensions, title blocks, neighboring regions, unchanged content, and the innermost printable frame are hard keep-outs.

DWG is the verified format. DXF, MLeader, native Table, XREF, and proxy objects require manual review.
