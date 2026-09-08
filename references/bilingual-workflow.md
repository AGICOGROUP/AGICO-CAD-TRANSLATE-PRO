# Bilingual pipeline

Preserve original entities and add target-only MText nearby. Never rewrite the Chinese/English original to hold both languages. Existing equivalent inline text or a uniquely matched nearby translation is reused; codes such as HCQ2000 are not evidence of bilingual coverage.

```powershell
python scripts/cad_translate.py export --source "source.dwg" --job "jobs/bilingual-001" --output-mode bilingual --source-language zh-CN --target-language en
python scripts/cad_translate.py prepare-translations --job "jobs/bilingual-001"
# Return target-only translatedText in exchange/translated-batches/*.jsonl.
python scripts/cad_translate.py assemble-translations --job "jobs/bilingual-001" --translated "jobs/bilingual-001/exchange/translated-batches"
python scripts/cad_translate.py import --job "jobs/bilingual-001" --translations "jobs/bilingual-001/exchange/translations.output.jsonl"
python scripts/cad_translate.py audit-summary --job "jobs/bilingual-001"
```

English→Chinese uses `--source-language en --target-language zh-CN`. Chinese additions use a Chinese-capable font. Source fonts, contents, locations and properties must remain unchanged.

Placement measures current MText geometry, tests below/above/right/left positions, wrapping, width compression and a readable height floor. It protects neighboring text, process lines, equipment and cell/frame boundaries. Rotated text retains its plane and orientation, but non-XY planes and nested block instances require specific visual verification; a copied rotation is not proof of correct 3D placement. Never move original text to create room.

`bilingual-pairs.json` records source handle, target handle, added/reused decision and bounds. Saved output is reopened: original text/properties/geometry must match, associated target handles must exist, and non-text structure must be retained. A second pass over an already translated drawing must add zero duplicate translations.

Relevant artifacts: `bilingual-native-check.json`, `bilingual-structure.json`, `bilingual-layout-audit.json`, `bilingual-language.json`, `bilingual-final.json`. No replacement residue gate or prose composition is used. Unplaceable labels remain unchanged and are listed in `bilingual-unresolved.json`; the job fails rather than publishing an incomplete drawing. Request a layout decision only when readable free space cannot be found. Finish with a hash-bound `bilingual-visual-review.json`.
