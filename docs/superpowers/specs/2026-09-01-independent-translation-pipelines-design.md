# Independent CAD Translation Pipelines Design

## Goal

Turn the current CAD translator into one product with two independently managed output pipelines:

- `replace`: single-language replacement. Translate from the selected source language to the selected target language and replace the source-language content.
- `bilingual`: bilingual augmentation. Preserve the source-language content and add the missing target-language content beside or below it. Existing valid bilingual content is retained without adding a duplicate.

Language direction and output form are separate decisions. The output mode must not encode a language such as `english`; `sourceLanguage` and `targetLanguage` continue to define the direction.

## Architectural boundary

The two modes are separate end-to-end pipelines, not one shared pipeline with a late mode switch.

Each pipeline owns its own:

- export eligibility checks;
- translation-input checks;
- object composition policy;
- layout optimizer and correction policy;
- candidate-language checks;
- topology and overlap checks;
- AutoCAD audit acceptance rules;
- audit schema and final pass/fail decision.

Rules that happen to be identical today remain separately declared. This allows either pipeline to evolve without silently changing the other.

Only mechanically neutral infrastructure may be shared: filesystem access, JSON serialization, process launching, hashing, AutoCAD connection, protected-token parsing, and immutable geometry primitives. Shared helpers must return facts, not decide whether a job passes.

## Dispatch model

The command entry point validates and normalizes the requested mode, then dispatches once:

```text
request
  -> normalize mode and language pair
  -> replace pipeline OR bilingual pipeline
  -> mode-owned final result
```

After dispatch, pipeline code must not branch back into a common business gate.

New jobs use `replace` or `bilingual`. For migration only, the legacy value `english` is accepted as an alias for `replace`; newly written configuration always records `replace`. Unknown modes fail before AutoCAD is launched.

The default is `replace`, preserving the product's original single-language behavior.

## Replace pipeline

The replace pipeline is based on the stable pre-merge `main` behavior.

1. Export source text and geometry under the stable source hash.
2. Validate the translation set with replace-specific rules.
3. Replace translatable source content with target content.
4. When an equivalent nearby target-language label already exists, retain the suitable target object and remove the redundant source object according to the existing fixed-label policy.
5. Run the original replace layout optimizer and its correction passes.
6. Apply replace-specific residue, topology, overlap, text-fit, and AutoCAD audit gates.
7. Emit a replace-specific report and candidate DWG only when all replace gates pass.

This pipeline must not invoke bilingual layout or bilingual final gates.

## Bilingual pipeline

The bilingual pipeline is based on the last tested `codex/cad-layout-v2` bilingual behavior.

1. Export source text and geometry under the stable source hash.
2. Detect valid existing bilingual objects and nearby bilingual pairs.
3. Validate translations with bilingual-specific rules.
4. Preserve source content. Preserve existing target content when equivalent; otherwise add a target companion.
5. Place the companion above, below, left, or right according to available geometry, with the tested bilingual layout V2 policy as the initial implementation.
6. Apply bilingual-specific source-retention, target-presence, duplicate-pair, topology, overlap, text-fit, and AutoCAD audit gates.
7. Emit a bilingual-specific report and candidate DWG only when all bilingual gates pass.

This pipeline must not invoke replace residue rules or replace layout correction.

## Existing bilingual detection

Visible natural-language content counts as an existing translation. Technical codes, units, font names, drawing identifiers, and protected tokens do not by themselves make an object bilingual.

Detection covers:

- source and target text in one object;
- a nearby source/target pair with equivalent meaning;
- repeated two-column title-block patterns already supported by the tested bilingual policies.

An accepted existing pair is preserved and audited. It is not translated or inserted again.

## Language-pair support

The pipeline interfaces are language-neutral, but a language pair is enabled only when it has a registered translation validator and final-language gate. Initially, the existing verified `zh-CN -> en` behavior remains the release baseline. Additional directions such as `en -> zh-CN` use the same two output modes but require their own pair policies and fixtures before being reported as verified.

## Reports and artifacts

Artifacts are mode-qualified so a job cannot accidentally reuse another mode's pass result. At minimum, status data records:

- normalized output mode;
- source and target languages;
- source hash and configuration hash;
- pipeline implementation/version;
- mode-owned gate results;
- output DWG hash.

A change of output mode invalidates prior translation checks, import results, and final audits even when the source DWG is unchanged.

## Branch structure and migration

- Local `main` remains at `c0559ed`, matching `origin/main` and preserving stable single-language translation.
- Development remains on `codex/cad-layout-v2`.
- The codex branch will contain the router plus both modules; it will not replace the stable implementation with bilingual behavior globally.
- Existing `english` job metadata remains readable through alias normalization, but regenerated jobs use `replace`.

## Verification

Automated coverage must prove pipeline isolation as well as behavior:

- default and legacy `english` inputs dispatch only to replace;
- `bilingual` dispatches only to bilingual;
- replace never invokes bilingual composition, layout, or gates;
- bilingual never invokes replace composition, correction, or final gates;
- a gate change in one pipeline has a test demonstrating no effect on the other;
- existing bilingual text is not duplicated;
- missing bilingual target text is added without deleting the source;
- replace output removes applicable source-language residue;
- mode changes invalidate stale artifacts;
- both pipelines preserve source sealing, protected tokens, drawing topology, and AutoCAD audit integrity through their own gate suites.

End-to-end DWG fixtures remain mandatory before declaring a language direction and output mode verified.

## Non-goals

- Merging the two final gate suites to reduce duplication.
- Automatically treating any Latin characters as an English translation.
- Advertising untested language directions as production-ready.
- Changing `main` until the independent pipelines pass their own tests and are intentionally integrated.
