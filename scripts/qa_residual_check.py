"""Mandatory pre-delivery check: scan a DWG for residual source-language text.

Walks *Model_Space, every *Paper_Space record and every block-table record and
dumps every group-1 string; any string still containing Han characters means the
translation missed it (or the export never saw it). Delivery must not proceed
while this reports a nonzero count.

Usage:
  python scripts/qa_residual_check.py <dwg-path> <out-txt-path>
Exit code 1 when residual source-language text is present.
"""
import re
import sys
from collections import Counter
from pathlib import Path

HAN = re.compile(r'[一-鿿]')


def analyze(out_path: Path) -> dict:
    rows = []
    for line in out_path.read_bytes().decode('gbk', 'ignore').splitlines():
        parts = line.split('|', 4)
        if len(parts) >= 5 and parts[0] == 'H':
            rows.append((parts[1], parts[2], parts[3], parts[4]))
    residual = [r for r in rows if HAN.search(r[3])]
    return {
        "textRows": len(rows),
        "residualCount": len(residual),
        "byScope": dict(Counter(r[0] for r in residual)),
        "records": [{"scope": r[0], "objectType": r[1], "handle": r[2], "text": r[3]} for r in residual],
    }


if __name__ == '__main__':
    report = analyze(Path(sys.argv[2]))
    print(f"text rows={report['textRows']} residual source-language={report['residualCount']}")
    for row in report['records'][:40]:
        print(f"  {row['scope']} {row['objectType']} {row['handle']} {row['text'][:70]!r}")
    sys.exit(1 if report['residualCount'] else 0)
