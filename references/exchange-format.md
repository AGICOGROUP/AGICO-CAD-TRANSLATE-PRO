# Exchange format

Use this reference only to diagnose translation-batch or contract failures.

`manifest.input.jsonl` is the complete sealed AutoCAD export. Never load it into model context. `prepare-translations` creates the model-facing worklist in bounded `part-*.jsonl` files:

```json
{"recordId":"stable-id","sourceText":"Chinese text with [[protected markers]]"}
```

Return one compact record per work item:

```json
{"recordId":"stable-id","translatedText":"English text with [[protected markers]]"}
```

Preserve markers exactly and in order. Do not return hashes, status, reason, geometry, properties, raw text, or non-Chinese records.

`assemble-translations` restores `schemaVersion`, `inputHash`, `reviewStatus`, and `reason`; fills non-Chinese records from the manifest; validates exact CJK work-item coverage and marker order; and writes the complete one-to-one `translations.output.jsonl` required by the unchanged importer.

Errors print counts and at most 20 IDs. Full reports remain under `artifacts/`. Import repeats the quality gate internally before starting AutoCAD. After composition it re-exports the candidate and requires zero CJK in both `plainText` and `rawText` before publication.
