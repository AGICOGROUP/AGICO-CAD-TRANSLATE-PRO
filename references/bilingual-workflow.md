# Bilingual pipeline

Preserve original entities and add target-only MText nearby. Never rewrite the Chinese/English original to hold both languages. Existing equivalent inline text or a uniquely matched nearby translation is reused; codes such as HCQ2000 are not evidence of bilingual coverage.

```powershell
python scripts/cad_translate.py export --source "source.dwg" --job "jobs/bilingual-001" --output-mode bilingual --source-language zh-CN --target-language en
python scripts/cad_translate.py prepare-translations --job "jobs/bilingual-001"
# Return target-only translatedText in exchange/translated-batches/*.jsonl.
python scripts/cad_translate.py assemble-translations --job "jobs/bilingual-001" --translated "jobs/bilingual-001/exchange/translated-batches"
python scripts/cad_translate.py import --job "jobs/bilingual-001" --translations "jobs/bilingual-001/exchange/translations.output.jsonl"
python scripts/cad_translate.py audit-summary --job "jobs/bilingual-001"
```

English→Chinese uses `--source-language en --target-language zh-CN`; Spanish→Chinese uses `--source-language es --target-language zh-CN`. Chinese additions use a Chinese-capable font. Source fonts, contents, locations and properties must remain unchanged.

For independent multi-column equipment, material and parameter schedules, preserve the entire original table and create a full-size target-language copy in continuous whitespace inside the drawing frame. Try left, right, above, then below; allow a larger gap to clear adjoining title blocks. Copy row heights, column widths, cell order, line properties and unchanged codes/numbers. Translate only the copy's natural-language text, fitting it within its original cell; retain source-sized text where possible and wrap or reduce text height only when needed. Never shrink the whole table. Shared frame lines are copied only over the table's extent. Title blocks, company/signature panels and ordinary labels are not duplicated. Existing linked target tables are reused on a second pass.

The native copy path currently supports unrotated independent line-grid schedules with DBText/MText in model/paper space. Unsupported objects, nested definitions, incomplete grids or insufficient full-size whitespace fall back to cell-local bilingual placement and record the reason. Native AutoCAD Table objects require specific coverage/manual review. A fallback is not evidence that the preferred table-copy strategy ran: inspect `bilingual-table-layout.json` and the rendered table before acceptance.

For single-column lists with one translated source per row, retain the side-column strategy: shared left edge and font height, source row order, and vertical row alignment. Measure the complete column inside the frame before reserving it. Do not merge unrelated fields into one ambiguous translation row. Existing equivalent bilingual text is reused. Review the entire copied table or translation column, including its outer borders, long labels, preserved numeric fields and row associations.

Placement measures the target text's real unwrapped MText width at each candidate scale. If that single-line width fits a collision-free nearby region, try it before any wrapped width. If it cannot fit without crossing a cell/frame, process geometry, equipment or neighboring text, allow controlled wrapping, then width compression and height reduction. Wrapping is conditional, never globally disabled or applied merely because the source Chinese text box is narrow. Test below/above/right/left positions and preserve a readable height floor. Rotated text retains its plane and orientation, but non-XY planes and nested block instances require specific visual verification; a copied rotation is not proof of correct 3D placement. Never move original text to create room.

When model/paper space and inserted block definitions both contain text, project occupied bounds through every direct/nested block-instance transform before choosing a position. Update those projected bounds after every addition so text that is collision-free in separate local definitions cannot overlap in the final visible layout.

During review, specifically compare short labels such as flow directions, cabinet names and drawing titles. A short English label split across lines despite sufficient horizontal whitespace is a layout defect and requires a fresh corrected job. Long equipment names may wrap when the available region genuinely cannot hold the measured single-line form safely.

`bilingual-pairs.json` records source handle, target handle, added/reused decision and bounds. Saved output is reopened: original text/properties/geometry must match, associated target handles must exist, and non-text structure must be retained. A second pass over an already translated drawing must add zero duplicate translations.

Relevant artifacts: `bilingual-native-check.json`, `bilingual-structure.json`, `bilingual-layout-audit.json`, `bilingual-language.json`, `bilingual-final.json`. No replacement residue gate or prose composition is used. Unplaceable labels remain unchanged and are listed in `bilingual-unresolved.json`; the job fails rather than publishing an incomplete drawing. Request a layout decision only when readable free space cannot be found. Finish with a hash-bound `bilingual-visual-review.json`.
