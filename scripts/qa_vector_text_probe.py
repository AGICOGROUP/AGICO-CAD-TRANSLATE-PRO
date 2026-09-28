"""Detect visible-but-untranslatable content in a DWG before claiming delivery.

Two content classes render like text but are invisible to every text walk:

* PDF-imported lettering, stored as polyline outlines on layers named after the
  import (``PDF_几何图形``, ``PDF3_几何图形``, ``PDF_text`` ...). The export sees
  almost no text, the residual scan reports zero, yet the sheet still shows the
  original-language tables.
* Embedded pictures (``OLE2FRAME`` = "Picture (Enhanced Metafile)") and custom
  objects from other CAD systems (ZWCAD ``ZWMCUSTCORE``/``zwcadmiso`` and other
  proxy classes), whose text lives in data AutoCAD cannot read without the
  originating application's object enabler.

A positive hit is evidence and must be disclosed in the delivery message. A
negative result is NOT proof of absence: DWG stores names in section-specific
encodings, so this probe never replaces the text-based residual scan.

Usage: python scripts/qa_vector_text_probe.py <drawing.dwg> [<drawing2.dwg> ...]
Exit code 1 when any drawing shows vector-text or embedded-picture evidence.
"""
import os
import sys

VECTOR_LAYER_MARKERS = (
    b"PDF_", b"PDF3_", b"PDF_text", b"PDF_Text",
)
VECTOR_LAYER_SUFFIXES = (
    "\u51e0\u4f55\u56fe\u5f62".encode("gbk"),   # 几何图形
    "\u6587\u5b57".encode("gbk"),               # 文字
    "\u56fe\u5f62".encode("gbk"),               # 图形
)
PROXY_CLASS_MARKERS = (
    b"ZWMCUSTCORE", b"zwcadmiso", b"ZWCAD", b"ACAD_PROXY_ENTITY",
)
PICTURE_MARKERS = (
    b"Picture (Enhanced Metafile)", b"OLE2FRAME",
)


def probe(path):
    data = open(path, "rb").read()
    findings = {"vectorLayers": [], "proxyClasses": [], "pictures": []}
    for marker in VECTOR_LAYER_MARKERS:
        for suffix in VECTOR_LAYER_SUFFIXES:
            needle = marker + suffix
            if needle in data:
                findings["vectorLayers"].append(needle.decode("gbk", "replace"))
            elif marker in data and marker not in findings["vectorLayers"]:
                findings["vectorLayers"].append(marker.decode("ascii", "replace"))
    for marker in PROXY_CLASS_MARKERS:
        if marker in data:
            findings["proxyClasses"].append(marker.decode("ascii", "replace"))
    for marker in PICTURE_MARKERS:
        if marker in data:
            findings["pictures"].append(marker.decode("ascii", "replace"))
    return findings


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    flagged = 0
    for path in argv[1:]:
        if not os.path.isfile(path):
            print("%s: MISSING" % path)
            flagged = 1
            continue
        found = probe(path)
        hit = bool(found["vectorLayers"] or found["proxyClasses"] or found["pictures"])
        print("=== %s (%d bytes) -> %s" % (os.path.basename(path), os.path.getsize(path),
                                           "VECTOR/EMBEDDED CONTENT" if hit else "no vector-text evidence"))
        for key, label in (("vectorLayers", "PDF/vector import layers"),
                           ("proxyClasses", "custom-object classes"),
                           ("pictures", "embedded pictures")):
            if found[key]:
                print("    %s: %s" % (label, ", ".join(sorted(set(found[key])))))
        if hit:
            print("    -> disclose these in the delivery message: their content is graphics/data, not text")
            flagged = 1
    return flagged


if __name__ == "__main__":
    sys.exit(main(sys.argv))
