# Source/candidate visual review

Automatic success is not visual acceptance. Each mode owns its receipt: `replace-visual-review.json` or `bilingual-visual-review.json`. Never reuse another mode's review.

Render matching source/candidate overviews, then detail windows for dense tables, title blocks, new bilingual labels and changed prose. Read compact layout reports to select windows; inspect both images yourself. Overview alone cannot establish small-text legibility.

```powershell
python scripts/render_review.py --drawing "source.dwg" --output "jobs/example/artifacts/source-overview.png"
python scripts/render_review.py --drawing "jobs/example/results/candidate.dwg" --output "jobs/example/artifacts/candidate-overview.png"
# For a detail append --window left bottom right top; use identical bounds for both images.
```

The renderer works on a disposable copy. Do not render directly into the original DWG or send localized QUIT/save responses. Choose fresh image names; existing images are not overwritten.

Replacement acceptance: accurate target-only content, readable fitting, no new overlap or crossing a cell/diagram boundary. Bilingual acceptance: unchanged readable source, translation clearly associated with its source, no new overlap, no repeated translation, unchanged non-text content. Check rotated/nested block instances individually when not covered by the overview. If any defect exists, record `failed` and create a fresh corrected job; never mark an unresolved drawing delivered.

After actual inspection, write the selected receipt under that job's `artifacts`:

```json
{
  "status": "passed",
  "sourceSha256": "exact sourceSha256 from config/export-job.json",
  "candidateSha256": "exact hash from the selected mode's final receipt",
  "images": ["source-overview.png", "candidate-overview.png", "source-title.png", "candidate-title.png"],
  "notes": "Describe inspected regions and findings."
}
```

`images` are real paths relative to artifacts. Include source and candidate evidence. Re-run `audit-summary`; only `deliveryReady=true` is deliverable. Any changed candidate hash invalidates the previous review.
