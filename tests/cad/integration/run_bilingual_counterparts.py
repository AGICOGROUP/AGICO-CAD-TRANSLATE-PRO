"""Fresh DWG regression: complete counterparts, technical tokens and repeat additions."""
import argparse
import json
from pathlib import Path
from run_v2_fixture import cad, ezdxf, to_dwg


def run(output):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    doc = ezdxf.new('R2018')
    doc.styles.new('Fixture', dxfattribs={'font': 'simsun.ttc'})
    space = doc.modelspace()

    def text(value, x, y):
        space.add_text(value, dxfattribs={'insert': (x, y), 'height': 3, 'style': 'Fixture'})

    text('精密袋滤器 Bag Filter', 20, 900)
    text('硬脂酸 Acid', 20, 800)
    text('风机 Fan', 20, 700)
    text('燃料', 20, 600)
    text('Petroleum Coke', 20, 594)
    text('氮气', 20, 500)
    text('Nitrogen', 20, 494)
    text('压力 1.5MPa', 20, 400)
    text('Pressure 15MPa', 20, 394)
    # A real grid must apply the same completeness rule before selecting slots.
    for y in (100, 120, 140, 160):
        space.add_line((0, y), (200, y))
    for x in (0, 10, 200):
        space.add_line((x, 100), (x, 160))
    text('1', 2, 145)
    text('2', 2, 125)
    text('3', 2, 105)
    text('精密袋滤器 Bag Filter', 15, 145)
    text('泵 Pump', 15, 125)
    text('1000', 15, 105)
    dxf = output / 'counterparts.dxf'
    doc.saveas(dxf)
    host = cad.discover_autocad()
    source = to_dwg(dxf, host)
    reports = []
    for label in ('first', 'repeat'):
        job = output / label
        assert cad.run_export(source, job, 'zh-CN', 'en', host, output_mode='bilingual') == 0
        cad.prepare_translation_worklist(job)
        translations = []
        for part in (job / 'exchange/translation-worklist').glob('part-*.jsonl'):
            for row in cad._read_jsonl(part):
                value = row['sourceText']
                for original, translated in {
                    '精密袋滤器 Bag Filter': 'Fine Bag Filter', '硬脂酸 Acid': 'Stearic Acid',
                    '风机 Fan': 'Fan', '燃料': 'Fuel', '氮气': 'Nitrogen', '泵 Pump': 'Pump',
                    '压力': 'Pressure',
                }.items():
                    value = value.replace(original, translated)
                translations.append({'recordId': row['recordId'], 'translatedText': value})
        batch = job / 'exchange/fixture-translations.jsonl'
        cad._atomic_write_jsonl(batch, translations)
        assembled = cad.assemble_translations(job, batch)
        assert cad.run_import(job, Path(assembled['output']), host) == 0
        native = json.loads((job / 'artifacts/bilingual-native-check.json').read_text(encoding='utf-8'))
        assert native['status'] == 'passed', native
        assert native['addedCount'] == (5 if label == 'first' else 0), native
        assert not native['changedSources'] and not native['missingTargets'], native
        pairs = json.loads((job / 'artifacts/bilingual-pairs.json').read_text(encoding='utf-8'))['pairs']
        assert len(pairs) == 8, pairs
        if label == 'first':
            assert sum(p['decision'] == 'added' and p['targetText'] == 'Fine Bag Filter' for p in pairs) == 2
            assert any(p['decision'] == 'added' and p['targetText'] == 'Fuel' for p in pairs)
            assert any(p['decision'] == 'added' and '1.5' in p['targetText'] for p in pairs)
        reports.append(native)
        source = job / 'results/candidate.dwg'
    (output / 'report.json').write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'status': 'passed', 'added': [r['addedCount'] for r in reports], 'output': str(output)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    run(parser.parse_args().output)
