"""Create fresh DWGs and exercise both new contracts in the real native engine."""
import argparse
import json
import os
import shutil
import subprocess
import sys
import uuid
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts'))
import native_job as n
from native_render import render


def script(path,dll,command):
    path.write_text('_.NETLOAD\n"'+dll.as_posix()+'"\n'+command+'\n_.QUIT\n_N\n',encoding='utf-8')


def run(engine,dll):
    engine=engine.resolve();root=ROOT/'work'/'native-migration-tests'/uuid.uuid4().hex
    root.mkdir(parents=True)
    build=root/'build';bundle=Path('C:/Program Files/Autodesk/ApplicationPlugins')/('CadNativeContractTest-'+uuid.uuid4().hex+'.bundle')/'Contents'/'Windows'
    command=['dotnet','build',str(ROOT/'tests/cad/NativeFixture.csproj'),'-c','Release','-o',str(build),'-p:AutoCADRoot='+str(engine.parent),'--nologo']
    with (root/'build.log').open('wb') as log:subprocess.run(command,stdout=log,stderr=log,check=True)
    bundle.mkdir(parents=True);dll=bundle/'NativeFixture.dll';shutil.copy2(build/'NativeFixture.dll',dll)
    # Each mode receives an entirely newly constructed source DWG.
    results=[]
    for mode in ('replace','bilingual'):
        case=root/mode;case.mkdir();init=case/'init.scr';script(init,dll,'NFINIT')
        with (case/'init.log').open('wb') as log:
            process=subprocess.Popen([str(engine),'/s',str(init)],env=dict(os.environ,CAD_NATIVE_JOB=str(case)),stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
            n.finish_process(process,case/'init-status.json',60,log)
        require=n.read(case/'init-status.json');assert require['status']=='created',require
        job=case/'job';n.prepare(case/'fixture-source.dwg',job,mode,'zh','en')
        for stage,command in [('export','NFEXPORT'),('write','NFWRITE'),('verify','NFEXPORT')]:script(job/(stage+'.scr'),dll,command)
        n.run_native(job,engine,job/'export.scr','export')
        source=n.read(job/'extracted.json');rows=source['rows'];targets={'基础':'Foundation','柱':'Column','梁':'Beam','基础采用C30混凝土，保护层50mm。':'Foundation concrete: C30; cover 50mm.'}
        edits=[];adds=[]
        for row in rows:
            target=targets[row['raw']]
            edit=dict(handle=row['handle'],kind=row['kind'],slot=row['slot'],expectedRaw=row['raw'],action='replace' if mode=='replace' else 'retain',target=target if mode=='replace' else row['raw'],needsTranslation=True,layoutPlan={},protectedValues=[],verificationIssues=[])
            if mode=='bilingual':
                edit['retainReason']='covered-by-addition'
                if row['raw']=='基础':position,region,width=[0,8,0],[0,3,14,8],14
                elif row['raw']=='柱':position,region,width=[20,12,0],[20,6,35,12],15
                elif row['raw']=='梁':position,region,width=[0,5.5,0],[0,3,14,5.5],14
                else:position,region,width=[0,-10,0],[0,-18,45,-10],45
                adds.append(dict(id='pair-'+row['handle'],sourceKeys=[[row['handle'],row['slot']]],block=row['block'],properties=row['handle'],target=target,placement=dict(position=position,region=region,width=width,height=2,minReadableHeight=1.3,rotation=0),protectedValues=[],verificationIssues=[]))
            edits.append(edit)
        mapping=dict(schemaVersion=1,tableIndexBase=0,sourceSha256=source['sourceSha256'],cadInputSha256=source['cadInputSha256'],mode=mode,sourceLanguage='zh',targetLanguage='en',edits=edits,reflows=[],additions=adds,scope={'sheetIds':['fixture-model']})
        n.write(job/'mapping.json',mapping);n.validate_mapping(source,mapping);n.run_native(job,engine,job/'write.scr','write');n.run_native(job,engine,job/'verify.scr','verify')
        saved=n.read(job/'saved/extracted.json');write=n.read(job/'write-status.json');content=n.verify_content(source,mapping,saved,write)
        if mode=='bilingual':
            assert len(write['additions'])==4
            # A fresh extraction of the new input sees complete targets; no duplicate additions.
            repeat=dict(mapping,sourceSha256=saved['sourceSha256'],cadInputSha256=saved['cadInputSha256'],additions=[],edits=[])
            counterparts={a['sourceKeys'][0][0]:a['handle'] for a in write['additions']}
            for row in saved['rows']:
                is_source=row['handle'] in counterparts
                edit=dict(handle=row['handle'],kind=row['kind'],slot=row['slot'],expectedRaw=row['raw'],target=row['raw'],action='retain',needsTranslation=is_source,retainReason='existing-bilingual' if is_source else 'already-target',verificationIssues=[])
                if is_source:edit.update(counterpartKeys=[[counterparts[row['handle']],'text']],counterpartComplete=True,counterpartMeaning=targets[row['raw']])
                repeat['edits'].append(edit)
            assert n.validate_mapping(saved,repeat)['additions']==0
        for role,drawing in [('source',case/'fixture-source.dwg'),('candidate',job/'candidate.dwg')]:
            render(drawing,job/'review'/f'{role}.png',engine,[-5,-20,42,20],job)
            render(drawing,job/'review'/f'{role}-dense.png',engine,[-2,3,33,16],job)
        # Agent reads these images before creating visual-review and invoking final verify.
        n.write(job/'content-regression.json',content);results.append(dict(mode=mode,job=str(job),content=content))
    n.write(root/'results.json',results)
    print(json.dumps(dict(root=str(root),cases=results,bundle=str(bundle)),ensure_ascii=False,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--engine',type=Path,required=True)
    args=parser.parse_args();run(args.engine,None)
