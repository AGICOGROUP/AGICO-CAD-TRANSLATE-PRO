"""Shared contracts and saved-DWG gates. Translation and native CAD remain model-directed."""
import argparse
import collections
import hashlib
import json
import math
import re
import shutil
import struct
import subprocess
import time
from pathlib import Path


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def prepare(source, job, mode, source_language, target_language):
    source, job = Path(source).resolve(), Path(job).resolve()
    require(source.is_file() and source.suffix.lower() == '.dwg', 'A native DWG source is required')
    require(not job.exists(), 'Job directory already exists; new tests require a new job')
    require(mode in ('replace', 'bilingual'), 'Unknown native mode')
    require(source_language and target_language and source_language != target_language, 'Explicit language direction required')
    job.mkdir(parents=True)
    shutil.copy2(source, job / 'input.dwg')
    task = dict(schemaVersion=1, sourcePath=str(source), sourceSha256=digest(source),
                mode=mode, sourceLanguage=source_language, targetLanguage=target_language,
                startedAt=time.time(), status='prepared', nativeInputPath=str(job/'input.dwg'))
    write(job / 'task.json', task)
    return task


def key(row):
    return row['handle'].upper(), row['slot']


def row_index(snapshot):
    rows = snapshot['rows']
    result = {key(row): row for row in rows}
    require(len(result) == len(rows), 'Duplicate handle/slot in snapshot')
    return result


def shape_rectangle(box):
    require(isinstance(box, list) and len(box) == 4 and all(isinstance(x,(int,float)) and math.isfinite(x) for x in box), 'Invalid region')
    require(box[0] < box[2] and box[1] < box[3], 'Empty region')


def in_region(box, region, tolerance=1e-5):
    return box[0] >= region[0]-tolerance and box[1] >= region[1]-tolerance and box[2] <= region[2]+tolerance and box[3] <= region[3]+tolerance


def overlap(a, b):
    return len(a) == len(b) == 4 and min(a[2],b[2])-max(a[0],b[0]) > 1e-5 and min(a[3],b[3])-max(a[1],b[1]) > 1e-5


def check_projection(snapshot):
    require(snapshot.get('worldBoxCoverage')=='complete', 'Complete instance projection required')
    blocks={b['name']:b for b in snapshot.get('blocks',[])}
    for row in snapshot['rows']:
        views=row.get('worldBoxes')
        require(isinstance(views,list), 'Missing text instance projection')
        if not views:
            block=blocks.get(row['block'],{})
            require(block.get('layout') is False and block.get('visibleInstanceCount')==0 and row['block'] not in ('*Model_Space','*Paper_Space'), 'Visible text has empty instance projection')
        for view in views:
            require(view.get('root') and isinstance(view.get('instancePath'),list), 'Invalid instance projection')
            shape_rectangle(view['box'])


def validate_mapping(source, mapping):
    for item in (source, mapping):
        require(item.get('schemaVersion') == 1 and item.get('tableIndexBase') == 0, 'Native schema 1 and zero-based indices required')
    for name in ('sourceSha256', 'cadInputSha256'):
        require(source.get(name) == mapping.get(name) and re.fullmatch('[0-9a-fA-F]{64}', source.get(name,'')), 'Source/input binding mismatch')
    mode = mapping.get('mode')
    require(mode in ('replace', 'bilingual'), 'Explicit native mode required')
    require(mapping.get('sourceLanguage') and mapping.get('targetLanguage') and mapping['sourceLanguage'] != mapping['targetLanguage'], 'Explicit languages required')
    src = row_index(source)
    if mapping.get('mode')=='bilingual':
        check_projection(source)
    edits = mapping.get('edits', [])
    actions = {key(e):e for e in edits}
    require(len(actions) == len(edits) and set(actions) == set(src), 'Incomplete or duplicate source action coverage')
    require(not source.get('coverageIssues'), 'Resolve extraction coverageIssues before writing')
    flows = {f['group']:f for f in mapping.get('reflows', [])}
    require(len(flows) == len(mapping.get('reflows', [])), 'Duplicate reflow group')
    additions = mapping.get('additions', [])
    require(mode == 'bilingual' or not additions, 'Replacement does not accept bilingual additions')
    require(mode == 'replace' or not flows, 'Bilingual must retain all original entities')
    owners = {}
    for flow in flows.values():
        require(flow.get('target','').strip() and not flow.get('verificationIssues'), 'Unresolved or empty reflow')
        require(flow.get('sourceHandles'), 'Reflow has no sources')
        for handle in flow['sourceHandles']:
            matches = [k for k in src if k[0] == handle.upper()]
            require(len(matches) == 1, 'Reflow must use unambiguous text handles')
            require(matches[0] not in owners, 'Duplicate reflow source')
            require(src[matches[0]]['block'] == flow['block'], 'Reflow crosses owning blocks')
            owners[matches[0]] = flow['group']
    added_sources, ids = {}, set()
    for addition in additions:
        aid = addition['id']
        require(aid not in ids, 'duplicate addition id')
        ids.add(aid)
        require(addition.get('target','').strip() and not addition.get('verificationIssues'), 'Empty or unresolved addition')
        require(addition.get('sourceKeys'), 'Addition has no source counterpart')
        for source_key in addition['sourceKeys']:
            k = (source_key[0].upper(), source_key[1])
            require(k in src, 'Unknown addition source counterpart')
            require(k not in added_sources, 'duplicate source counterpart addition')
            require(src[k]['block'] == addition['block'], 'Addition crosses source block')
            added_sources[k] = aid
        require(any(src[k]['handle'] == addition['properties'] for k in src if src[k]['block'] == addition['block']), 'Unknown inherited-properties entity')
        plan = addition['placement']
        shape_rectangle(plan['region'])
        position = plan['position']
        require(len(position)==3 and all(math.isfinite(v) for v in position), 'Invalid addition position')
        require(plan['region'][0] <= position[0] <= plan['region'][2] and plan['region'][1] <= position[1] <= plan['region'][3], 'Addition anchor outside region')
        require(plan['width'] > 0 and plan['height'] >= plan['minReadableHeight'] > 0 and math.isfinite(plan.get('rotation',0)), 'Unreadable addition layout')
    counts = collections.Counter()
    for k, edit in actions.items():
        row = src[k]
        require(edit['kind'] == row['kind'] and edit['expectedRaw'] == row['raw'], 'Source kind/raw mismatch')
        require(not edit.get('verificationIssues'), 'Unresolved edit verificationIssues')
        action = edit['action']
        counts[action] += 1
        if row['kind'] == 'TABLE':
            indices = [int(i) for i in row['slot'].split(',')]
            require(len(indices) in (2,3) and all(i>=0 for i in indices), 'Invalid table slot')
            require([edit.get('row'),edit.get('column')] == indices[:2], 'Table row/column mismatch')
            if len(indices)==3:
                require(edit.get('contentIndex')==indices[2], 'Table contentIndex mismatch')
        if mode == 'bilingual':
            require(action == 'retain' and edit.get('target',row['raw']) == row['raw'], 'Bilingual must retain original text')
            required = edit.get('needsTranslation')
            require(isinstance(required,bool), 'Explicit needsTranslation classification required')
            # Obvious Chinese prose must not disappear behind an already-target label.
            if mapping['sourceLanguage'].lower().startswith('zh') and re.search('[\u3400-\u9fff]', row.get('visibleText',row['raw'])):
                require(required or edit.get('retainReason') == 'engineering-token' and edit.get('retainExplanation'), 'Untranslated source needs counterpart')
            if required and k not in added_sources:
                if edit.get('retainReason')=='existing-bilingual-inline':
                    visible=row.get('visibleText',row['raw'])
                    a,b=edit.get('inlineSource',''),edit.get('inlineTarget','')
                    require(a and b and a!=b and a in visible and b in visible and edit.get('counterpartComplete') is True and edit.get('counterpartMeaning','').strip(), 'Incomplete inline counterpart evidence')
                    if mapping['sourceLanguage'].lower().startswith('zh') and not mapping['targetLanguage'].lower().startswith('zh'):
                        require(re.search('[\u3400-\u9fff]',a) and not re.search('[\u3400-\u9fff]',b), 'Inline counterpart has wrong language')
                    continue
                require(edit.get('retainReason') == 'existing-bilingual', 'Missing source counterpart')
                refs = edit.get('counterpartKeys',[])
                require(refs and edit.get('counterpartComplete') is True and edit.get('counterpartMeaning','').strip(), 'Incomplete counterpart evidence')
                for ref in refs:
                    ref_key = ref[0].upper(), ref[1]
                    require(ref_key in src and src[ref_key]['raw'].strip() and ref_key != k, 'Invalid existing counterpart')
                    visible=src[ref_key].get('visibleText',src[ref_key]['raw'])
                    if mapping['sourceLanguage'].lower().startswith('zh') and not mapping['targetLanguage'].lower().startswith('zh'):
                        require(not re.search('[\u3400-\u9fff]',visible), 'Existing counterpart has wrong language')
                    if mapping['targetLanguage'].lower().startswith('zh'):
                        require(re.search('[\u3400-\u9fff]',visible), 'Existing counterpart has wrong language')
        elif action == 'replace':
            require(edit.get('target','').strip(), 'Empty replacement target')
        elif action == 'retain':
            require(edit.get('retainReason'), 'Retain reason required')
        elif action == 'reflow-member':
            require(owners.get(k) == edit.get('reflowGroup'), 'Missing reflow source coverage')
        else:
            raise ValueError('Unsupported native action')
    require(all(actions[k]['action']=='reflow-member' for k in owners), 'Reflow source not owned')
    return dict(counts, additions=len(additions), reflows=len(flows))


def verify_content(source, mapping, saved, receipt):
    validate_mapping(source, mapping)
    src, dst = row_index(source), row_index(saved)
    # Check original source preservation before target receipts to give a useful failure.
    for edit in mapping['edits']:
        k = key(edit)
        if edit['action']=='retain':
            require(k in dst and dst[k]['raw'] == src[k]['raw'], 'Saved original text changed')
            if mapping['mode']=='bilingual':
                fields=['block','layer','color','style','font','bigFont','position','height','width','rotation']
                if src[k]['kind'] in ('TEXT','ATTRIB','ATTDEF'):fields+=['alignmentPoint','widthFactor']
                for field in fields:
                    require(field in src[k] and field in dst[k] and src[k][field] is not None and dst[k][field] is not None, 'Missing original appearance evidence: '+field)
                    require(src[k].get(field)==dst[k].get(field), 'Saved original appearance changed: '+field)
        elif edit['action']=='replace':
            require(k in dst and dst[k]['raw']==edit['target'], 'Saved target content mismatch')
        elif edit['action']=='reflow-member':
            require(k not in dst, 'Unremoved reflow source')
    require(not saved.get('coverageIssues'), 'Saved extraction has coverage issues')
    if mapping['mode']=='bilingual':
        check_projection(saved)
        styles={s['name']:s for s in saved.get('styles',[])}
        for style in source.get('styles',[]):
            require(styles.get(style['name'])==style, 'Original shared style changed')
    geo=lambda snapshot:{g['handle']:g for g in snapshot['geometry']}
    require(geo(source)==geo(saved), 'Saved engineering geometry changed')
    require(source.get('instances',[])==saved.get('instances',[]), 'Block instance transforms changed')
    expected = {a['id']:a for a in mapping.get('additions',[])}
    actual = {a['id']:a for a in receipt.get('additions',[])}
    require(set(actual)==set(expected) and len(actual)==len(receipt.get('additions',[])), 'Missing/duplicate saved addition receipts')
    generated = set()
    for aid, addition in expected.items():
        result = actual[aid]
        k = result['handle'].upper(), result.get('slot','text')
        require(k in dst and k not in src and k not in generated, 'Invalid saved addition target')
        generated.add(k)
        row=dst[k]
        require(row['raw']==addition['target'] and row['block']==addition['block'], 'Saved addition content/owner mismatch')
        require(row['height'] >= addition['placement']['minReadableHeight'], 'Unreadable saved addition')
        box=row.get('box',[])
        require(len(box)==4 and in_region(box,addition['placement']['region']), 'Saved addition outside region')
        for other in dst.values():
            if key(other)!=k and other['block']==row['block']:
                require(not overlap(box,other.get('box',[])), 'Saved bilingual text overlap: '+aid+' / '+other['handle'])
            if key(other)!=k:
                for target_view in row['worldBoxes']:
                    shape_rectangle(target_view['box'])
                    for other_view in other['worldBoxes']:
                        shape_rectangle(other_view['box'])
                        if target_view['root']==other_view['root']:
                            require(not overlap(target_view['box'],other_view['box']), 'Saved instance text overlap: '+aid+' / '+other['handle'])
    expected_flows={f['group']:f for f in mapping.get('reflows',[])}
    actual_flows={f['group']:f for f in receipt.get('reflows',[])}
    require(set(actual_flows)==set(expected_flows) and len(actual_flows)==len(receipt.get('reflows',[])), 'Missing/duplicate reflow receipts')
    for gid, flow in expected_flows.items():
        result=actual_flows[gid];k=result['handle'].upper(),'text'
        require(k in dst and k not in src and k not in generated, 'Invalid saved reflow target')
        generated.add(k)
        require(dst[k]['raw']==flow['target'] and dst[k]['block']==flow['block'], 'Saved reflow target mismatch')
    require(set(dst)=={key(e) for e in mapping['edits'] if e['action']!='reflow-member'}|generated, 'Unexplained saved text census change')
    return dict(status='content-verified',sourceRecords=len(src),savedRecords=len(dst),additions=len(expected))


def check_visual_review(job, task, candidate_hash, expected_sheet_ids):
    review=read(job/'visual-review.json')
    require(review.get('sourceSha256')==task['sourceSha256'] and review.get('candidateSha256')==candidate_hash, 'Visual review binding mismatch')
    require(review.get('issues')==[] and review.get('translationReviewed') is True and review.get('symbolsReviewed') is True, 'Visual/semantic review unresolved')
    sheets=review.get('sheets',[])
    require(sheets and len({s['id'] for s in sheets})==len(sheets), 'Actual sheet inventory required')
    require(set(expected_sheet_ids)=={s['id'] for s in sheets}, 'Visual review omitted actual sheets')
    for sheet in sheets:
        require(sheet.get('reviewed') is True and sheet.get('denseRegions'), 'Each sheet and dense region must be reviewed')
        for evidence in [sheet]+sheet['denseRegions']:
            for role in ('source','candidate'):
                img=(job/evidence[role+'Image']).resolve()
                require(img.is_relative_to(job) and img.is_file(), 'Missing native visual image')
                data=img.read_bytes()
                require(data[:8]==b'\x89PNG\r\n\x1a\n' and len(data)>512, 'Invalid visual PNG evidence')
                receipt=read(img.with_suffix(img.suffix+'.render.json'))
                wanted=task['sourceSha256'] if role=='source' else candidate_hash
                require(receipt.get('drawingSha256')==wanted and receipt.get('pngSha256')==digest(img) and receipt.get('native') is True, 'Native visual evidence binding mismatch')
    return review


def verify(job):
    job=Path(job).resolve();task=read(job/'task.json')
    source=read(job/'extracted.json');mapping=read(job/'mapping.json');saved=read(job/'saved'/'extracted.json');receipt=read(job/'write-status.json')
    candidate=job/'candidate.dwg';candidate_hash=digest(candidate)
    require(digest(task['sourcePath'])==task['sourceSha256']==source['sourceSha256'], 'Original source changed')
    require(mapping['mode']==task['mode'] and mapping['sourceLanguage']==task['sourceLanguage'] and mapping['targetLanguage']==task['targetLanguage'], 'Task mode/direction changed')
    require(receipt.get('schemaVersion')==1 and receipt.get('errors')==[] and receipt.get('status')=='written-awaiting-verification' and receipt.get('candidateSha256')==candidate_hash, 'Native write status mismatch')
    require(saved.get('openedCandidateSha256')==candidate_hash, 'Fresh saved candidate extraction required')
    require(Path(saved['engine']['inputPath']).resolve()==job/'saved'/'input.dwg' and digest(job/'saved'/'input.dwg')==saved['cadInputSha256'], 'Verification must open the actual separate copy')
    result=verify_content(source,mapping,saved,receipt)
    sheet_ids=mapping.get('scope',{}).get('sheetIds',[])
    require(sheet_ids and len(set(sheet_ids))==len(sheet_ids), 'Explicit actual sheet inventory required')
    review=check_visual_review(job,task,candidate_hash,sheet_ids)
    result.update(status='verified',mode=task['mode'],sourceSha256=task['sourceSha256'],candidateSha256=candidate_hash,
                  reviewedSheetIds=[s['id'] for s in review['sheets']],warnings=review.get('warnings',[]),
                  mappingSha256=digest(job/'mapping.json'),savedSnapshotSha256=digest(job/'saved'/'extracted.json'),
                  visualReviewSha256=digest(job/'visual-review.json'),writeStatusSha256=digest(job/'write-status.json'))
    write(job/'verification.json',result)
    return result


def deliver(job, output):
    job, output=Path(job).resolve(),Path(output).resolve()
    result=read(job/'verification.json')
    require(result.get('status')=='verified', 'Native verified evidence required')
    task=read(job/'task.json');candidate=job/'candidate.dwg'
    require(digest(candidate)==result['candidateSha256'] and digest(task['sourcePath'])==result['sourceSha256'], 'Delivery binding changed')
    for field,path in [('mappingSha256',job/'mapping.json'),('savedSnapshotSha256',job/'saved'/'extracted.json'),('visualReviewSha256',job/'visual-review.json'),('writeStatusSha256',job/'write-status.json')]:
        require(result.get(field)==digest(path), 'Delivery evidence changed: '+field)
    output.mkdir(parents=True,exist_ok=True)
    suffix=re.sub('[^A-Za-z0-9-]','',task['targetLanguage']).upper()
    stem=Path(task['sourcePath']).stem+'-'+suffix+('-BILINGUAL' if task['mode']=='bilingual' else '')
    existing=[int(p.stem.rsplit('-v',1)[1]) for p in output.glob(stem+'-v*.dwg') if p.stem.rsplit('-v',1)[1].isdigit()]
    version=max(existing,default=0)+1;target=output/f'{stem}-v{version:02d}.dwg'
    with target.open('xb') as stream, candidate.open('rb') as src:
        shutil.copyfileobj(src,stream)
    require(digest(target)==result['candidateSha256'], 'Final copy hash mismatch')
    write(job/'delivery.json',dict(path=str(target),sha256=digest(target),status='delivered'))
    return str(target)


def deploy(source, target, files, archive):
    source,target,archive=Path(source).resolve(),Path(target).resolve(),Path(archive).resolve()
    require(target!=Path(target.anchor) and source!=target and not target.is_relative_to(source) and not source.is_relative_to(target), 'Unsafe deployment roots')
    require(not archive.is_relative_to(target) and not archive.is_relative_to(source), 'Archive must be outside active skill roots')
    paths=[]
    for name in files:
        rel=Path(name);src=(source/rel).resolve();dst=(target/rel).resolve()
        require(not rel.is_absolute() and src.is_relative_to(source) and dst.is_relative_to(target) and src.is_file(), 'Unsafe deployment file')
        paths.append((rel,src,dst))
    require(len({r for r,_,_ in paths})==len(paths), 'Duplicate deployment path')
    keep={r for r,_,_ in paths};preserved={'work','jobs','outputs','runs','.git'}
    target.mkdir(parents=True,exist_ok=True);archive.mkdir(parents=True,exist_ok=False)
    before=[]
    for old in list(target.rglob('*')):
        if not old.is_file():continue
        rel=old.relative_to(target)
        if rel.parts[0] in preserved:continue
        require(old.resolve().is_relative_to(target), 'Deployment path escaped target')
        if rel not in keep or digest(old)!=digest(source/rel):
            backup=archive/rel;backup.parent.mkdir(parents=True,exist_ok=True);shutil.move(str(old),str(backup));before.append(str(rel))
    for rel,src,dst in paths:
        dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst)
        require(digest(src)==digest(dst), 'Mounted deployment mismatch')
    result=dict(installedFiles=len(paths),removedOrReplaced=before,target=str(target),archive=str(archive))
    write(archive/'deployment.json',result)
    return result


def finish_process(process, status, timeout, log, exit_grace=2):
    """Wait for a completed native status, then end only this owned process tree."""
    deadline=time.monotonic()+timeout
    completed=None
    while time.monotonic()<deadline:
        if Path(status).is_file():
            try:
                value=read(status)
                if value.get('status'):completed=value;break
            except (ValueError,OSError):pass
        if process.poll() is not None:break
        time.sleep(.1)
    if process.poll() is None:
        try:process.wait(timeout=exit_grace if completed else 0)
        except subprocess.TimeoutExpired:
            if hasattr(subprocess,'CREATE_NO_WINDOW'):
                subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
            else:process.kill()
            process.wait(timeout=10)
    if completed is None:
        raise TimeoutError('Native stage ended without completed status; inspect retained log')
    return completed


def run_native(job, engine, script, stage, timeout=180):
    job=Path(job).resolve();task=read(job/'task.json');input_path=job/'input.dwg'
    if stage=='verify':
        input_path=job/'saved'/'input.dwg';input_path.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(job/'candidate.dwg',input_path)
    script=Path(script).resolve();engine=Path(engine).resolve()
    require(engine.is_file() and script.is_file(), 'Native engine/script missing')
    require(input_path.is_file() and input_path!=Path(task['sourcePath']).resolve(), 'Only isolated working copies may be opened')
    before=digest(input_path)
    if stage=='write':
        source,mapping=read(job/'extracted.json'),read(job/'mapping.json')
        validate_mapping(source,mapping)
        require(before==mapping['cadInputSha256'], 'Current native input binding changed')
    if stage in ('write','verify'):
        for proof in ('verification.json','delivery.json'):
            if (job/proof).exists():(job/proof).unlink()
    import os
    env=dict(os.environ,CAD_NATIVE_JOB=str(job),CAD_NATIVE_INPUT=str(input_path),CAD_NATIVE_STAGE=stage,
             CAD_NATIVE_SOURCE_HASH=task['sourceSha256'],CAD_NATIVE_INPUT_HASH=before)
    status=job/(stage+'-status.json')
    if status.exists():status.unlink()
    with (job/(stage+'.log')).open('wb') as log:
        process=subprocess.Popen([str(engine),'/i',str(input_path),'/s',str(script)],env=env,stdout=log,stderr=log,
                                 creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        finish_process(process,status,timeout,log)
    require(status.is_file(), 'Native process produced no status')
    result=read(status);require(result.get('status') not in ('failed',None), 'Native stage failed: '+str(result))
    if stage in ('export','verify'):
        manifest_path=job/'extracted.json' if stage=='export' else job/'saved'/'extracted.json'
        snapshot=read(manifest_path);snapshot['cadInputSha256']=digest(input_path)
        if stage=='verify':snapshot['openedCandidateSha256']=before
        write(manifest_path,snapshot)
    require(digest(task['sourcePath'])==task['sourceSha256'], 'Source was modified')
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__);subs=parser.add_subparsers(dest='command',required=True)
    prep=subs.add_parser('prepare');prep.add_argument('--source',type=Path,required=True);prep.add_argument('--job',type=Path,required=True);prep.add_argument('--mode',choices=['replace','bilingual'],required=True);prep.add_argument('--source-language',required=True);prep.add_argument('--target-language',required=True)
    for name in ('validate','verify','deliver'):
        cmd=subs.add_parser(name);cmd.add_argument('--job',type=Path,required=True)
        if name=='deliver':cmd.add_argument('--output',type=Path,required=True)
    native=subs.add_parser('run-native');native.add_argument('--job',type=Path,required=True);native.add_argument('--engine',type=Path,required=True);native.add_argument('--script',type=Path,required=True);native.add_argument('--stage',choices=['export','write','verify'],required=True);native.add_argument('--timeout',type=int,default=180)
    args=parser.parse_args();values=vars(args);command=values.pop('command')
    if command=='prepare':result=prepare(args.source,args.job,args.mode,args.source_language,args.target_language)
    elif command=='validate':result=validate_mapping(read(args.job/'extracted.json'),read(args.job/'mapping.json'))
    elif command=='run-native':result=run_native(**values)
    elif command=='verify':result=verify(args.job)
    else:result=deliver(args.job,args.output)
    print(json.dumps(result,ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
