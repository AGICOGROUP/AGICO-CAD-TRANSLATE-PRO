# Translation preparation and quality revision

Validated on 2026-09-08 against the Algeria 25 t/day drawing, source SHA256
`b1405bb11a8e4b4385732bee3fd4293c56856f7ad075e4879ff09468b6ff5d3f`.

Replacement requests now retain semantic context while ignoring font/height/width differences.
Exact protected values, layer, role, object type and attribute tag remain grouping boundaries.
Bilingual grouping and both native placement pipelines are unchanged.

| Measurement | Before | After |
|---|---:|---:|
| Source entities requiring translation | 1119 | 1119 |
| Model request representatives | 707 | 529 |
| Representative source characters | 13197 | 11649 |
| Same-DWG native export plus import, reused translations | 32.802 s | 34.049 s |

Request count fell 25.2%. This is not a 25.2% wall-clock speed claim: requests now
also carry protected-token values needed for accurate translation. Native time did
not improve in this single-run comparison. No fresh model translation was timed;
the 20-minute end-to-end target remains unverified.

Validation job: `runs/efficiency-quality-20260908` in the mounted workspace.
The job explicitly reuses v14 translations to exercise export, preparation,
assembly and native import without conflating runtime testing with translation.
Its candidate is not a newly quality-approved deliverable.

New targeted review finds 16 source entities across 7 distinct source/target pairs
with possible semantic loss in the reused translations. A height-ratio review
finds 110 adjustments below 65% of source text height. Both are advisory review
triggers, not automatic determinations of translation or visual failure.

The skill now requires full technical/company meaning before fitting and readable
final-window inspection. Batch marker errors are reported together with exact
source/token context. Valid batches can be retained after pre-import assembly
failure. Workflow timing records inter-command waiting from first export onward;
pre-export user wait must be added separately. These changes address the observed
translation-preparation and iterative-repair bottlenecks without changing geometry
or relying on smaller type to achieve a speed budget.

Verification: 61 Python tests passed, including format-independent grouping,
independent bilingual grouping behavior, protected-value worklists, per-entity
hash fan-out, aggregate marker errors, semantic-loss examples, readability
selection and elapsed-time continuity. Skill frontmatter validation passed using
UTF-8. Native AutoCAD 2025 replacement round-trip passed automatic checks.
