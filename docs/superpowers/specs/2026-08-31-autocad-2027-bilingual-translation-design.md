# AutoCAD 2027 Adaptive Bilingual Translation Design

## Goal

Produce a new Chinese-English DWG from a Chinese source drawing using AutoCAD 2027. Preserve every source Chinese object and all non-text geometry. Add one English companion for every translatable Chinese text record without overwriting the source drawing.

## Compatibility

The runner supports release-specific AutoCAD roots and registry releases. AutoCAD 2025 remains the default compatibility target; AutoCAD 2027 uses `R26.0` and assemblies from the configured 2027 installation. Runtime preflight must find Core Console, an initialized profile for the selected release, and the matching plugin bundle before creating a job.

## Bilingual Data Contract

Jobs declare `outputMode: bilingual`. Translation batches retain the current `recordId` and `translatedText` exchange contract. During import, the source Chinese entity remains unchanged and a new English entity is created and linked to its source record in the audit output. Existing English-only mode retains replacement behavior.

Every translatable Chinese record must have exactly one nonempty English translation. Protected markers, models, tags, quantities, standards, and identifiers remain unchanged and ordered.

## Placement

For each source text slot, generate candidate English placements below, above, right, and left. Rank candidates by collision-free fit, printable-frame containment, consistency with nearby bilingual pairs, and minimal displacement from the source. Repeated labels share a learned direction, spacing, height, and alignment when feasible.

Fixed cells and title blocks use a two-line layout inside the source-derived cell. Candidate fitting may compress English width and then reduce English height, never below 55% of the Chinese source height. It must not move Chinese text, equipment, pipes, dimensions, tables, frames, or other drawing geometry.

If no safe placement exists, leave the Chinese source untouched, do not insert an overlapping English object, and emit a bounded manual-review record.

## Auditing

Bilingual audit replaces the English-only zero-CJK gate when `outputMode` is bilingual. It verifies:

- every eligible Chinese source has one English companion or an explicit manual-review outcome;
- inserted English contains no Chinese residue;
- protected markers and identifiers are preserved;
- inserted text stays inside the innermost printable frame;
- inserted text does not collide with hard keep-outs or other inserted text;
- size never drops below 55% of source height;
- repeated-label placement is consistent where space allows.

Publishing is blocked by missing coverage, marker corruption, frame overflow, or unreported collision. Reported no-fit records set `requiresVisualReview` and remain visible in the bounded audit summary.

## Verification

Use test-first changes for release selection, profile lookup, mode-specific import behavior, placement ranking, fallback behavior, and audit gates. Build the plugin against AutoCAD 2027 managed assemblies, run unit tests, run Core Console export/import on the supplied DWG once per stage, then run the bilingual audit and inspect only changed or high-risk regions if visual review is required.

## Deliverable

Deliver only the verified `candidate.dwg` copied to a clearly named bilingual output beside the user's source file. The source DWG remains byte-for-byte unchanged.
