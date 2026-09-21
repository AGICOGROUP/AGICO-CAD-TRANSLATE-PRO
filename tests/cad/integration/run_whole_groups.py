"""Fresh full-pipeline regression for prose blocks, copied tables and false grids."""
import argparse
import json
from pathlib import Path
from run_v2_fixture import cad, ezdxf, to_dwg


def run(output, signature_rotation=90):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    doc = ezdxf.new('R2018')
    style=doc.styles.new('Fixture', dxfattribs={'font': 'simsun.ttc'})
    style.set_extended_font_data('SimSun')
    space = doc.modelspace()
    targets = {}
    def text(raw, target, x, y, width=0, rotation=0):
        targets[raw] = target
        if rotation:
            space.add_text(raw, dxfattribs={'insert': (x,y), 'height': 3, 'rotation': rotation, 'style': 'Fixture'})
        else:
            space.add_mtext(raw, dxfattribs={'insert': (x,y), 'char_height': 3, 'width': width, 'style': 'Fixture'})
    def grid(x, y, columns, rows, w=70, h=25):
        for i in range(columns+1): space.add_line((x+i*w,y),(x+i*w,y+rows*h))
        for i in range(rows+1): space.add_line((x,y+i*h),(x+columns*w,y+i*h))
    # Complete title grid at the bottom right; nearest right/bottom are outside the sheet.
    for a,b in [((0,0),(350,0)),((350,0),(350,210)),((350,210),(0,210)),((0,210),(0,0))]:
        space.add_line(a,b)
    grid(200,0,2,2)
    for i,(raw,target) in enumerate([('日期','Date'),('签名','Signature'),('实名','Name'),('专业','Discipline')]):
        text(raw,target,203+(i%2)*70,22+(i//2)*25)
    # Partial bilingual table must create four target-only cells.
    grid(200,90,2,2)
    for i,raw in enumerate(['水泵 Water Pump','水泵','水泵','水泵']):
        text(raw,'Water Pump',203+(i%2)*70,112+(i//2)*25)
    # Complete bilingual table should not be copied.
    grid(200,155,2,2)
    for i in range(4): text('阀门 Valve','Valve',203+(i%2)*70,177+(i//2)*25)
    notes=[('1. 安装前必须核对设备外形尺寸和基础预留孔位置，确认无误后方可施工。',
            '1. Before installation, verify equipment dimensions and foundation opening locations. Begin construction only after confirmation.'),
           ('2. 所有设备安装完成后应进行检查，确认连接牢固，运转正常，无异常振动。',
            '2. After installation, inspect all equipment and confirm secure connections, normal operation and no abnormal vibration.'),
           ('3. 施工期间应遵守安全规定，按照图纸要求完成设备安装和试运行工作。',
            '3. Follow safety regulations during construction and complete installation and commissioning in accordance with the drawing.')]
    for i,(raw,target) in enumerate(notes): text(raw,target,20,195-i*25,100)
    # Adjacent single-column signatures must never be blocked as a failed table.
    grid(20,5,1,4,50,15)
    for i,(raw,target) in enumerate([('审核','Reviewed by'),('校核','Checked by'),('设计','Designed by'),('制图','Drawn by')]):
        text(raw,target,28,(8 if signature_rotation==90 else 17)+i*15,rotation=signature_rotation)
    source_path=output/'groups.dxf'; doc.saveas(source_path)
    host=cad.discover_autocad(); source=to_dwg(source_path,host); job=output/'job'
    assert cad.run_export(source,job,'zh-CN','en',host,output_mode='bilingual')==0
    cad.prepare_translation_worklist(job)
    translated=[]
    for part in (job/'exchange/translation-worklist').glob('part-*.jsonl'):
        for row in cad._read_jsonl(part):
            raw=row['sourceText']
            for token in row['protectedTokens']: raw=raw.replace(token['marker'],token['raw'])
            target=targets[raw]
            for token in row['protectedTokens']: target=target.replace(token['raw'],token['marker'],1)
            translated.append({'recordId':row['recordId'],'translatedText':target})
    batch=job/'exchange/fresh.jsonl'; cad._atomic_write_jsonl(batch,translated)
    assembled=cad.assemble_translations(job,batch)
    assert cad.run_import(job,Path(assembled['output']),host)==0
    summary=cad.summarize_audit(job)
    assert summary['status']=='passed',summary
    pairs=json.loads((job/'artifacts/bilingual-pairs.json').read_text(encoding='utf-8'))['pairs']
    copies=[p for p in pairs if p['placementStrategy']=='table-copy']
    assert len(copies)==8,copies
    assert sum(p['decision']=='existing-inline' for p in pairs)==4,pairs
    assert sum(p['placementStrategy']=='note-block' for p in pairs)==3,pairs
    assert sum(p['placementStrategy']=='cell-local-or-nearby' for p in pairs)==4,pairs
    signatures = {target:i for i,target in enumerate(['Reviewed by','Checked by','Designed by','Drawn by'])}
    for pair in pairs:
        if pair['placementStrategy']!='cell-local-or-nearby': continue
        i=signatures[pair['targetText']]; b=pair['bounds']
        assert b['left']>=20 and b['right']<=70 and b['bottom']>=5+i*15 and b['top']<=20+i*15, ('Signature translation must stay in its own cell',pair)
        assert pair['heightScale']>=.7, ('Signature translation must remain readable',pair)
    layout=json.loads((job/'artifacts/bilingual-table-layout.json').read_text(encoding='utf-8'))
    for group in layout['tables']:
        if group['strategy']!='note-block' or 'destination' not in group: continue
        a,b=group['source'],group['destination']
        assert abs(a['width']-b['width'])<.001 and a['top']==b['top']
        assert b['left']>=a['right'] or b['right']<=a['left']
        assert b['right']<=0 or b['left']>=350 or b['left']>=0 and b['right']<=350, 'Prefer complete frame clearance when available'
    result={'status':'passed','copiedCells':len(copies),'retainedBilingualCells':4,'proseRows':3,'localSignatures':4,'signatureRotation':signature_rotation,
            'candidate':json.loads((job/'config/export-job.json').read_text(encoding='utf-8'))['outputPath'],'job':str(job)}
    (output/'report.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--signature-rotation',type=int,choices=[90,270],default=90)
    args=parser.parse_args()
    run(args.output,args.signature_rotation)
