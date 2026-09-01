---
name: translate-cad-files
description: Translate AutoCAD 2025 DWG text as single-language replacement or bilingual preserve-and-add output.
---

# Translate CAD Files

Never overwrite the source. Select one pipeline before export. Never switch pipelines or reuse the other's artifacts.

## Select the pipeline

- Use `replace` by default for single-language output. It replaces source text and uses stable replace layout and gates.
- Use `bilingual` only when requested. It preserves source text, skips valid translations, adds missing target text nearby, and uses layout V2 plus bilingual gates.
- Legacy `english` job metadata maps to `replace`; never write `english` for a new job.

Only `zh-CN -> en` is verified. Other directions require a registered policy and fixture.

## Workflow

1. Run `scripts/run.ps1 doctor --source <drawing>` unless AutoCAD 2025 was verified in this task.
2. Run `scripts/run.ps1 export --source <drawing> --job <new-job-dir> --output-mode <replace|bilingual>`.
3. Run `scripts/run.ps1 prepare-translations --job <job-dir> --max-source-chars 6000`.
4. Process one worklist part at a time. Preserve markers. In `replace`, write target text only. In `bilingual`, retain source text and add target text unless equivalent target text exists.
5. Run `assemble-translations`, then import. Import performs the pre-import language gate internally, writes `artifacts/postcomposition-language-check.json`, and runs only the selected pipeline.
6. Run `scripts/run.ps1 audit-summary --job <job-dir>`. Treat any printable-frame overflow as a hard failure. Also reject missing coverage, wrong language, duplicates, topology failure, or AutoCAD audit failure.
7. Review only changed, composed, or high-risk regions using `references/visual-audit.md`.
8. Deliver `results/candidate.dwg` or `.dxf` only after all selected gates pass.

Never open or print the complete manifests, translations, detailed layout audits, or logical-flow reports.

## Independent gates

`replace` owns its export, translation, composition, residue, layout, topology, overlap, fit, and AutoCAD audit decisions. Its layout artifact is `replace-layout-audit.json`.

`bilingual` owns separate source-retention, target-presence, existing-pair, duplicate, composition, layout, topology, overlap, fit, and AutoCAD audit decisions. Its layout artifact is `bilingual-layout-audit.json`.

Even when two rules currently match, do not merge their gate implementations.

## Translation and layout rules

Use the user-supplied project glossary first, then `references/cement-industry-glossary.md`, then engineering usage. Match the longest complete Chinese term. Preserve models, standards, quantities, formatting, and markers; insert an ASCII word boundary when required. Codes, units, and font names are not translations.

For bilingual layout V2, assign one source-derived allowed region to each changed label or narrative group, then perform one fit and one audit. Reject the candidate if any changed text crosses it. Preserve font, alignment, hierarchy, and readability. Fit in this order: wrap, compress width, then reduce height. Tables, diagrams, dimensions, title blocks, neighbors, unchanged content, and the printable frame are hard keep-outs.

DWG is the verified format. DXF, MLeader, native Table, XREF, and proxy objects require manual review.
