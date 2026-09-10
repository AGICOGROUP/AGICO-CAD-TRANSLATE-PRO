"""Native regression for one complete translation per stacked Chinese term."""
import argparse
import json
from pathlib import Path
from run_v2_fixture import cad, ezdxf, to_dwg


def run(output):
    output.mkdir(parents=True, exist_ok=False)
    doc=ezdxf.new('R2018')
    doc.styles.new('Fixture',dxfattribs={'font':'simsun.ttc'})
    space=doc.modelspace()
    space.add_lwpolyline([(0,0),(200,0),(200,160),(0,160)],close=True)
    for x,term in ((35,'硬脂酸'),(115,'聚合物')):
        for i,char in enumerate(term):
            space.add_text(char,dxfattribs={'insert':(x,120-i*(3 if x==35 else 5)),'height':3,'style':'Fixture'})
    # Nearby characters separated by a table/diagram line are NOT a term.
    space.add_text('甲',dxfattribs={'insert':(175,70),'height':3,'style':'Fixture'})
    space.add_text('乙',dxfattribs={'insert':(175,65),'height':3,'style':'Fixture'})
    space.add_line((160,69),(190,69))
    for i,char in enumerate('搅拌机'):
        space.add_text(char,dxfattribs={'insert':(35+i*3,60),'height':3,'style':'Fixture'})
    # Do not join Chinese across an intervening digit, even without grid lines.
    for i,char in enumerate('第1层'):
        space.add_text(char,dxfattribs={'insert':(110+i*3,60),'height':3,'style':'Fixture'})
    dormant=doc.blocks.new('UNUSED_TRANSLATION_FIXTURE')
    dormant.add_text('观察窗',dxfattribs={'insert':(0,0),'height':3,'style':'Fixture'})
    path=output/'terms.dxf'
    doc.saveas(path)
    host=cad.discover_autocad()
    source=to_dwg(path,host)
    reports=[]
    for label in ('first','repeat'):
        job=output/label
        assert cad.run_export(source,job,'zh-CN','en',host,output_mode='bilingual')==0
        manifest=cad._read_jsonl(job/'exchange/manifest.input.jsonl')
        assert not any(r['rawText']=='观察窗' for r in manifest),'dormant block must stay out of active scope'
        work=cad.prepare_translation_worklist(job)
        assert work['translationRecordCount']==7,work
        rows=[]
        for part in (job/'exchange/translation-worklist').glob('part-*.jsonl'):
            for row in cad._read_jsonl(part):
                rows.append({'recordId':row['recordId'],'translatedText':{'硬脂酸':'Stearic Acid','聚合物':'Polymer','搅拌机':'Mixer','第':'No.','层':'Level','甲':'Alpha','乙':'Beta'}[row['sourceText']]})
        cad._atomic_write_jsonl(job/'exchange/fixture-translations.jsonl',rows)
        assembled=cad.assemble_translations(job,job/'exchange/fixture-translations.jsonl')
        assert cad.run_import(job,Path(assembled['output']),host)==0
        report=cad.summarize_audit(job)
        assert report['status']=='passed',report
        assert report['addedCount']==(7 if label=='first' else 0),report
        pairs=json.loads((job/'artifacts/bilingual-pairs.json').read_text(encoding='utf-8'))['pairs']
        assert len(pairs)==13 and len({p['targetHandle'] for p in pairs})==7,pairs
        current=cad._read_jsonl(job/'artifacts/bilingual-candidate.jsonl')
        assert sum(r['rawText'].count('Stearic Acid') for r in current)==1
        assert sum(r['rawText'].count('Polymer') for r in current)==1
        reports.append(report)
        source=Path(report['candidate'])
    (output/'report.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(reports,ensure_ascii=False))


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',type=Path,required=True)
    run(parser.parse_args().output)
