"""Targeted semantic-loss review; findings are not proof of mistranslation."""
import re
from translation_work import visible


def review(source, target):
    text = visible(source.get("plainText", ""))
    translated = visible(target)
    issues = []
    if "有限公司" in text and not re.search(r"\b(ltd|limited|co|company|corporation|inc)\b", translated, re.I):
        issues.append("company_legal_name_may_be_truncated")
    if "仓" in text and not re.search(r"\b(silo|bin|hopper|storage|warehouse|tank)\b", translated, re.I):
        issues.append("storage_equipment_meaning_may_be_missing")
    if "生石灰" in text and not re.search(r"\b(quicklime|lime|CaO)\b", translated, re.I):
        issues.append("quicklime_material_may_be_missing")
    if len(re.findall(r"[\u3400-\u9fff]", text)) >= 5 and re.fullmatch(r"[A-Z]{2,5}", translated):
        issues.append("unexplained_acronym_may_lose_meaning")
    return issues


def layout_review(adjustments, minimum_ratio=0.65):
    """Identify readability risk even when collision checks pass."""
    rows = []
    for item in adjustments.get("adjustments", []):
        before, after = item.get("originalHeight"), item.get("newHeight")
        if isinstance(before, (float, int)) and isinstance(after, (float, int)) and before > 0:
            ratio = after / before
            if ratio < minimum_ratio:
                rows.append({"recordId": item.get("recordId"), "handle": item.get("newHandle"),
                    "heightRatio": round(ratio, 4), "originalHeight": before, "newHeight": after})
    return {"status": "review-needed" if rows else "no-severe-height-reduction",
            "thresholdRatio": minimum_ratio, "count": len(rows), "records": rows,
            "note": "Inspect final placed instances at readable scale; fitting success alone does not imply legibility."}
