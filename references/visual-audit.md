# Source/candidate visual review

Automatic checks do not establish visual acceptance. Review the actual final candidate yourself and write only its mode-specific, hash-bound receipt: `replace-visual-review.json` or `bilingual-visual-review.json`.

## Render readable evidence

Select representative regions from compact layout reports and render matching source/candidate overviews plus readable details in one batch. Include dense tables, title blocks, new bilingual labels and changed prose. Use actual placed-world bounds, not block-definition coordinates; check rotated/nested instances individually when not covered.

```powershell
# Replacement: replace example coordinates with actual world extents; repeat --window as needed.
python scripts/cad_translate.py diagnose --job "jobs/example" --window 618360 -22339 634212 -13500 --render
# Bilingual: replace handles with selected source handles; includes their placed instances.
python scripts/render_review.py --job "jobs/example" --handles "AB,CD"
```

The renderer uses disposable drawings, world-plan/native bounds and readable text display. It batches each drawing's windows in one CAD session without approving images or saving changes to the source. `review-render-plan.json` (bilingual) or `diagnostic-summary.json` (diagnosis) links windows to returned image paths. Replacement may include a saved pre-composition view to locate defects; that intermediate is not delivery evidence.

Keep a full-extents overview for scope, but use details when outlying objects make the sheet tiny. Empty crops, unreadable thumbnails and cut-off content do not count: correct the window, including new bounds after local correction. For bilingual tables and prose placed outside a frame, inspect the full source/target association and clear same-side placement. A DWG may pass with a disclosed printing warning: the existing print window or viewport may omit the supplement. Do not claim printability or change plot settings. A requested plotted PDF/fixed print layout still requires actual inclusion; use explicit layout review for paper-space content.

Reuse images only when drawing hash, window, view mode, renderer identity and image digest still match within the same test. Use current returned paths; an old image's existence does not prove freshness. Batch rendering resolves the current final `candidatePath` after corrections.

## Decide usability

Replacement requires accurate target-only content, full meaning and readable fitting with correct cell/diagram association. Bilingual requires unchanged readable source, complete clearly associated translations, no misleading duplication and intact non-text content. Compare technical qualifiers, company names, numbers/units and shortened or small labels against the source.

For bilingual groups, compare `bilingual-table-layout.json` with the complete visible source/target structure. Apply the shared existing-bilingual skip rule to labels, paragraphs and tables before judging missing additions. A bilingual title-field template is skipped whole even when a filled-in value remains source-only (the user-approved 锥斗结构图 example); no duplicate table or value supplement is required. Confirm that a skipped group remains unchanged and is explicitly accounted for. For a table that still requires translation, require a whole target-only copy, including merged cells, numeric fields and every body row. For tall tables inspect the immediately adjacent interior/exterior left/right choices; for wide tables inspect the immediately adjacent interior/exterior above/below choices. A clear placement far from the source is not an acceptable substitute for a blocked adjacent choice. An inline suffix, copied header without its body or unframed fallback list does not satisfy the complete-table rule. Single-column signature/equipment frames must not be mistaken for tables or silently lose translations. Check rotated signature fields at readable scale: each translation must stay within its own cell, using horizontal text if needed, without crossing adjacent field separators. Prose must retain the source block's width and reading order, grow vertically as needed and sit to its left/right. Check the full target extent, including outside-frame additions, for readability, text overlap and clear source association. Keep short legends in aligned same-row columns. A recorded strategy or zero unresolved count does not prove correct classification.

Use `passed` when usable without outstanding defects, `passed_with_warnings` for localized cosmetic imperfections, and `failed` for material defects. A readable unnecessary wrap or slight alignment difference may be a warning. In bilingual mode, crossing linework or a frame edge is allowed and is not itself a failure; text-to-text overlap, missing/misleading translations, changed numbers/units, unreadable glyphs, wrong associations and source/geometry damage remain blocking. Replacement-mode obstruction checks are unchanged. Consider widespread minor defects together; stop cosmetic iteration once usable, without inventing a numerical quality score.

## Record and finish

After inspecting the images, write the selected receipt under the job's `artifacts`:

```json
{
  "status": "passed",
  "sourceSha256": "exact sourceSha256 from config/export-job.json",
  "candidateSha256": "exact hash from this mode's final receipt",
  "images": ["source-overview.png", "candidate-overview.png", "source-detail.png", "candidate-detail.png"],
  "notes": "Actual inspected regions and findings, including group and plot association when relevant.",
  "warnings": [],
  "blockingIssues": []
}
```

Replace example values with actual bindings and paths relative to `artifacts`, including both source and candidate evidence. `passed_with_warnings` needs nonempty warnings; `failed` needs concrete blocking issues. Use `layoutReviewRecordIds` and `segmentOverflowCount` to select relevant regions, not as automatic pass/fail thresholds. No separate per-record approval or layout receipt is required.

Re-run `audit-summary`. Deliver only with `deliveryReady=true`; disclose returned warnings for `ready_with_warnings`. A failed candidate may be shared only as a labeled review draft. A changed candidate hash invalidates the old visual receipt; another mode's receipt cannot clear this mode's failures.
