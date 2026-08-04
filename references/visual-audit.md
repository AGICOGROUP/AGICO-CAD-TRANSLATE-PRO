# Risk-scoped visual audit

Use this only when `audit-summary.json` sets `requiresVisualReview` to true. Read the compact `visual-review-targets.json`, not the full layout or logical-flow reports. Review changed, composed, or high-risk dense-note areas; do not render unchanged sheets individually.

Render identical source and candidate overviews with AutoCAD Core Console, then place them in one labeled source/candidate contact sheet. Use the target windows only to create detail crops for defects that cannot be judged in the overview; do not render every target separately.

For a model-space window, run `accoreconsole.exe /i "<drawing>" /s "<render.scr>" /l en-US` with this script:

```text
_.FILEDIA
0
_.CMDDIA
0
_.TILEMODE
1
_.REGENALL
_.ZOOM
_W
{left},{bottom}
{right},{top}
_.PNGOUT
"{absolute-output.png}"
_ALL

_.QUIT
```

Record only bounded results in `artifacts/large-note-panel-review.json`:

```json
{
  "schemaVersion":"1.0",
  "status":"passed",
  "reviewedPanelCount":2,
  "segmentOverflowCount":0,
  "keepOutIntrusionCount":0,
  "shortLabelCompositionCount":0,
  "newSevereOverlapObserved":false,
  "tablesAndDiagramsPreserved":true,
  "contactSheet":"source-candidate-contact-sheet.png"
}
```

Pass only when all selected columns are present, source-relative placement is preserved, tables and diagrams are intact, and prose does not enter a table, diagram, dimension block, title block, or neighboring column. Ordinary CAD contact is acceptable; any new keep-out intrusion fails.
