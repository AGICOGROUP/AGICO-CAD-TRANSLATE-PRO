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

Use `correct` for actual visual defects on existing translations. Keep full text and its meaning; try usable width, controlled wrapping, then a nearby position/height within the same cell or clearly associated whitespace. Choose coordinates from the actual native drawing, not the pixel preview. Model/block positions are in their owning definition's coordinates. Review every placed instance of a shared block.

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

`CAD_TRANSLATE_PLUGIN_DIR` selects isolated trusted branch DLLs for development; ordinary runs use the deployed plugin. Native timing includes the selected DLL fingerprints. The pure validator is deployed under `bin/text-validation`, or selected with `CAD_TRANSLATE_TEXTCLI`; source checkouts can build it once and reuse it. Rebuild the validator and native DLLs together after changing Core. Never repeatedly compile during a translation task.

Use compact errors and `*-timing.json` to locate the slow/failed stage. Bilingual phase timing separates topology, bounds, tables, placement, saving and inspection. Workflow timing includes translation/review and time between commands; native reinspection time is not a fresh end-to-end translation benchmark. Finish with the mode's hash-bound [visual review](visual-audit.md) and `deliveryReady=true`.
