# Source/candidate visual review

Automatic success is not visual acceptance. Each mode owns its receipt: `replace-visual-review.json` or `bilingual-visual-review.json`. Never reuse another mode's review. For bilingual tables, inspect `bilingual-table-layout.json`: verify that multi-column schedules used a full-size target-language copy when space allowed, with all outer/grid borders present, original row/column geometry retained and readable target text. Check recorded fallback reasons against the actual drawing; source retention alone does not establish acceptable table layout.

Render matching source/candidate overviews, then detail windows for dense tables, title blocks, new bilingual labels and changed prose. Read compact layout reports to select windows; inspect both images yourself. Overview alone cannot establish small-text legibility.

Use world-space extents from actual placed instances for detail windows; block-definition local coordinates are not drawing coordinates. Verify the intended region is present and text is readable before counting an image as evidence. Empty crops, tiny whole-sheet thumbnails and cut-off titles cannot support a passed review. Correct the window rather than writing a passing receipt. Compare company names, equipment/material qualifiers and any shortened or reduced-height labels against the source. Include the final post-composition drawing, not an intermediate layout candidate.

```powershell
python scripts/render_review.py --drawing "source.dwg" --output "jobs/example/artifacts/source-overview.png"
python scripts/render_review.py --drawing "jobs/example/results/candidate.dwg" --output "jobs/example/artifacts/candidate-overview.png"
# For a detail append --window left bottom right top; use identical bounds for both images.
# Bilingual: batch matching overviews and all placed instances of selected source handles.
python scripts/render_review.py --job "jobs/example" --handles "AB,CD"
```

The renderer works on a disposable copy, sets a world-plan view and disables quick-text boxes before zooming to native world bounds. Batch mode reads `bilingual-review-windows.json`, accepts native model-space name casing, keeps each selected block instance and renders all windows with one CAD session per drawing (two sessions for a source/candidate pair). It writes image associations to `review-render-plan.json`; it does not approve the images. Select representative handles from compact reports in one batch. If distant source objects shrink the full-extents overview, keep that overview for scope and use readable world-coordinate detail windows for the main sheet and outlying translated content; do not treat empty paper-space output as successful review. Use explicit layout review for genuine paper-space windows. Do not render directly into the original DWG or send localized QUIT/save responses. Choose fresh image names; existing images are not overwritten.

Replacement acceptance: accurate target-only content and readable fitting without material obstruction or ambiguous cell/diagram association. Bilingual acceptance: unchanged readable source, complete and clearly associated translations, no misleading duplication and unchanged non-text content. Short labels should remain single-line when space permits; an unnecessary but readable wrap is cosmetic, not by itself a failed drawing. Check rotated/nested block instances individually when not covered by the overview.

Use `passed` for usable output without outstanding defects, `passed_with_warnings` for usable output with localized cosmetic imperfections, and `failed` for material defects. List minor imperfections in `warnings` and material defects in `blockingIssues`. Never downgrade missing/misleading translations, wrong numbers/units, unreadable glyphs, serious text/dimension obstruction, wrong row/source association or source/geometry damage to cosmetic warnings. Assess overall usability as well as individual defects; do not claim a 9/10 score without a defined evaluation. Do not rebuild a usable drawing just for cosmetic perfection. If blocked, retain the best candidate and give the user concrete remaining issues, not only a generic failure message.

After actual inspection, write the selected receipt under that job's `artifacts`:

```json
{
  "status": "passed",
  "sourceSha256": "exact sourceSha256 from config/export-job.json",
  "candidateSha256": "exact hash from the selected mode's final receipt",
  "images": ["source-overview.png", "candidate-overview.png", "source-title.png", "candidate-title.png"],
  "notes": "Describe inspected regions and findings.",
  "warnings": [],
  "blockingIssues": []
}
```

For `passed_with_warnings`, supply a nonempty list of concise warning strings. Assess layout and readability together in this final visual review; no separate `layoutAssessment`, per-record approval list or overflow sign-off is required. Use `layoutReviewRecordIds` and `segmentOverflowCount` as pointers to relevant detail windows, grouping adjacent findings in one view. Judge actual engineering readability: a geometric contact may be an innocuous touch or a serious obstruction. Record the inspected regions and outcome in `notes`; if a material defect remains, use `failed` with `blockingIssues`.

`images` are real paths relative to artifacts. Include source and candidate evidence. Re-run `audit-summary`; `deliveryReady=true` with `deliveryStatus=ready_with_warnings` is valid delivery with the returned warnings disclosed. A failed candidate may be shared only as an explicitly labeled review draft. Any changed candidate hash invalidates the previous review. Both branches own their review logic; replacement reviews cannot clear bilingual missing additions or source changes.
