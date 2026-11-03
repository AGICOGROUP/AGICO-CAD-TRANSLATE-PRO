import copy
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest


def test_native_thin_separator_can_be_protected_with_its_identifier():
    rows = [dict(handle='A', kind='TEXT', raw='GB50009', block='Model', height=10,
                 position=[0, 0, 0], box=[0, 0, 32, 9.3]),
            dict(handle='B', kind='TEXT', raw='一', block='Model', height=10,
                 position=[33.5, 0, 0], box=[33.5, 4.57, 39.4, 5.43]),
            dict(handle='C', kind='TEXT', raw='2012', block='Model', height=10,
                 position=[40, 0, 0], box=[40, 0, 56, 9.3])]
    mod = api()
    original = mod.protected_source_text(rows)
    mod.check_protected_values(original, 'GB50009-2012',
                               [dict(source='GB50009一2012', target='GB50009-2012')])


def test_fitting_must_not_drop_a_declared_material_qualifier():
    with pytest.raises(ValueError, match='Protected value'):
        api().check_protected_values('热镀锌钢管', 'galvanized steel conduit',
                                     [dict(source='热镀锌', target='hot-dip galvanized')])


def long_note_mapping():
    source, mapping = sample()
    row = source['rows'][0]
    row.update(raw='1. 基础采用筏板基础，混凝土强度等级和垫层厚度按设计要求施工，',
               visibleText='1. 基础采用筏板基础，混凝土强度等级和垫层厚度按设计要求施工，',
               position=[0, 4, 0], box=[0, 4, 20, 6])
    lower = dict(row, handle='A2', raw='并按图纸标明的标高及范围设置，施工前复核所有相关工程尺寸。',
                 visibleText='并按图纸标明的标高及范围设置，施工前复核所有相关工程尺寸。',
                 position=[0, 0, 0], box=[0, 0, 18, 2])
    source['rows'].append(lower)
    mapping['edits'] = [dict(mapping['edits'][0], handle=r['handle'], expectedRaw=r['raw'],
                              action='reflow-member', target='', reflowGroup='note') for r in source['rows']]
    mapping['reflows'] = [dict(group='note', sourceHandles=['A1', 'A2'], block=row['block'],
                              target='Foundation construction and levels as designed.',
                              layoutPlan=dict(position=[0, 6, 0], width=20, region=[0, -10, 20, 6]),
                              minReadableHeight=1, protectedValues=[], verificationIssues=[])]
    return source, mapping


def circle_label_mapping():
    source, mapping = sample()
    source['rows'][0].update(raw='LB1', visibleText='LB1', height=1,
                             position=[-.4, -.4, 0], box=[-.4, -.4, .4, .4])
    source['blocks'] = [dict(name='*Model_Space', handle='D0', origin=[0, 0, 0], entities=2)]
    source['geometry'] = [dict(handle='C1', block='*Model_Space', type='AcDbCircle',
                               shape=dict(center=[0, 0, 0], radius=1))]
    mapping['edits'][0].update(expectedRaw='LB1', action='retain', target='LB1', retainReason='engineering-token')
    return source, mapping


def test_circled_identifier_cannot_be_absorbed_into_an_english_paragraph():
    source, mapping = circle_label_mapping()
    mapping['edits'][0].update(action='reflow-member', reflowGroup='note', target='')
    mapping['reflows'] = [dict(group='note', sourceHandles=['A1'], block='*Model_Space',
                              target='The slab is LB1 and reinforcement is continuous.',
                              protectedValues=[], verificationIssues=[])]
    with pytest.raises(ValueError, match='Circled identifier'):
        api().validate_mapping(source, mapping)


def test_saved_identifier_must_still_be_inside_its_original_circle():
    source, mapping = circle_label_mapping()
    saved = copy.deepcopy(source)
    saved['rows'][0].update(position=[5, 5, 0], box=[5, 5, 5.8, 5.8])
    with pytest.raises(ValueError, match='original marker'):
        api().verify_content(source, mapping, saved, {})


def test_original_circled_identifier_is_accepted_unchanged():
    source, mapping = circle_label_mapping()
    assert api().verify_content(source, mapping, copy.deepcopy(source), {})['status'] == 'content-verified'


def test_large_equipment_circle_is_not_treated_as_a_tiny_id_bubble():
    source, mapping = circle_label_mapping()
    source['geometry'][0]['shape']['radius'] = 100
    assert api().circled_identifiers(source) == {}


def test_isolated_rebuilt_identifier_is_allowed_inside_its_original_marker():
    source, mapping = circle_label_mapping()
    mapping['edits'][0].update(action='reflow-member', target='', reflowGroup='marker')
    mapping['reflows'] = [dict(group='marker', sourceHandles=['A1'], block='*Model_Space',
                              target='LB1', layoutPlan=dict(region=[-1, -1, 1, 1]),
                              minReadableHeight=.5, protectedValues=[], verificationIssues=[])]
    saved = copy.deepcopy(source)
    saved['rows'][0].update(handle='B1', kind='MTEXT')
    result = api().verify_content(source, mapping, saved, dict(reflows=[dict(group='marker', handle='B1')]))
    assert result['status'] == 'content-verified'


def test_invisible_construction_circle_does_not_bind_a_label():
    source, _ = circle_label_mapping()
    source['geometry'][0]['visible'] = False
    assert api().circled_identifiers(source) == {}


def test_reflection_circle_geometry_can_bind_the_same_native_id():
    source, _ = circle_label_mapping()
    source['geometry'][0] = dict(handle='C1', block='*Model_Space', kind='Circle',
                                 parameters=dict(Center=[0, 0, 0], Radius=1, Normal=[0, 0, 1]))
    assert api().circled_identifiers(source) == {('A1', 'text'): (0, 0, 1)}


def test_long_note_cannot_widen_itself_into_an_adjacent_column():
    source, mapping = long_note_mapping()
    mapping['reflows'][0]['layoutPlan'].update(width=35, region=[0, -10, 35, 6])
    with pytest.raises(ValueError, match='source note width'):
        api().validate_mapping(source, mapping)


def test_long_note_can_wrap_vertically_without_losing_its_width_limit():
    source, mapping = long_note_mapping()
    assert api().validate_mapping(source, mapping)['reflows'] == 1


def test_saved_long_note_ink_cannot_shift_into_neighboring_drawing():
    source, mapping = long_note_mapping()
    mapping['reflows'][0]['layoutPlan']['region'] = [0, -10, 35, 6]
    saved = copy.deepcopy(source)
    saved['rows'] = [dict(source['rows'][0], handle='B1', kind='MTEXT',
                         raw=mapping['reflows'][0]['target'], height=1,
                         box=[1, 0, 21, 6], inkBox=[1, 0, 21, 6])]
    with pytest.raises(ValueError, match='source note width'):
        api().verify_content(source, mapping, saved, dict(reflows=[dict(group='note', handle='B1')]))


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


def test_reflow_index_keeps_handle_ambiguity_blocking():
    source, mapping = sample()
    second = dict(source['rows'][0], slot='other-content')
    source['rows'].append(second)
    mapping['edits'].append(dict(mapping['edits'][0], slot='other-content'))
    mapping['reflows'] = [dict(group='note', sourceHandles=['a1'],
                              block='*Model_Space', target='Foundation')]
    with pytest.raises(ValueError, match='unambiguous'):
        api().validate_mapping(source, mapping)


def test_reflow_index_accepts_case_insensitive_unique_source():
    source, mapping = sample()
    mapping['edits'][0].update(action='reflow-member', reflowGroup='note')
    mapping['reflows'] = [dict(group='note', sourceHandles=['a1'],
                              block='*Model_Space', target='Foundation')]
    assert api().validate_mapping(source, mapping)['reflows'] == 1


@pytest.mark.parametrize('target', ['Foundation concrete C20', 'Foundation concrete C300'])
def test_declared_engineering_value_cannot_silently_change(target):
    source, mapping = sample()
    source['rows'][0].update(raw='基础混凝土C30', visibleText='基础混凝土C30')
    mapping['edits'][0].update(expectedRaw='基础混凝土C30', target=target, protectedValues=['C30'])
    with pytest.raises(ValueError, match='Protected value'):
        api().validate_mapping(source, mapping)


def test_explicit_engineering_representation_conversion_is_allowed():
    source, mapping = sample()
    source['rows'][0].update(raw='保护层50毫米', visibleText='保护层50毫米')
    mapping['edits'][0].update(expectedRaw='保护层50毫米', target='Cover 50mm',
                               protectedValues=[dict(source='50毫米', target='50mm')])
    assert api().validate_mapping(source, mapping)['replace'] == 1


def test_protected_value_in_mtext_paragraph_is_visible():
    source, mapping = sample()
    source['rows'][0].update(raw='基础C30', visibleText='基础C30')
    mapping['edits'][0].update(expectedRaw='基础C30', target=r'Foundation\PC30', protectedValues=['C30'])
    assert api().validate_mapping(source, mapping)['replace'] == 1


def test_mtext_color_parameter_cannot_impersonate_protected_value():
    source, mapping = sample()
    source['rows'][0].update(raw='编号30', visibleText='编号30')
    mapping['edits'][0].update(expectedRaw='编号30', target=r'{\C30;Identifier}', protectedValues=['30'])
    with pytest.raises(ValueError, match='Protected value'):
        api().validate_mapping(source, mapping)


def test_protected_unit_followed_by_sentence_period_is_valid():
    source, mapping = sample()
    source['rows'][0].update(raw='基础采用C30混凝土，保护层50mm。')
    mapping['edits'][0].update(expectedRaw=source['rows'][0]['raw'],
                               target='Foundation concrete: C30; cover 50mm.', protectedValues=['C30', '50mm'])
    assert api().validate_mapping(source, mapping)['replace'] == 1


def test_reflow_protected_standard_can_span_adjacent_native_fragments():
    source, mapping = sample()
    source['rows'][0].update(raw='GB55002', visibleText='GB55002', box=[0, 0, 8, 2])
    source['rows'].append(dict(source['rows'][0], handle='A2', raw='-2021', visibleText='-2021', box=[8.1, 0, 14, 2]))
    mapping['edits'] = [dict(handle=r['handle'], slot=r['slot'], kind=r['kind'], expectedRaw=r['raw'], action='reflow-member', reflowGroup='standard') for r in source['rows']]
    mapping['reflows'] = [dict(group='standard', sourceHandles=['A1','A2'], block='*Model_Space', target='Standard GB55002-2021', protectedValues=['GB55002-2021'])]
    assert api().validate_mapping(source, mapping)['reflows'] == 1


def test_unrelated_protected_fragments_are_not_concatenated():
    source, mapping = sample()
    source['rows'][0].update(raw='GB55002', visibleText='GB55002', box=[0, 0, 8, 2])
    source['rows'].append(dict(source['rows'][0], handle='A2', raw='-2021', visibleText='-2021', box=[200, 0, 214, 2]))
    mapping['edits'] = [dict(handle=r['handle'], slot=r['slot'], kind=r['kind'], expectedRaw=r['raw'], action='reflow-member', reflowGroup='standard') for r in source['rows']]
    mapping['reflows'] = [dict(group='standard', sourceHandles=['A1','A2'], block='*Model_Space', target='Standard GB55002-2021', protectedValues=['GB55002-2021'])]
    with pytest.raises(ValueError, match='Protected value'):
        api().validate_mapping(source, mapping)


@pytest.mark.parametrize('height,box', [(1.0,[0,-2,10,0]), (2,[0,-2,100,0])])
def test_saved_reflow_readability_and_region_are_enforced(height,box):
    source, mapping = sample()
    mapping['edits'][0].update(action='reflow-member',reflowGroup='note')
    mapping['reflows']=[dict(group='note',sourceHandles=['A1'],block='*Model_Space',target='Foundation',
                             layoutPlan=dict(region=[0,-10,30,0]),minReadableHeight=1.4)]
    saved=copy.deepcopy(source)
    saved['rows']=[dict(source['rows'][0],handle='B1',raw='Foundation',visibleText='Foundation',height=height,box=box)]
    receipt=dict(additions=[],reflows=[dict(group='note',handle='B1',slot='text')])
    with pytest.raises(ValueError,match='reflow'):
        api().verify_content(source,mapping,saved,receipt)


@pytest.mark.parametrize('native_ink_bounds',[False,True])
def test_exclusive_reflow_cannot_obscure_retained_engineering_number(native_ink_bounds):
    source,mapping=sample()
    number=dict(source['rows'][0],handle='A2',raw='6',visibleText='6',box=[15,-2,16,0])
    if native_ink_bounds:
        number.update(kind='MTEXT',box=[15,-2,50,0],inkBox=[45,-2,46,0])
    source['rows'].append(number)
    mapping['edits'][0].update(action='reflow-member',reflowGroup='note')
    mapping['edits'].append(dict(handle='A2',slot='text',kind=number['kind'],expectedRaw='6',action='retain',retainReason='engineering-value'))
    mapping['reflows']=[dict(group='note',sourceHandles=['A1'],block='*Model_Space',target='Foundation',minReadableHeight=1.4,
                             layoutPlan=dict(region=[0,-10,30,0],checkTextOverlap=True))]
    saved=copy.deepcopy(source)
    saved['rows']=[number,dict(source['rows'][0],handle='B1',raw='Foundation',visibleText='Foundation',box=[0,-2,20,0])]
    receipt=dict(additions=[],reflows=[dict(group='note',handle='B1',slot='text')])
    if native_ink_bounds:
        assert api().verify_content(source,mapping,saved,receipt)['status']=='content-verified'
    else:
        with pytest.raises(ValueError,match='overlap'):
            api().verify_content(source,mapping,saved,receipt)


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


def test_saved_anonymous_block_rename_preserves_stable_owner():
    source, mapping = sample()
    source['blocks'] = [dict(handle='C1', name='*X1123', layout=False,
                             origin=[0, 0, 0], entities=1, references=[])]
    source['geometry'] = [dict(handle='B1', type='AcDbLine', block='*X1123',
                               layer='0', color=7,
                               shape=dict(a=[0, 0, 0], b=[1, 0, 0]))]
    source['instances'] = [dict(path='Model/A2', block='*X1123',
                                matrix=list(range(16)), origin=[0, 0, 0])]
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    saved['blocks'][0]['name'] = '*X61'
    saved['geometry'][0]['block'] = '*X61'
    saved['instances'][0]['block'] = '*X61'
    assert api().verify_content(source, mapping, saved, {})['status'] == 'content-verified'
    saved['geometry'][0]['shape']['b'][0] = 2
    with pytest.raises(ValueError, match='geometry'):
        api().verify_content(source, mapping, saved, {})


def test_named_block_rename_is_not_normalized():
    source, mapping = sample()
    source['blocks'] = [dict(handle='C1', name='Column', layout=False,
                             origin=[0, 0, 0], entities=0, references=[])]
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    saved['blocks'][0]['name'] = 'Beam'
    with pytest.raises(ValueError, match='block'):
        api().verify_content(source, mapping, saved, {})


def test_ellipse_roundtrip_precision_only():
    source, mapping = sample()
    source['geometry'] = [dict(handle='B1', type='AcDbEllipse', block='*Model_Space',
                               shape=dict(MinorAxis=[0.0, 15.556349185660316]))]
    source['blocks'] = [dict(handle='C1', name='*Model_Space', layout=True,
                             origin=[0, 0, 0], entities=1, references=[])]
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    saved['geometry'][0]['shape']['MinorAxis'][1] = 15.556349185660313
    assert api().verify_content(source, mapping, saved, {})['status'] == 'content-verified'
    saved['geometry'][0]['shape']['MinorAxis'][1] = 15.5563491856
    with pytest.raises(ValueError, match='geometry'):
        api().verify_content(source, mapping, saved, {})


def test_instance_text_bounds_can_change_but_transform_cannot():
    source, mapping = sample()
    source['blocks'] = [dict(handle='C1', name='*Model_Space', layout=True,
                             origin=[0, 0, 0], entities=1, references=[])]
    source['instances'] = [dict(path='Model/A', block='*Model_Space', matrix=[1,0,0,1],
                               origin=[0,0,0], bounds=[0,0,5,2])]
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    saved['instances'][0]['bounds'] = [0,0,8,2]
    assert api().verify_content(source, mapping, saved, {})['status'] == 'content-verified'
    saved['instances'][0]['matrix'][0] = 2
    with pytest.raises(ValueError, match='transforms'):
        api().verify_content(source, mapping, saved, {})


def test_large_coordinate_roundtrip_only_accepts_few_float_ulps():
    source, mapping = sample()
    source['blocks'] = [dict(handle='C1', name='*Model_Space', layout=True,
                             origin=[0, 0, 0], entities=1, references=[])]
    source['geometry'] = [dict(handle='B1', type='AcDbArc', block='*Model_Space',
                               shape=dict(center=[0, 653775.7817339428, 0]))]
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    saved['geometry'][0]['shape']['center'][1] = 653775.7817339426
    assert api().verify_content(source, mapping, saved, {})['status'] == 'content-verified'
    saved['geometry'][0]['shape']['center'][1] += 1e-7
    with pytest.raises(ValueError, match='geometry'):
        api().verify_content(source, mapping, saved, {})


def test_viewport_roundtrip_precision_only():
    source, mapping = sample()
    source['blocks'] = [dict(handle='C1', name='*Model_Space', layout=True,
                             origin=[0, 0, 0], entities=1, references=[])]
    source['geometry'] = [dict(handle='B1', type='AcDbViewport', block='*Model_Space',
                              shape=dict(CenterPoint=[420.4999990729235, 0, 0],
                                         CustomScale=0.9999999999999997, Locked=False))]
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    saved['geometry'][0]['shape']['CenterPoint'][0] = 420.49999907292363
    saved['geometry'][0]['shape']['CustomScale'] = 1
    assert api().verify_content(source, mapping, saved, {})['status'] == 'content-verified'
    saved['geometry'][0]['shape']['CenterPoint'][0] += 1e-8
    with pytest.raises(ValueError, match='geometry'):
        api().verify_content(source, mapping, saved, {})


def test_explicit_nonprinting_source_marker_can_be_preserved():
    source, mapping = sample()
    marker = dict(source['rows'][0], handle='B2', raw='TH_SUPERPART',
                  visibleText='TH_SUPERPART', height=1e-29, box=[0, 0, 0, 0])
    source['rows'].append(marker)
    source['coverageIssues'] = [dict(type='missing-text-bounds', handle='B2')]
    mapping['edits'].append(dict(handle='B2', kind='TEXT', slot='text',
                                 expectedRaw='TH_SUPERPART', action='retain',
                                 target='TH_SUPERPART', retainReason='nonprinting-marker',
                                 verificationIssues=[]))
    mapping['scope'] = dict(coverageResolutions=[dict(type='missing-text-bounds',
                                                     handle='B2', reason='nonprinting-marker')])
    assert api().validate_mapping(source, mapping)['retain'] == 1
    saved = copy.deepcopy(source)
    saved['rows'][0]['raw'] = 'Foundation'
    assert api().verify_content(source, mapping, saved, {})['status'] == 'content-verified'


def test_coverage_exception_does_not_hide_chinese_or_unknown_proxy():
    source, mapping = sample()
    source['coverageIssues'] = [dict(type='missing-text-bounds', handle='A1')]
    mapping['scope'] = dict(coverageResolutions=[dict(type='missing-text-bounds',
                                                     handle='A1', reason='nonprinting-marker')])
    with pytest.raises(ValueError, match='coverage'):
        api().validate_mapping(source, mapping)


def test_standard_mechanical_part_proxy_preserves_geometry_without_text_gate():
    source = {'rows': [], 'coverageIssues': [dict(type='unreadable-custom-or-image', handle='B1')],
              'geometry': [dict(handle='B1', type='AcDbZombieEntity', shape=dict(
                  OriginalClassName='AmgStdPart', OriginalDxfName='STDPART2D',
                  ApplicationDescription='AmgStdPart|Product Description: AutoCAD Mechanical 2000|Company: Autodesk',
                  HasFields=False, XData=[None, None]))]}
    mapping = {'scope': {'coverageResolutions': [dict(type='unreadable-custom-or-image',
                    handle='B1', reason='preserved-proxy-graphics')]}}
    api().check_coverage_resolutions(source, mapping)
    source['geometry'][0]['shape']['HasFields'] = True
    with pytest.raises(ValueError, match='coverage'):
        api().check_coverage_resolutions(source, mapping)
    source, mapping = sample()
    source['coverageIssues'] = [dict(type='unreadable-custom-or-image', handle='B1')]
    source['geometry'] = [dict(handle='B1', type='AcDbZombieEntity',
                               shape=dict(OriginalClassName='Unknown', HasFields=False))]
    mapping.setdefault('scope', {})
    mapping['scope']['coverageResolutions'] = [dict(type='unreadable-custom-or-image',
                                                     handle='B1', reason='preserved-proxy-graphics')]
    with pytest.raises(ValueError, match='coverage'):
        api().validate_mapping(source, mapping)


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
