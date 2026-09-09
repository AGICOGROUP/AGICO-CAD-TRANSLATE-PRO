# Replacement pipeline

Replace original text with the requested target language. Preserve geometry, codes, units, layer/style hierarchy and unrelated content. Fit changed text within its original cell or allowed region; full translations should fit by wrapping and width adjustment before shrinking.

```powershell
python scripts/cad_translate.py export --source "source.dwg" --job "jobs/replace-001" --output-mode replace --source-language zh-CN --target-language en
python scripts/cad_translate.py prepare-translations --job "jobs/replace-001"
# Translate each worklist part to exchange/translated-batches/part-0001.jsonl, etc.
python scripts/cad_translate.py assemble-translations --job "jobs/replace-001" --translated "jobs/replace-001/exchange/translated-batches"
python scripts/cad_translate.py import --job "jobs/replace-001" --translations "jobs/replace-001/exchange/translations.output.jsonl"
python scripts/cad_translate.py audit-summary --job "jobs/replace-001"
```

For English→Chinese use `--source-language en --target-language zh-CN`. Translation JSONL is target-only in both directions. IDs absent from the deduplicated worklist are restored by assembly; never fabricate them.

The native replacement pipeline captures topology once, applies the replacement fitter, verifies saved content, runs eligible English prose composition, reopens the output and checks non-text structure. Chinese output retains the verified per-entity fitting and never uses legacy English-preferring composition. Final text is extracted within the same CAD process. Python applies replacement-only language and overflow gates; there is no separate unconditional compose/export process.

Relevant artifacts: `replace-layout-audit.json`, `logical-flow-report.json`, `replace-structure.json`, `replace-native-check.json`, `replace-language.json`, `replace-final.json`. Unresolved content or layout errors fail publication. A hash-bound `replace-visual-review.json` is needed to finish delivery.

## Fast, complete translation

Replacement grouping ignores presentation-only differences (font, height, width and alignment), while retaining exact source, protected values, object type, role, layer and attribute tag. CAD still fits every entity separately. Translate the full term once; inspect the included `protectedTokens` and `contextVariants`. Bilingual grouping is unchanged.

Fix all rows in `artifacts/translation-marker-repairs.jsonl` together when assembly reports marker errors. Pre-CAD assembly failures may be corrected in the same job: export data is unchanged and no candidate exists. Preserve completed good batches. A native import failure still uses a fresh job, with prior translations transferred by matching source text, protected values and context rather than blindly copying old IDs. Do not regenerate good translations.

Read `replace-semantic-review.json` before import. It flags possible loss of company names, storage equipment, quicklime qualifiers and unexplained acronyms. Resolve against the source and glossary; these heuristics are advisory and cannot establish complete translation accuracy. Do not use automatic layout success as semantic approval.

Preserve existing text anchors, rotation, layer, color and style as far as target text permits. Use the complete approved translation as the content authority through fitting and composition. Model codes (such as MDPX150), capacities, units and font-control names do not establish that a Chinese label is already bilingual. If post-composition text has lost words, restore the full approved text with its protected formatting to every affected occurrence, using the layout audit's handle mapping; do not reconstruct it by removing Chinese from the source or accept a surviving model number as a translation.

For a local overlap or unsuitable wrap, keep the rest of the drawing unchanged and adjust the affected text in a candidate copy. First widen the text box toward actual nearby whitespace while retaining its anchor. Keep one line when it fits; otherwise allow controlled wrapping. If that still crosses equipment, neighboring text or a frame, move the complete label or note a short distance above, below or beside its source location within the same view, keeping its association clear. Confine table/title-block text to its cell. Preserve rotation, layer, color and style; reduce height only after width, wrapping and a nearby safe placement have been considered. Never use drawing-specific coordinates as a general placement rule or move geometry to make room. `replace-readability-review.json` selects labels reduced below 65% of source height for close inspection; inspect these labels at readable scale rather than assuming failure or success from the ratio.

Batch local corrections into one native edit/save pass, preserving the original DWG version. Reuse the approved translations; do not rerun translation or redevelop the plugin for routine fitting. Inspect the final post-composition text, not just the pre-composition layout audit. Verify corrected content against the full restored translations and confirm unchanged non-text/table structure; when composition merged entities, follow its reported membership instead of assuming one old handle per output. Re-render corrected regions plus a final overview, then bind the replacement final/visual receipts to that exact verified file hash. Never just update receipt hashes to bypass a failed check. Resolve semantic and readability findings before delivery; do not shorten technical meaning to clear a collision.
