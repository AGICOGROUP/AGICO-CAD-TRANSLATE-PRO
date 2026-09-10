"""Early reuse of current-drawing inline pairs explicitly reviewed for completeness."""
import json
import re
from pathlib import Path

HAN = re.compile(r"[\u3400-\u9fff\uf900-\ufaff\U00020000-\U000323af]")

def visible_source(row):
    text = row.get("rawText", "")
    text = re.sub(r"\\[A-Za-z][^;\\]*;", "", text)
    return text.replace("\\P", "\n").replace("{", "").replace("}", "")

def inline_candidates(records):
    # Some source English is entirely inside number/unit tokens ("6.10 Stack").
    # Leave those few rows on the normal path: target-only validation needs a
    # visible word outside protected markers, not an invented duplicate label.
    def exchange_has_english(row):
        text = re.sub(r"⟦P\d{4}⟧", "", HAN.sub("", row.get("plainText", "")))
        return re.search(r"(?<!\w)[A-Za-z]{2,}(?!\w)", text)
    return [dict(recordId=r["recordId"], handle=r["handle"], inputHash=r["inputHash"],
                 sourceText=visible_source(r)) for r in records
            if r.get("handle") and HAN.search(visible_source(r))
            and exchange_has_english(r)
            and re.search(r"(?<![A-Za-z0-9_.-])[A-Za-z]{4,}(?![A-Za-z0-9_.-])", visible_source(r))]

def enabled(job):
    path = Path(job) / "config/export-job.json"
    config = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {}
    return (config.get("outputMode") == "bilingual" and
            config.get("sourceLanguage", "").lower() in {"zh", "zh-cn", "zh-hans"} and
            config.get("targetLanguage", "").lower() in {"en", "en-us", "en-gb"})

def reviewed_reuse(records, job):
    path = Path(job) / "exchange/bilingual-inline-review.json"
    if not enabled(job) or not path.is_file(): return {}
    receipt = json.loads(path.read_text(encoding="utf-8"))
    config = json.loads((Path(job) / "config/export-job.json").read_text(encoding="utf-8"))
    if receipt["sourceSha256"] != config["sourceSha256"]:
        raise ValueError("Inline review belongs to a different source drawing")
    by_id = {r["recordId"]: r for r in records}
    candidates = {r["recordId"] for r in inline_candidates(records)}
    result = {}
    for item in receipt["records"]:
        row = by_id.get(item["recordId"])
        if row is None or row["inputHash"] != item["inputHash"] or row["recordId"] not in candidates:
            raise ValueError("Inline review no longer matches its source entity")
        # Keep all protected markers in order. Native importer reuses the original
        # mixed entity; this exchange text is never written back to its source.
        result[row["recordId"]] = HAN.sub("", row["plainText"])
    return result
