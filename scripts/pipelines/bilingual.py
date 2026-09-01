import re
from pathlib import Path
from pipeline_io import read_jsonl, write_report

SOURCE_TEXT = re.compile(r"[\u2e80-\u2fff\u3000-\u303f\u31c0-\u31ef\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\ufe10-\ufe1f\ufe30-\ufe4f\uff01-\uff60\uffe0-\uffee\U00020000-\U0002fa1f\U00030000-\U000323af]")
TARGET_TEXT = re.compile(r"[A-Za-z]{2,}")

class BilingualPipeline:
    mode = "bilingual"

    def check_translations(self, manifest_path: Path, translations_path: Path, report_path: Path | None, validate_complete) -> dict[str, object]:
        record_count = validate_complete(manifest_path, translations_path)
        manifest = read_jsonl(manifest_path)
        translations = read_jsonl(translations_path)
        manifest_by_id = {str(record["recordId"]): record for record in manifest}
        bilingual_ids: list[str] = []
        missing_source: list[str] = []
        missing_target: list[str] = []
        invalid: list[str] = []
        translated_source_records = 0
        for translation in translations:
            record_id = str(translation["recordId"])
            source = str(manifest_by_id[record_id].get("plainText", ""))
            translated = str(translation["translatedText"])
            if SOURCE_TEXT.search(source):
                translated_source_records += 1
                bilingual_ids.append(record_id)
                if source not in translated:
                    missing_source.append(record_id)
                if not TARGET_TEXT.search(translated):
                    missing_target.append(record_id)
            if (source and not translated.strip()) or "\ufffd" in translated:
                invalid.append(record_id)
        passed = not missing_source and not missing_target and not invalid
        report = {"schemaVersion": "1.0", "pipeline": "bilingual-v1", "outputMode": self.mode,
            "status": "passed" if passed else "failed", "records": record_count,
            "translatedSourceRecords": translated_source_records, "chineseResidualCount": 0,
            "chineseResidualRecordIds": [], "invalidTranslationCount": len(invalid),
            "invalidTranslationRecordIds": invalid, "bilingualRecordCount": len(bilingual_ids),
            "missingChineseRecordIds": missing_source, "missingEnglishRecordIds": missing_target}
        write_report(report_path, report)
        return report

    def check_candidate(self, manifest_path: Path, report_path: Path) -> dict[str, object]:
        records = read_jsonl(manifest_path)
        missing = [str(r.get("recordId", "")) for r in records
            if SOURCE_TEXT.search(str(r.get("plainText", ""))) and not TARGET_TEXT.search(str(r.get("plainText", "")))]
        report = {"schemaVersion": "1.0", "pipeline": "bilingual-v1", "outputMode": self.mode,
            "status": "passed" if not missing else "failed", "records": len(records),
            "plainTextChineseResidualCount": 0, "plainTextChineseResidualRecordIds": [],
            "rawTextChineseResidualCount": 0, "rawTextChineseResidualRecordIds": [],
            "missingEnglishRecordCount": len(missing), "missingEnglishRecordIds": missing}
        write_report(report_path, report)
        return report
