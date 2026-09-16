"""Fresh native fixture: title fields stay local and formatted DWG No. is reused."""
import argparse
import json
from pathlib import Path
from run_v2_fixture import cad, ezdxf, to_dwg

def run(output):
    output=output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    doc=ezdxf.new('R2018'); space=doc.modelspace()
    labels=['审  定','设  总','审  核','校  对','设  计','制  图']
    targets=['Approved by','Chief Designer','Reviewed by','Checked by','Designed by','Drawn by']
    for i,label in enumerate(labels):
        y=i*8
        for a,b in [((0,y),(30,y)),((0,y+8),(30,y+8)),((0,y),(0,y+8)),((30,y),(30,y+8))]:
            space.add_line(a,b)
        space.add_mtext(label,dxfattribs={'insert':(1,y+7),'char_height':2.4})
    space.add_mtext(r'{\fSimSun;图}{\fArial;号}\P{\fArial;DWG.}\PNo.',dxfattribs={'insert':(45,30),'char_height':2})
    space.add_mtext('水泵 HCQ2000',dxfattribs={'insert':(45,45),'char_height':2})
    path=output/'title.dxf';doc.saveas(path);host=cad.discover_autocad();source=to_dwg(path,host)
    job=output/'job';assert cad.run_export(source,job,'zh-CN','en',host,output_mode='bilingual')==0
    cad.prepare_translation_worklist(job)
    candidates=cad._read_jsonl(job/'exchange/bilingual-inline-candidates.jsonl')
    assert len(candidates)==1,candidates
    cad.prepare_translation_worklist(job,existing_inline_handles=candidates[0]['handle'])
    rows=[]
    for part in (job/'exchange/translation-worklist').glob('*.jsonl'):
        for row in cad._read_jsonl(part):
            target=dict(zip(labels,targets)).get(row['sourceText'])
            if target is None:target=row['sourceText'].replace('水泵','Water Pump')
            rows.append({'recordId':row['recordId'],'translatedText':target})
    batch=job/'exchange/fresh.jsonl';cad._atomic_write_jsonl(batch,rows)
    result=cad.assemble_translations(job,batch)
    assert cad.run_import(job,Path(result['output']),host)==0
    report=cad.summarize_audit(job);assert report['status']=='passed',report
    pairs=json.loads((job/'artifacts/bilingual-pairs.json').read_text())['pairs']
    assert report['addedCount']==7 and report['skippedExistingCount']==1,report
    sources={r['handle']:r for r in cad._read_jsonl(job/'exchange/manifest.input.jsonl')}
    checked=0
    for pair in pairs:
        raw=sources[pair['sourceHandle']]['rawText']
        if raw not in labels:continue
        checked+=1
        assert pair['placementStrategy']=='cell-local-or-nearby',pair
        y=labels.index(raw)*8;box=pair['bounds']
        assert box['left']>=0 and box['right']<=30 and box['bottom']>=y and box['top']<=y+8,pair
    assert checked==6,checked
    timing=json.loads((job/'artifacts/import-timing.json').read_text())
    assert all(timing['pluginSha256'][n]==cad.sha256(cad.PLUGIN_DIR/n) for n in cad.PLUGIN_FILES)
    (output/'report.json').write_text(json.dumps({'status':'passed','added':7,'reused':1,'runtime':timing['pluginDirectory']}))
    print((output/'report.json').read_text())

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True)
    run(parser.parse_args().output)
