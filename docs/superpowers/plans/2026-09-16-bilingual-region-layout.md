# Bilingual region layout implementation plan

**Goal:** Preserve complete tables and readable source/target associations, reducing layout retries without changing replacement mode.

**Approved design:** User approved the in-conversation design on 2026-09-16: retain CAD I/O, replace competing bilingual grouping with whole-region ownership, keep independent replacement/bilingual pipelines, and compare actual visual results and end-to-end time.

**Architecture:** Recover connected grids once per definition; partition only at supported semantic boundaries, not repeated row height alone. Table membership excludes narrative/legend routing. Narrative panels require prose evidence; short aligned lists preserve row correspondence. Choose nearby positions inside the drawing frame before any explicit out-of-frame review. Preserve source entities and exact copied geometry.

**Tech stack:** C#/.NET 8, AutoCAD 2025, Python runner.

## Constraints

- Existing dirty changes belong to this task's previous attempt; preserve unrelated changes.
- Do not change replacement behavior or its gates.
- Fresh drawing test: new export, translation, import, renders; no historical target-text reuse.
- Minor cosmetic issues can be warnings, but partial tables, misassociation and unreadable output cannot pass.
- No remote push or shared deployment before verification.

## Tasks

- [x] Add behavior tests for rectangle JSON round trip, complete multi-level-header and actual nested grids, short-list classification and inside-frame placement priority. Demonstrated rectangle/header failures before their fixes; native whole-drawing review remains separate.
- [x] Replace table shape-only splitting with full-grid ownership and explicit title-panel separation; share detected groups with the other bilingual planners.
- [x] Replace generic short-column note grouping with aligned-list placement; keep prose panels together and search readable in-frame positions first. Record unresolved whole groups instead of silently scattering.
- [x] Repair rectangle receipt deserialization and restore strict copied-line verification; do not accept truncated subsets when receipt bounds are missing.
- [x] Condense SKILL.md and bilingual instructions by replacing contradictory/repeated sections. Match commands and supported structures to implementation.
- [x] Run core/Python/native tests, freshly translate supplied experimental-line DWG, render main sheet plus table/legend/details, inspect final candidate, and report actual remaining limitations and measured time.

## Verification commands

```powershell
dotnet run --project src/cad/CadTranslation.Core.Tests -c Release
python -m pytest tests -q
dotnet build tests/cad/native-contact/NativeContactTests.csproj -c Release '-p:AutoCADDir=E:\AUTOCAD2025\AutoCAD 2025'
python tests/cad/native-contact/run_near_label.py --plugin <isolated-built-plugin>/NativeContactTests.dll --output <fresh-directory> --command CAD_TABLE_STRATEGY_TEST
```

Native verification must exercise multi-level headers with segmented grid lines and complete cell copying, not just a synthetic four-row schedule. Visual acceptance requires readable source/candidate details of all affected region types. Full user wait and fresh translation run time are separate from development time.

## Current checkpoint

205 core tests and 123 Python tests (39 subtests) pass. Native AutoCAD table tests pass, including nested grid copying, single pending label, frame-exterior placement, readable cell fit, exact geometry tamper rejection and unchanged sources. Full test collection also exposes a pre-existing `tests.cad` import-resolution error; that integration suite has not been claimed passing.

Fresh experimental-line regression was exported and translated from scratch (648 records, 203 unique translation entries). Complete instrument table is now recognized as 98 cells. Following explicit user approval, r6 places its complete English copy above the frame: 183 additions, 46 reused records, no unresolved/missing targets or changed sources. Actual native renders were reviewed and audit-summary reports deliveryReady=true, ready_with_warnings (print-range inclusion and minor wrapping).

User approved nearest same-side frame-exterior space for complete tables and prose; ordinary labels stay local. Neighbor-sheet interiors remain protected. A clean new grid BlockReference avoids copying associative reactors and preserves original display state. Validated DLLs replaced assets/plugin with matching Core validator; old package backed up under tmp/package-before-outside-20260916. No commit or push. See the checkpoint for timings and limitations; no measured end-to-end speedup is claimed.
