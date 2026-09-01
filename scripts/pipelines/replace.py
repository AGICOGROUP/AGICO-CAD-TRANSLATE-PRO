import re
from pathlib import Path
from pipeline_io import read_jsonl, write_report

SOURCE_RESIDUE = re.compile(r"[\u2e80-\u2fff\u3000-\u303f\u31c0-\u31ef\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\ufe10-\ufe1f\ufe30-\ufe4f\uff01-\uff60\uffe0-\uffee\U00020000-\U0002fa1f\U00030000-\U000323af]")

class ReplacePipeline:
    mode = "replace"

    def check_translations(self, manifest_path: Path, translations_path: Path, report_path: Path | None, validate_complete) -> dict[str, object]:
        record_count = validate_complete(manifest_path, translations_path)
        manifest = read_jsonl(manifest_path)
        translations = read_jsonl(translations_path)
        manifest_by_id = {str(record["recordId"]): record for record in manifest}
        translated_source_records = 0
        residual_ids: list[str] = []
        invalid_ids: list[str] = []
        for translation in translations:
            record_id = str(translation["recordId"])
            source_text = str(manifest_by_id[record_id].get("plainText", ""))
            translated_text = str(translation["translatedText"])
            if SOURCE_RESIDUE.search(source_text):
                translated_source_records += 1
            if SOURCE_RESIDUE.search(translated_text):
                residual_ids.append(record_id)
            if (source_text and not translated_text.strip()) or "\ufffd" in translated_text:
                invalid_ids.append(record_id)
        report = {
            "schemaVersion": "1.0", "pipeline": "replace-v1", "outputMode": self.mode,
            "status": "passed" if not residual_ids and not invalid_ids else "failed",
            "records": record_count, "translatedSourceRecords": translated_source_records,
            "chineseResidualCount": len(residual_ids), "chineseResidualRecordIds": residual_ids,
            "invalidTranslationCount": len(invalid_ids), "invalidTranslationRecordIds": invalid_ids,
            "bilingualRecordCount": 0, "missingChineseRecordIds": [], "missingEnglishRecordIds": [],
        }
        write_report(report_path, report)
        return report

    def check_candidate(self, manifest_path: Path, report_path: Path) -> dict[str, object]:
        records = read_jsonl(manifest_path)
        plain = [str(r.get("recordId", "")) for r in records if SOURCE_RESIDUE.search(str(r.get("plainText", "")))]
        raw = [str(r.get("recordId", "")) for r in records if SOURCE_RESIDUE.search(str(r.get("rawText", "")))]
        report = {"schemaVersion": "1.0", "pipeline": "replace-v1", "outputMode": self.mode,
            "status": "passed" if not plain and not raw else "failed", "records": len(records),
            "plainTextChineseResidualCount": len(plain), "plainTextChineseResidualRecordIds": plain,
            "rawTextChineseResidualCount": len(raw), "rawTextChineseResidualRecordIds": raw,
            "missingEnglishRecordCount": 0, "missingEnglishRecordIds": []}
        write_report(report_path, report)
        return report
