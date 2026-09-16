# Continue a CAD translation task

The AI handles translation, repairs and visual decisions. The user supplies the file, language and output mode; do not introduce manual CAD processing or routine approvals.

## Import and recovery

Use the selected mode's export → prepare → translate bounded parts → assemble → import workflow. Import runs the same Core text validator as native CAD before launching CAD. `text-validation.json` preserves per-record errors. Fix the reported records together; do not replace accurate full technical translations with shorter phrases merely to satisfy layout.

When a command fails or an interrupted task resumes, use the existing job:

```powershell
python scripts/cad_translate.py status --job "jobs/example"
python scripts/cad_translate.py resume --job "jobs/example"
```

`resume` checks the sealed source, manifest, mode, language and translation binding. A verified unchanged candidate is reused. A retained staged drawing is inspected without retranslation or placement. If no candidate exists, it imports the complete batch. It returns the next action; `review` means inspect the saved result, not rebuild it. Inspect/ correction attempts get separate configs/results, and the original drawing is never overwritten.

For an incomplete batch, recovery preserves valid rows, fills exact equivalent occurrences and returns a repair worklist. Translate only the reported missing/invalid units, then supply the compact repaired rows:

```powershell
python scripts/cad_translate.py resume --job "jobs/example" --translated "jobs/example/exchange/repaired.jsonl"
```

A changed source, language, mode or already-used translation batch requires a fresh job/import bound to that content; never edit hashes to reuse stale output. Preserve reviewed translations by matching source/protected values/context, and use `export --retry-from` to retain task timing. Older V2 bilingual candidates can be freshly inspected. Older composed replacement candidates lacking a content receipt may require one fresh import because their final content cannot be proven from the old per-entity mapping. A missing receipt is not permission to invent one. V1 jobs still require a V2 export.

## One batch of local layout corrections

For layout-only defects on an existing bilingual candidate, collect the actual defects found in one review and default to one batch of `correct` edits, followed by batched rendering of affected regions and an overview. Reuse unchanged evidence only within that same test; do not start another cosmetic iteration once the drawing is usable. Repeating bilingual `import` with unchanged bound translations now enters saved-candidate recovery: it reuses a verified candidate or reinspects a retained stage, without running whole-drawing placement again. Read `artifacts/import-recovery.json` for the actual action and review state; a successful command does not mean a reported visual defect was repaired. Keep grouped notes together in the correction batch. Keep full text and its meaning; try usable width, controlled wrapping, then a nearby position/height within the same cell or clearly associated whitespace. Choose coordinates from the actual native drawing, not the pixel preview. Model/block positions are in their owning definition's coordinates. Review every placed instance of a shared block.

```json
{
  "candidateSha256": "current candidate hash",
  "edits": [
    {"handle": "AB", "expectedText": "complete current raw MText contents", "width": 50, "height": 3, "x": 10, "y": 20}
  ]
}
```

```powershell
python scripts/cad_translate.py correct --job "jobs/example" --corrections "jobs/example/exchange/corrections.json"
python scripts/cad_translate.py audit-summary --job "jobs/example"
```

Every edit needs a target handle and exact current raw text. Only specify changed dimensions/coordinates; x/y must be supplied together. MText width is its boundary width, height is text height, x/y is its insertion point. DBText supports height and x/y at its alignment anchor; boundary width is unsupported. Bilingual correction only edits added target text, never originals or reused source labels. Shared term members remain one target. Unsupported object types are explicit failures, not silent omissions.

The command proves its input, applies one transaction, saves a new candidate and rechecks content/source/structure. Source and previous candidates remain intact. `local-correction.json` gives old/new measured bounds. Use the path from `audit-summary` or the mode's final `candidatePath`; the export config intentionally keeps its original output path.

Content rewriting and adding a missing untranslated object are outside this geometry-only command. Repair the approved translation batch or placement cause and run a fresh bound import for those cases; never mutate receipt hashes to accept a partial result. A correction does not certify visual clearance: inspect its affected regions plus an overview, and transform local bounds to the actual placed view when needed. Old automatic review windows may cover the former placement, so expand the source/candidate window to include the new bounds. Stop when the drawing is usable; cosmetic warnings alone do not justify another full run.

## Runtime and evidence

`CAD_TRANSLATE_PLUGIN_DIR` selects an explicit trusted runtime for development; ordinary AutoCAD 2025 runs automatically deploy and reuse the current assets/plugin package by DLL hash. Keep the packaged native DLL and Core validator aligned after rebuilding Core. An unknown CADTRANS command is a runtime-selection issue to check against the recorded DLL fingerprints before retrying; do not mix old import DLLs with a new correction DLL and call it a complete new-version regression. Native timing includes the selected DLL fingerprints. The pure validator is deployed under `bin/text-validation`, or selected with `CAD_TRANSLATE_TEXTCLI`; source checkouts can build it once and reuse it. Rebuild the validator and native DLLs together after changing Core. Never repeatedly compile during a translation task.

Before repeating a full import to fix a defect, collect the existing evidence once:

```powershell
python scripts/cad_translate.py diagnose --job "jobs/example"
# Supply actual world coordinates; repeat --window for up to eight affected regions.
python scripts/cad_translate.py diagnose --job "jobs/example" --window 618360 -22339 634212 -13500 --render
```

Replace the example coordinates with the affected region's actual extents. The command overwrites `artifacts/diagnostic-summary.json` with grouped risks, drawing bindings, timing and returned image paths. Rendering batches each drawing's windows in one CAD session and reuses valid cached images. It compares the source, saved pre-composition drawing when available, and current final candidate without importing or approving them. Missing/stale intermediates are excluded. Risk bounds from block definitions must be transformed to placed instances before use; historical layout risks may differ from final composition.

Use the same readable region to locate where the symptom appears: source, imported text before composition, or final composition. State one concrete cause supported by that comparison. If native layout code must change, first test actual candidate placement on a small representative fixture containing the affected text, dimensions, anchors and blocking geometry. Checking that a source file contains a function name or rendering an old candidate does not validate new placement behavior. Once the local result improves, perform one full drawing regression in a fresh job with `export --retry-from`; retain the reviewed translations where their content binding permits. Same-job bilingual `import` intentionally recovers the saved candidate and is not a test of newly deployed placement code. If local reproduction is impossible, use one instrumented full run to obtain missing evidence, then reassess instead of repeatedly changing global spacing/font parameters. This is a repair workflow, not an additional delivery gate.

Workflow timing separates the recorded task from its latest attempt and command spans from intervening translation, review, debugging and waiting. For the same source/mode/language, every fresh retry job uses `export --retry-from "jobs/previous-attempt"`; unlinked older attempts and pre-export time cannot be inferred from the last job. Repeated queries after completion do not extend its recorded finish. Native reinspection time is not a fresh end-to-end translation benchmark. Finish with the mode's hash-bound [visual review](visual-audit.md) and `deliveryReady=true`; this diagnostic workflow adds no separate acceptance gate.
