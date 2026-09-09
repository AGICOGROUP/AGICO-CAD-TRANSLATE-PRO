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
    if value in {"es", "es-es", "es-mx", "es-419"}: return "es"
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
    raw = re.sub(r"\\[Ff][^;]*;", "", str(record.get("rawText", "")))
    legacy_chinese = source_language == "zh" or "|c134" in str(record.get("rawText", ""))
    if "\ufffd" in raw or (legacy_chinese and re.search(r"[\u0080-\u00ff]\?|\?[\u0080-\u00ff]", raw)):
        raise ValueError(
            f"Unresolved text encoding: handle={record.get('handle', '?')}, "
            f"recordId={record.get('recordId', '?')}. Verify SHX/bigfont and "
            "re-extract readable source text before translation; do not pass through or guess."
        )
    text = visible(str(record.get("plainText", "")))
    return bool((CJK if source_language == "zh" else LATIN).search(text))

def groups(records, source_language, *, semantic=False):
    """Same protected values, role and layout context only; stable representative IDs."""
    result = {}
    for row in records:
        if not needs_translation(row, source_language): continue
        properties = row.get("properties", {})
        # Presentation is applied per entity by CAD, not by shortening the translation.
        context = {"layer": properties.get("layer"),
                   "tag": properties.get("typeSpecific", {}).get("tag")} if semantic else properties
        key = json.dumps([row.get("plainText"), row.get("protectedTokens", []),
            row.get("objectType"), row.get("textRole"), context],
            sort_keys=True, ensure_ascii=False)
        result.setdefault(key, []).append(row)
    return list(result.values())

def job_groups(records, source_language, job):
    config = Path(job) / "config" / "export-job.json"
    mode = json.loads(config.read_text(encoding="utf-8")).get("outputMode") if config.is_file() else "replace"
    # Bilingual placement and its request grouping remain independent.
    return groups(records, source_language, semantic=mode == "replace")

def layout_hint(row):
    properties = row.get("properties", {})
    return {"type": row.get("objectType"), "role": row.get("textRole"),
        "layer": properties.get("layer"), "height": properties.get("height"),
        "width": properties.get("typeSpecific", {}).get("width")}
