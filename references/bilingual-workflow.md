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

For connected grid tables (including gridded title blocks), first plan a complete translation column in continuous whitespace beside the table. Use a shared left edge and font height, preserve source row order and vertically align each translation to its corresponding row; preserve existing row identifiers and reuse existing bilingual text. Measure the whole column before reserving it, try left then right, and keep it inside the enclosing drawing frame. Never apply this strategy to ordinary labels or isolated rectangular frames. Multiple translated cells in the same row, rotated text, insufficient whitespace or unsupported table objects fall back to the existing cell-local placement; do not merge unrelated cell translations into an ambiguous row. The native planner currently supports connected line-grid cells with one translated source per row. Native AutoCAD Table objects still require specific coverage/manual review. In review, inspect the complete side column and its row associations, not only individual words.

Placement measures the target text's real unwrapped MText width at each candidate scale. If that single-line width fits a collision-free nearby region, try it before any wrapped width. If it cannot fit without crossing a cell/frame, process geometry, equipment or neighboring text, allow controlled wrapping, then width compression and height reduction. Wrapping is conditional, never globally disabled or applied merely because the source Chinese text box is narrow. Test below/above/right/left positions and preserve a readable height floor. Rotated text retains its plane and orientation, but non-XY planes and nested block instances require specific visual verification; a copied rotation is not proof of correct 3D placement. Never move original text to create room.

When model/paper space and inserted block definitions both contain text, project occupied bounds through every direct/nested block-instance transform before choosing a position. Update those projected bounds after every addition so text that is collision-free in separate local definitions cannot overlap in the final visible layout.

During review, specifically compare short labels such as flow directions, cabinet names and drawing titles. A short English label split across lines despite sufficient horizontal whitespace is a layout defect and requires a fresh corrected job. Long equipment names may wrap when the available region genuinely cannot hold the measured single-line form safely.

`bilingual-pairs.json` records source handle, target handle, added/reused decision and bounds. Saved output is reopened: original text/properties/geometry must match, associated target handles must exist, and non-text structure must be retained. A second pass over an already translated drawing must add zero duplicate translations.

Relevant artifacts: `bilingual-native-check.json`, `bilingual-structure.json`, `bilingual-layout-audit.json`, `bilingual-language.json`, `bilingual-final.json`. No replacement residue gate or prose composition is used. Unplaceable labels remain unchanged and are listed in `bilingual-unresolved.json`; the job fails rather than publishing an incomplete drawing. Request a layout decision only when readable free space cannot be found. Finish with a hash-bound `bilingual-visual-review.json`.
