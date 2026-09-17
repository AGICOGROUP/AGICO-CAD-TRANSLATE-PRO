# Bilingual pipeline

Preserve original text, properties and geometry; add target-only MText. Export model space, every layout and recursively placed block definitions. Leave dormant block-library definitions unchanged and record their exclusion in `bilingual-scope.json`.

```powershell
python scripts/cad_translate.py export --source "source.dwg" --job "jobs/bilingual-001" --output-mode bilingual --source-language zh-CN --target-language en
python scripts/cad_translate.py prepare-translations --job "jobs/bilingual-001"
# Review current-drawing inline candidates before confirming complete pairs:
# python scripts/cad_translate.py prepare-translations --job "jobs/bilingual-001" --existing-inline-handles "AB,CD"
# Consult references/cement-industry-glossary.md for current worklist terms (SKILL.md).
# Write target-only translatedText to exchange/translated-batches/*.jsonl; review term consistency before assembly.
python scripts/cad_translate.py assemble-translations --job "jobs/bilingual-001" --translated "jobs/bilingual-001/exchange/translated-batches"
python scripts/cad_translate.py import --job "jobs/bilingual-001" --translations "jobs/bilingual-001/exchange/translations.output.jsonl"
python scripts/cad_translate.py audit-summary --job "jobs/bilingual-001"
```

For Chinese→English, `bilingual-inline-candidates.jsonl` lists possible existing pairs. Review visible wording across formatting and confirm only complete equivalents using current handles. Model codes, units and incomplete English are not translations. Confirmed pairs stay in preservation checks but leave the translation worklist; this reuse is bound to the current source/entity hashes. Do not fuzzy-match equipment qualifiers, codes or quantities. For other directions, set explicit language flags; Chinese additions need a Chinese-capable font.

Rows with `termMemberIds` combine adjacent character entities into one complete term: translate the whole term, not syllables per entity. `bilingual-term-groups.json` binds the grouping; assembly retains all members and import creates one shared target. Review meaning even when a record is approved.

## Choose the right group

Recognize complete tables in a common parent coordinate system after projecting nested block linework. Header and body belong to the same table; differing row heights, styles or block ownership do not justify separating them. Split an adjoining title/signature panel only at actual cell boundaries with title-field evidence, never through a merged cell. A lone label surrounded by unrelated lines is not a schedule.

Use these distinct layouts:

- **Tables:** measure readable same-row suffix space after each original inside its actual cell. If missing translations fit, use `table-inline-right`. Otherwise keep the original and make a full-size adjacent translated copy, including the complete grid, merged cells, all row/column dimensions, numeric values, codes and line properties. Never accept a header-only copy with the body reclassified as notes.
- **Title/signature fields:** pair the target inside its own cell; if it cannot fit readably, use clearly associated local whitespace. Do not collect title fields into a detached schedule.
- **Short legends and code definitions:** retain row-by-row association and align the translations in a neat same-row column beside the originals. They are not prose to move into a detached note panel.
- **Prose and substantial notes:** reserve one complete translation panel before ordinary labels. Preserve numbering, reading order and related numeric lines; use a common left edge, readable height and consistent spacing. Prefer nearby continuous whitespace inside the frame. Use outside-frame space only when needed and after the visibility/association review below. Do not merge unrelated columns across table/process boundaries.

Place a copied table right, left, above or below the original, preferring nearby in-frame space. If the frame or crowded content prevents a complete adjacent copy, use the nearest clear space wholly beyond the containing frame on the same side, aligned with the source. Whole prose panels may use this frame-edge placement too. Keep the complete grid at original scale; do not scatter rows or cross into another drawing. An explicit user requirement to keep all additions inside the frame overrides this fallback.

The native path supports unrotated Line grids with DBText/MText, unequal rows and merged cells. Pure nested Line-grid blocks can be cloned as a complete grid after their transforms are resolved. Mixed-content blocks are not supported for whole-block grid copying: preserve them, report the limitation and review locally rather than duplicating engineering objects or claiming full support. Native AutoCAD Table and other unsupported structures likewise require specific coverage. Invalid boundaries must not be reconstructed by joining surviving vertices.

Inspect `bilingual-table-layout.json` for group membership, chosen strategy, destination and rejection reasons. Check actual source/candidate pairs: a recorded `note-block`, successful reservation or low unresolved count is not proof of correct classification or full-table coverage. Repair a misplaced group together, without retranslating valid content solely for layout.

## Fit and associate

Ordinary labels stay clearly associated with their source. Test measured unwrapped width before wrapping; do not inherit a narrow Chinese box as the final English width. Prefer nearby readable wrapping over a remote single line, and review reduced-height text at readable scale. Position/scale search thresholds belong to the placer, not manual per-drawing tuning.

Check target bounds against original and newly added text, cell/frame boundaries and engineering geometry in the final placed view. Project obstacles through every direct/nested block-instance transform and keep model/paper roots separate. Rotated/shared blocks require review of their actual instances; local-definition clearance is not world-space clearance. Every fallback uses the same containment and collision checks.

For any table or panel outside a frame, inspect the source and target together and confirm complete, readable content in clear same-side space. A panel must not straddle a frame or obscure another sheet. Preserve original geometry and plot settings. Frame-external supplements are allowed in the DWG; disclose that the existing print window or viewport may exclude them and must include the supplement before printing. This printing limitation alone is a warning, not failed translation. If the requested deliverable is a plotted PDF or an explicitly fixed print layout, verify inclusion in that actual deliverable instead.

## Evidence and repair

Use compact reports and selected records rather than dumping detailed audits:

- `bilingual-pairs.json`: source/target handles, reuse decisions and bounds. Shared term members reference one target. New targets carry `AGICO_CAD_BILINGUAL` XData; reuse still checks text and source hashes. A second pass over a translated input must add no duplicates.
- `bilingual-native-check.json`, `bilingual-structure.json`, `bilingual-language.json`, `bilingual-final.json`: reopened-candidate source preservation, structure, content and final binding. This branch does not use replacement residue/composition gates.
- `bilingual-layout-audit.json` and `bilingual-unresolved.json`: overlap, small text, distant association and placement failures, with bounded rejection examples. These point to visual checks; distance alone is not rejection, and table/panel associations are judged as groups.
- `bilingual-review-windows.json`: placed-world detail windows. Use these and current target bounds to select readable source/candidate evidence.
- `bilingual-object-access.json` and `command-diagnostic.json`: bounded access failures, stage/context and loaded DLL identity. Check these before blaming proxies/fonts or changing the placer.

For saved layout defects, follow [same-test recovery and correction](recovery-workflow.md), then [visual review](visual-audit.md) with `bilingual-visual-review.json`. Missing additions, altered originals, misleading pairings and serious obstruction cannot be cosmetic warnings. Unsupported or unresolved coverage must remain explicit.
