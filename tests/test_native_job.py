import copy
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest


def api():
    path = Path(__file__).parents[1] / 'scripts' / 'native_job.py'
    if not path.exists():
        pytest.fail('New native job contract is unavailable')
    spec = importlib.util.spec_from_file_location('native_job', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def sample(mode='replace'):
    row = dict(handle='A1', kind='TEXT', slot='text', raw='基础', visibleText='基础',
               block='*Model_Space', layer='Notes', height=2, width=1,
               rotation=0, position=[0, 0, 0], box=[0, 0, 4, 2], style='Standard',
               color=7,font='simhei.ttf',bigFont='',alignmentPoint=[0,0,0],widthFactor=1,
               worldBoxes=[dict(root='Model',instancePath=[],box=[0,0,4,2])])
    source = dict(schemaVersion=1, tableIndexBase=0, sourceSha256='a'*64,
                  cadInputSha256='b'*64, rows=[row], blocks=[], layouts=[], styles=[],
                  geometry=[], coverageIssues=[],worldBoxCoverage='complete')
    mapping = dict(schemaVersion=1, tableIndexBase=0, mode=mode, sourceLanguage='zh',
                   targetLanguage='en', sourceSha256='a'*64, cadInputSha256='b'*64,
                   edits=[dict(handle='A1', kind='TEXT', slot='text', expectedRaw='基础',
                               action='replace', target='Foundation', needsTranslation=True,
                               layoutPlan={}, protectedValues=[], verificationIssues=[])],
                   reflows=[], additions=[])
    return source, mapping


def bilingual():
    source, mapping = sample('bilingual')
    mapping['edits'][0].update(action='retain', target='基础', retainReason='covered-by-addition')
    mapping['additions'] = [dict(id='pair-1', sourceKeys=[['A1', 'text']], block='*Model_Space',
                                properties='A1', target='Foundation', translationRequired=True,
                                placement=dict(position=[0, -1, 0], rotation=0, width=6, height=2,
                                               region=[0, -3, 6, -1], minReadableHeight=1.3),
                                verificationIssues=[])]
    return source, mapping


def test_prepare_new_test_never_accepts_existing_job(tmp_path):
    mod = api()
    source = tmp_path / 'source.dwg'
    source.write_bytes(b'untouched drawing')
    job = tmp_path / 'job'
    result = mod.prepare(source, job, 'bilingual', 'zh', 'en')
    assert (job / 'input.dwg').read_bytes() == b'untouched drawing'
    assert result['mode'] == 'bilingual'
    with pytest.raises(ValueError, match='exists'):
        mod.prepare(source, job, 'replace', 'zh', 'en')
    assert source.read_bytes() == b'untouched drawing'


def test_complete_replace_contract_passes():
    source, mapping = sample()
    assert api().validate_mapping(source, mapping)['replace'] == 1


def test_missing_source_record_is_blocking():
    source, mapping = sample()
    mapping['edits'] = []
    with pytest.raises(ValueError, match='coverage'):
        api().validate_mapping(source, mapping)


def test_changed_raw_and_stale_input_are_rejected():
    source, mapping = sample()
    mapping['edits'][0]['expectedRaw'] = '基础X'
    with pytest.raises(ValueError, match='raw'):
        api().validate_mapping(source, mapping)
    mapping['edits'][0]['expectedRaw'] = '基础'
    mapping['cadInputSha256'] = 'c'*64
    with pytest.raises(ValueError, match='binding'):
        api().validate_mapping(source, mapping)


def test_old_manifest_cannot_enter_native_contract():
    source, _ = sample()
    with pytest.raises(ValueError, match='schema'):
        api().validate_mapping(source, {'pipelineVersion':'2.0', 'translatedText':'Foundation'})


def test_bilingual_retains_source_and_has_complete_pair():
    source, mapping = bilingual()
    assert api().validate_mapping(source, mapping)['additions'] == 1


def test_bilingual_cannot_replace_source():
    source, mapping = sample('bilingual')
    with pytest.raises(ValueError, match='retain'):
        api().validate_mapping(source, mapping)


def test_bilingual_missing_translation_is_blocking():
    source, mapping = bilingual()
    mapping['additions'] = []
    with pytest.raises(ValueError, match='counterpart'):
        api().validate_mapping(source, mapping)


def test_duplicate_bilingual_additions_are_rejected():
    source, mapping = bilingual()
    duplicate = copy.deepcopy(mapping['additions'][0])
    duplicate['id'] = 'pair-2'
    mapping['additions'].append(duplicate)
    with pytest.raises(ValueError, match='duplicate'):
        api().validate_mapping(source, mapping)


def test_existing_bilingual_requires_real_counterpart_evidence():
    source, mapping = bilingual()
    mapping['additions'] = []
    edit = mapping['edits'][0]
    edit.update(retainReason='existing-bilingual', counterpartKeys=[['BAD', 'text']],
                counterpartMeaning='Foundation', counterpartComplete=True)
    with pytest.raises(ValueError, match='counterpart'):
        api().validate_mapping(source, mapping)


def test_untranslated_value_in_bilingual_title_block_still_needs_pair():
    source, mapping = bilingual()
    mapping['edits'][0].update(retainReason='already-target')
    mapping['additions'] = []
    with pytest.raises(ValueError, match='counterpart'):
        api().validate_mapping(source, mapping)


def test_outside_addition_region_rejected():
    source, mapping = bilingual()
    mapping['additions'][0]['placement']['position'] = [50, -1, 0]
    with pytest.raises(ValueError, match='region'):
        api().validate_mapping(source, mapping)


def test_saved_bilingual_rejects_modified_original():
    source, mapping = bilingual()
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    with pytest.raises(ValueError, match='original'):
        api().verify_content(source, mapping, saved, {'additions':[]})


def test_saved_replace_requires_new_extraction_target():
    source, mapping = sample()
    with pytest.raises(ValueError, match='target'):
        api().verify_content(source, mapping, source, {})


def test_saved_geometry_change_is_blocking():
    source, mapping = sample()
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    saved['geometry'] = [dict(handle='B1', type='AcDbLine', shape={'a':[0,0,0],'b':[2,0,0]})]
    with pytest.raises(ValueError, match='geometry'):
        api().verify_content(source, mapping, saved, {})


def test_deliver_never_accepts_old_delivery_ready(tmp_path):
    mod = api()
    candidate = tmp_path / 'candidate.dwg'
    candidate.write_bytes(b'candidate')
    (tmp_path / 'verification.json').write_text(json.dumps({'deliveryReady':True}))
    with pytest.raises(ValueError, match='verified'):
        mod.deliver(tmp_path, tmp_path / 'outputs')


def test_deploy_removes_stale_active_files(tmp_path):
    mod = api()
    source, target = tmp_path/'source', tmp_path/'mounted'
    source.mkdir();target.mkdir()
    (source/'SKILL.md').write_text('new skill')
    (target/'SKILL.md').write_text('old skill')
    (target/'old_bilingual.py').write_text('old runtime')
    mod.deploy(source, target, ['SKILL.md'], tmp_path/'archive')
    assert (target/'SKILL.md').read_text() == 'new skill'
    assert not (target/'old_bilingual.py').exists()
    assert (tmp_path/'archive'/'old_bilingual.py').read_text() == 'old runtime'


def test_missing_original_style_evidence_blocks_bilingual():
    source,mapping=bilingual()
    source['styles']=[dict(name='Standard',font='simsun.ttc',width=1)]
    saved=copy.deepcopy(source)
    saved['styles'][0]['font']='Arial.ttf'
    with pytest.raises(ValueError,match='style'):
        api().verify_content(source,mapping,saved,{'additions':[]})


def test_bilingual_target_drift_is_blocking():
    source,mapping=bilingual()
    saved=copy.deepcopy(source)
    saved['rows'].append(dict(source['rows'][0],handle='B1',kind='MTEXT',raw='Footing',height=2,box=[0,-3,5,-1]))
    with pytest.raises(ValueError,match='content'):
        api().verify_content(source,mapping,saved,{'additions':[dict(id='pair-1',handle='B1',sourceKeys=[['A1','text']])]})


def test_bilingual_existing_counterpart_cannot_be_same_source_language():
    source,mapping=bilingual()
    other=dict(source['rows'][0],handle='A2',raw='基础',visibleText='基础')
    source['rows'].append(other)
    mapping['additions']=[]
    mapping['edits'][0].update(retainReason='existing-bilingual',counterpartKeys=[['A2','text']],counterpartMeaning='Foundation',counterpartComplete=True)
    mapping['edits'].append(dict(mapping['edits'][0],handle='A2',counterpartKeys=[['A1','text']]))
    with pytest.raises(ValueError,match='language'):
        api().validate_mapping(source,mapping)


def test_completed_hidden_stage_ends_prompt_waiting_process(tmp_path):
    mod=api();status=tmp_path/'status.json'
    program="import pathlib,json,time;pathlib.Path("+repr(str(status))+").write_text(json.dumps({'status':'extracted'}));time.sleep(60)"
    process=subprocess.Popen([sys.executable,'-c',program],creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    with (tmp_path/'log.txt').open('wb') as log:
        mod.finish_process(process,status,5,log,exit_grace=.1)
    assert process.poll() is not None


def test_nested_instance_collision_is_blocking():
    source,mapping=bilingual();saved=copy.deepcopy(source)
    saved['rows'].append(dict(source['rows'][0],handle='B1',raw='Foundation',height=2,box=[0,-3,5,-1],worldBoxes=[dict(root='Model',instancePath=[],box=[0,-3,5,-1])]))
    nested=dict(source['rows'][0],handle='C1',block='Nested',raw='梁',worldBoxes=[dict(root='Model',instancePath=['D1'],box=[1,-3,3,-1])])
    source['rows'].append(nested);saved['rows'].append(nested)
    mapping['edits'].append(dict(handle='C1',kind='TEXT',slot='text',expectedRaw='梁',action='retain',target='梁',needsTranslation=False,retainReason='engineering-token',retainExplanation='Fixture token'))
    with pytest.raises(ValueError,match='instance text overlap'):
        api().verify_content(source,mapping,saved,{'additions':[dict(id='pair-1',handle='B1')]})


def test_missing_projection_blocks_bilingual():
    source,mapping=bilingual();source.pop('worldBoxCoverage')
    with pytest.raises(ValueError,match='projection'):
        api().validate_mapping(source,mapping)


def test_visual_review_must_cover_every_actual_sheet(tmp_path):
    mod=api();task=dict(sourceSha256='a'*64)
    mod.write(tmp_path/'visual-review.json',dict(sourceSha256='a'*64,candidateSha256='b'*64,issues=[],translationReviewed=True,symbolsReviewed=True,sheets=[dict(id='one',reviewed=True,denseRegions=[{}])]))
    with pytest.raises(ValueError,match='omitted'):
        mod.check_visual_review(tmp_path,task,'b'*64,['one','two'])


def test_bilingual_width_factor_change_is_blocking():
    source,mapping=bilingual();saved=copy.deepcopy(source);saved['rows'][0]['widthFactor']=.5
    with pytest.raises(ValueError,match='widthFactor'):
        api().verify_content(source,mapping,saved,{'additions':[]})


def test_null_original_appearance_is_blocking():
    source,mapping=bilingual();source['rows'][0]['font']=None
    with pytest.raises(ValueError,match='appearance'):
        api().verify_content(source,mapping,copy.deepcopy(source),{'additions':[]})


def test_visible_empty_projection_is_blocking():
    source,mapping=bilingual();source['rows'][0]['worldBoxes']=[]
    with pytest.raises(ValueError,match='projection'):
        api().validate_mapping(source,mapping)


def test_complete_inline_bilingual_does_not_get_duplicate_addition():
    source,mapping=bilingual();row=source['rows'][0];row['raw']=row['visibleText']='基础 Foundation'
    mapping['additions']=[]
    mapping['edits'][0].update(expectedRaw=row['raw'],target=row['raw'],retainReason='existing-bilingual-inline',counterpartComplete=True,counterpartMeaning='Foundation',inlineSource='基础',inlineTarget='Foundation')
    assert api().validate_mapping(source,mapping)['additions']==0
