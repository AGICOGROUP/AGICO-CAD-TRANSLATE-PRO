"""Bounded translation requests; no CAD mutations or pipeline acceptance rules."""
import json
import re
from pathlib import Path

HAN = re.compile(r"[\u3400-\u9fff\uf900-\ufaff\U00020000-\U000323af]")
CJK = re.compile(r"[\u2e80-\u303f\u31c0-\u31ef\u3400-\u9fff\uf900-\ufaff\ufe10-\ufe1f\ufe30-\ufe4f\uff01-\uff60\uffe0-\uffee\U00020000-\U000323af]")
LATIN = re.compile(r"[A-Za-z]{2,}")
MARKER = re.compile(r"⟦P\d{4}⟧")

def language(value):
    value = value.lower().strip()
    if value in {"zh", "zh-cn", "zh-hans"}: return "zh"
    if value in {"en", "en-us", "en-gb"}: return "en"
    raise ValueError(f"Unsupported language: {value}")

def direction(job):
    path = Path(job) / "config" / "export-job.json"
    config = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {}
    return language(config.get("sourceLanguage", "zh-CN")), language(config.get("targetLanguage", "en"))

def visible(text):
    text = MARKER.sub("", text)
    text = re.sub(r"\\[A-Za-z][^;]*;", "", text)
    return text.replace("\\P", " ").replace("\\", " ").strip(" {};")

def needs_translation(record, source_language):
    text = visible(str(record.get("plainText", "")))
    return bool((CJK if source_language == "zh" else LATIN).search(text))

def groups(records, source_language):
    """Same protected values, role and layout context only; stable representative IDs."""
    result = {}
    for row in records:
        if not needs_translation(row, source_language): continue
        key = json.dumps([row.get("plainText"), row.get("protectedTokens", []),
            row.get("objectType"), row.get("textRole"), row.get("properties", {})],
            sort_keys=True, ensure_ascii=False)
        result.setdefault(key, []).append(row)
    return list(result.values())

def layout_hint(row):
    properties = row.get("properties", {})
    return {"type": row.get("objectType"), "role": row.get("textRole"),
        "layer": properties.get("layer"), "height": properties.get("height"),
        "width": properties.get("typeSpecific", {}).get("width")}
