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


def _box_size(bounds):
    if not isinstance(bounds, dict):
        return None, None
    try:
        return (bounds["maxX"] - bounds["minX"], bounds["maxY"] - bounds["minY"])
    except (KeyError, TypeError):
        return None, None


def layout_review(adjustments, minimum_ratio=0.65, maximum_growth_ratio=1.5, severe_growth_ratio=2.0):
    """Identify layout risk even when collision checks pass.

    Two opposite failure modes matter. Text shrunk below the original height is
    hard to read; text whose box grew taller because a longer translation was
    wrapped into several lines invades the space above or below it and can sit on
    top of a table whose own text was not part of the occupancy set. A wider box
    is reported separately: target text is normally longer than the source, so the
    width signal is informational and only the height signal gates delivery.
    """
    rows, grown, severe, wider = [], [], [], []
    for item in adjustments.get("adjustments", []):
        before, after = item.get("originalHeight"), item.get("newHeight")
        if isinstance(before, (float, int)) and isinstance(after, (float, int)) and before > 0:
            ratio = after / before
            if ratio < minimum_ratio:
                rows.append({"recordId": item.get("recordId"), "handle": item.get("newHandle"),
                    "heightRatio": round(ratio, 4), "originalHeight": before, "newHeight": after})
        source_width, source_height = _box_size(item.get("sourceBounds"))
        candidate_width, candidate_height = _box_size(item.get("candidateBounds"))
        if not source_width or not source_height or candidate_width is None or candidate_height is None:
            continue
        height_growth = candidate_height / source_height
        width_growth = candidate_width / source_width
        entry = {"recordId": item.get("recordId"), "handle": item.get("newHandle"),
            "heightGrowthRatio": round(height_growth, 4), "widthGrowthRatio": round(width_growth, 4),
            "sourceBox": {"width": round(source_width, 2), "height": round(source_height, 2)},
            "candidateBox": {"width": round(candidate_width, 2), "height": round(candidate_height, 2)},
            "actions": item.get("actions", []), "reason": item.get("reason")}
        if height_growth >= maximum_growth_ratio:
            grown.append(entry)
            if height_growth >= severe_growth_ratio:
                severe.append(entry)
        elif width_growth >= maximum_growth_ratio:
            wider.append(entry)
    return {"status": "review-needed" if rows or grown else "no-layout-risk",
            "thresholdRatio": minimum_ratio, "growthThresholdRatio": maximum_growth_ratio,
            "severeGrowthThresholdRatio": severe_growth_ratio,
            "count": len(rows), "records": rows,
            "growthCount": len(grown), "growthRecords": grown,
            "severeGrowthCount": len(severe), "severeGrowthRecords": severe,
            "widthGrowthCount": len(wider), "widthGrowthRecords": wider,
            "recordIds": sorted({row.get("recordId") for row in rows + grown if row.get("recordId")}),
            "severeRecordIds": sorted({row.get("recordId") for row in severe if row.get("recordId")}),
            "note": "Inspect final placed instances at readable scale; fitting success alone does not imply legibility. "
                    "growthRecords lists labels whose box grew taller than the source footprint because a longer target "
                    "was wrapped, so they can cover a neighbouring table or frame even though the fit test passed. "
                    "widthGrowthRecords is informational: target text is normally longer horizontally."}
