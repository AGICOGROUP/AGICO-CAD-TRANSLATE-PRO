import importlib.util
import json
import subprocess
import sys
import pytest
from pathlib import Path


def api():
    path = Path(__file__).parents[1] / 'scripts' / 'replacement_groups.py'
    spec = importlib.util.spec_from_file_location('replacement_groups', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def row(handle, text, box, *, kind='TEXT', block='*Model_Space', height=10, region=None):
    item = dict(handle=handle, slot='text', kind=kind, raw=text,
                visibleText=text, box=box, height=height, rotation=0, block=block)
    if region is not None:
        item['region'] = region
    return item


def handles(units):
    return [tuple(member['handle'] for member in unit['members']) for unit in units]


def test_thin_native_separator_stays_in_its_standard_identifier():
    rows = [row('A', 'GB', [0, 0, 7.4, 9.3]),
            row('B', '50009', [12, 0, 32, 9.3]),
            row('C', '一', [33.5, 4.57, 39.4, 5.43]),
            row('D', '2012', [40, 0, 56, 9.3])]
    for item in rows:
        item['position'] = [item['box'][0], 0, 0]
    units = api().build_units(rows)
    assert handles(units) == [('A', 'B', 'C', 'D')]
    assert units[0]['source'] == 'GB50009一2012'


def test_thin_separator_baseline_does_not_join_a_different_line():
    rows = [row('A', 'GB50009', [0, 20, 32, 29.3]),
            row('B', '一', [33.5, 24.57, 39.4, 25.43]),
            row('C', '2012', [40, 0, 56, 9.3])]
    rows[1]['position'] = [33.5, 20, 0]
    assert handles(api().build_units(rows)) == [('A', 'B'), ('C',)]


def test_wrapped_parenthesis_continuation_does_not_swallow_next_legend():
    rows = [row('A', '暗敷导体（沿结构层敷设，两端引出', [0, 40, 100, 50]),
            row('B', '与外部导体焊接并防腐）', [0, 22.5, 85, 32.5]),
            row('C', '明敷导体（做法见说明）', [0, 6.5, 95, 16.5])]
    units = api().build_units(rows)
    assert handles(units) == [('A', 'B'), ('C',)]
    assert units[0]['source'].count('\n') == 1


def test_closed_parenthesis_does_not_absorb_an_independent_label():
    rows = [row('A', '暗敷导体（做法见说明）', [0, 20, 100, 30]),
            row('B', '明敷导体（做法见说明）', [0, 4, 95, 14])]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_same_baseline_separate_columns_do_not_form_one_sentence():
    rows = [row('A', '左栏说明', [0, 0, 40, 10]),
            row('B', '右栏说明', [200, 0, 240, 10])]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_adjacent_mixed_script_fragments_stay_together():
    rows = [row('A', '基础', [0, 0, 20, 10]),
            row('B', 'C30', [21, 0, 41, 10])]
    assert handles(api().build_units(rows)) == [('A', 'B')]


def test_small_baseline_jitter_does_not_reverse_inline_reading_order():
    rows = [row('A', '基础', [0, 0, 20, 10]),
            row('B', 'C30', [21, 1, 41, 11])]
    assert handles(api().build_units(rows)) == [('A', 'B')]


def test_table_cells_never_merge_even_when_close():
    rows = [row('A', '设备名称', [0, 0, 40, 10], kind='TABLE'),
            row('B', '规格型号', [42, 0, 82, 10], kind='TABLE')]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_explicit_regions_stop_fragment_merging():
    rows = [row('A', '设备', [0, 0, 20, 10], region='left-cell'),
            row('B', '名称', [22, 0, 42, 10], region='right-cell')]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_grid_line_between_text_boxes_stops_merging():
    rows = [row('A', '设备', [0, 0, 20, 10]),
            row('B', '名称', [22, 0, 42, 10])]
    grid = [dict(type='AcDbLine', block='*Model_Space',
                 a=[21, -5, 0], b=[21, 15, 0])]
    assert handles(api().build_units(rows, grid)) == [('A',), ('B',)]


def test_nested_geometry_shape_also_blocks_cell_crossing():
    rows = [row('A', '设备', [0, 0, 20, 10]),
            row('B', '名称', [22, 0, 42, 10])]
    grid = [dict(type='AcDbLine', block='*Model_Space',
                 shape=dict(a=[21, -5, 0], b=[21, 15, 0]))]
    assert handles(api().build_units(rows, grid)) == [('A',), ('B',)]


def test_polyline_vertex_points_also_block_cell_crossing():
    rows = [row('A', '设备', [0, 0, 20, 10]),
            row('B', '名称', [22, 0, 42, 10])]
    grid = [dict(type='AcDbPolyline', block='*Model_Space', shape=dict(
        closed=False, vertices=[dict(p=[21, -5]), dict(p=[21, 15])] ))]
    assert handles(api().build_units(rows, grid)) == [('A',), ('B',)]


def test_wrapped_paragraph_joins_only_inside_column():
    rows = [row('A', '钢筋应按设计要求', [0, 20, 80, 30]),
            row('B', '进行绑扎和锚固', [0, 7, 70, 17]),
            row('C', '邻栏文字', [200, 20, 250, 30])]
    assert handles(api().build_units(rows)) == [('A', 'B'), ('C',)]


def test_numbered_item_starts_new_unit():
    rows = [row('A', '1. 基础施工说明', [0, 20, 90, 30]),
            row('B', '2. 钢筋工程说明', [0, 7, 90, 17])]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_indented_numbered_item_continuation_survives_wider_line_spacing():
    rows = [row('A', '1. 基础混凝土施工要求', [0, 100, 100, 110]),
            row('B', '应按设计标高浇筑', [10, 82.6, 100, 92.6]),
            row('C', '并按规定养护。', [10, 64.7, 100, 74.7]),
            row('D', '2. 钢筋施工要求', [0, 47.3, 100, 57.3])]
    units = api().build_units(rows)
    assert handles(units) == [('A', 'B', 'C'), ('D',)]
    assert units[0]['source'] == '1. 基础混凝土施工要求\n应按设计标高浇筑\n并按规定养护。'


def test_wide_gap_without_numbered_indent_does_not_join():
    rows = [row('A', '基础混凝土施工要求', [0, 100, 100, 110]),
            row('B', '应按设计标高浇筑', [10, 82.1, 100, 92.1]),
            row('C', '1. 基础混凝土施工要求', [200, 100, 300, 110]),
            row('D', '施工说明：', [210, 82.1, 300, 92.1])]
    assert handles(api().build_units(rows)) == [('A',), ('C',), ('B',), ('D',)]


def test_subheading_after_numbered_item_is_not_a_continuation():
    rows = [row('A', '1. 基础混凝土施工要求', [0, 100, 100, 110]),
            row('B', '施工说明：', [10, 82.1, 100, 92.1])]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_short_unmarked_heading_after_numbered_item_is_not_a_continuation():
    rows = [row('A', '2) 结构混凝土耐久性的基本要求', [0, 100, 100, 110]),
            row('B', '环境类别', [10, 82.1, 100, 92.1])]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_every_visible_source_record_has_one_group_owner():
    rows = [row('A', '基础', [0, 0, 20, 10]),
            row('B', 'C30', [21, 0, 41, 10]),
            row('C', '型号', [0, 30, 20, 40], kind='TABLE')]
    members = [m['handle'] for unit in api().build_units(rows) for m in unit['members']]
    assert sorted(members) == ['A', 'B', 'C']


def test_cli_uses_extracted_grid_and_writes_source_units(tmp_path):
    source = tmp_path / 'extracted.json'
    output = tmp_path / 'units.json'
    packet = tmp_path / 'packet.json'
    source.write_text(json.dumps(dict(sourceSha256='a' * 64, cadInputSha256='b' * 64,
                                      rows=[row('A', '设备', [0, 0, 20, 10]),
                                             row('B', '名称', [22, 0, 42, 10])],
                                      geometry=[dict(type='AcDbLine', block='*Model_Space',
                                                     a=[21, -5, 0], b=[21, 15, 0])])), encoding='utf-8')
    script = Path(__file__).parents[1] / 'scripts' / 'replacement_groups.py'
    subprocess.run([sys.executable, str(script), str(source), str(output), '--packet', str(packet)], check=True)
    result = json.loads(output.read_text(encoding='utf-8'))
    assert result['sourceSha256'] == 'a' * 64
    assert result['cadInputSha256'] == 'b' * 64
    assert handles(result['units']) == [('A',), ('B',)]
    compact = json.loads(packet.read_text(encoding='utf8'))
    assert compact['sourceSha256'] == result['sourceSha256']
    assert compact['cadInputSha256'] == result['cadInputSha256']
    assert compact['unitCount'] == 2
    assert {o['unitId'] for e in compact['entries'] for o in e['occurrences']} == {u['id'] for u in result['units']}


def test_native_point_vertices_block_cell_crossing():
    rows = [row('A', '设备', [0, 0, 20, 10]), row('B', '名称', [22, 0, 42, 10])]
    grid = [dict(type='AcDbPolyline', block='*Model_Space', shape=dict(
        vertices=[dict(point=[21, -5]), dict(point=[21, 15])]))]
    assert handles(api().build_units(rows, grid)) == [('A',), ('B',)]


def test_horizontal_table_rule_prevents_paragraph_merge():
    rows = [row('A', '基础混凝土施工要求', [0, 20, 100, 30]),
            row('B', '采用规定的强度等级', [0, 7, 100, 17])]
    grid = [dict(type='AcDbLine', block='*Model_Space', a=[-5, 18], b=[105, 18])]
    assert handles(api().build_units(rows, grid)) == [('A',), ('B',)]


def test_arc_chord_is_not_a_table_rule():
    rows = [row('A', '设备', [0, 0, 20, 10]), row('B', '名称', [22, 0, 42, 10])]
    grid = [dict(type='AcDbPolyline', block='*Model_Space', shape=dict(
        vertices=[dict(point=[21, -5], bulge=1), dict(point=[21, 15])]))]
    assert handles(api().build_units(rows, grid)) == [('A', 'B')]


@pytest.mark.parametrize('box', [[0, 0, float('nan'), 10], [0, 0, float('inf'), 10], ['x', 0, 20, 10]])
def test_invalid_bounds_remain_atomic_for_review(box):
    units = api().build_units([row('A', '基础', box)])
    assert units[0]['box'] is None
    assert units[0]['review'] == 'missing-native-bounds'


def test_distant_baselines_do_not_rescan_all_prior_lines(monkeypatch):
    mod = api()
    calls = 0
    original = mod._box
    def counted(value):
        nonlocal calls
        calls += 1
        return original(value)
    monkeypatch.setattr(mod, '_box', counted)
    rows = [row(str(i), 'Independent label.', [0, i * 40, 80, i * 40 + 10])
            for i in range(500)]
    assert len(mod.build_units(rows)) == 500
    assert calls < 20000


def test_tall_nearby_lines_still_join_after_candidate_pruning():
    rows = [row('A', '1. 结构构件施工要求', [0, 100, 200, 120], height=20),
            row('B', '应按图纸完成施工。', [20, 65, 200, 85], height=20)]
    assert handles(api().build_units(rows)) == [('A', 'B')]


def test_numbered_prose_continuation_can_cross_background_ruling():
    rows = [row('A', '2.所有用电设备的金属外壳均做等电位联结', [0, 20, 120, 30]),
            row('B', '并与接地装置可靠连接。', [1, 7, 120, 17])]
    grid = [dict(type='AcDbLine', block='*Model_Space', a=[-5, 18], b=[125, 18])]
    assert handles(api().build_units(rows, grid)) == [('A', 'B')]


def test_numbered_continuation_with_font_jitter_and_terminal_colon():
    rows = [row('A', '2）本工程采用下列保护措施并应', [0, 20, 120, 30]),
            row('B', '符合本条下列各项具体规定：', [-0.1, 7, 120, 17])]
    grid = [dict(type='AcDbLine', block='*Model_Space', a=[-5, 18], b=[125, 18])]
    assert handles(api().build_units(rows, grid)) == [('A', 'B')]


def test_short_subheading_is_separate_even_at_ordinary_spacing():
    rows = [row('A', '2. 结构混凝土耐久性的基本要求', [0, 20, 120, 30]),
            row('B', '施工说明：', [10, 7, 90, 17])]
    assert handles(api().build_units(rows)) == [('A',), ('B',)]


def test_translation_packet_deduplicates_without_losing_context_or_symbols():
    mod = api()
    rows = [row('A', '柱', [0, 0, 10, 10], block='Structure'),
            row('B', '柱', [0, 0, 10, 10], block='Electrical'),
            row('C', '±0.000', [0, 0, 40, 10], block='Levels'),
            row('D', '+0.000', [0, 0, 40, 10], block='OtherLevels')]
    units = mod.build_units(rows)
    packet = mod.translation_packet(units)
    assert packet['unitCount'] == 4
    assert len(packet['entries']) == 3
    columns = next(e for e in packet['entries'] if e['source'] == '柱')
    assert {o['block'] for o in columns['occurrences']} == {'Structure', 'Electrical'}
    ids = [o['unitId'] for e in packet['entries'] for o in e['occurrences']]
    assert len(ids) == len(set(ids)) == 4
    assert {e['source'] for e in packet['entries']} == {'柱', '±0.000', '+0.000'}


def test_inserted_block_grid_is_transformed_into_owning_space():
    rows = [row('A', '设备', [0, 0, 20, 10]), row('B', '名称', [22, 0, 42, 10])]
    geometry = [dict(type='AcDbLine', block='Grid', shape=dict(a=[10,-5,0], b=[10,15,0])),
                dict(type='AcDbBlockReference', block='*Model_Space', shape=dict(definition='AB',
                     matrix=[1,0,0,11, 0,1,0,0, 0,0,1,0, 0,0,0,1]))]
    assert handles(api().build_units(rows, geometry, [dict(handle='AB',name='Grid')])) == [('A',), ('B',)]


def test_nested_rotated_grid_is_transformed_once_per_reference():
    rows = [row('A', '设备', [0, 0, 20, 10]), row('B', '名称', [22, 0, 42, 10])]
    geometry = [dict(type='AcDbLine', block='Grid', shape=dict(a=[-5,0,0], b=[15,0,0])),
                dict(type='AcDbBlockReference',block='Container',shape=dict(definition='AB',matrix=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1])),
                dict(type='AcDbBlockReference',block='*Model_Space',shape=dict(definition='CD',matrix=[0,-1,0,21,1,0,0,0,0,0,1,0,0,0,0,1]))]
    blocks=[dict(handle='AB',name='Grid'),dict(handle='CD',name='Container')]
    assert handles(api().build_units(rows, geometry, blocks)) == [('A',), ('B',)]


def test_grid_inside_overhanging_font_bounds_still_separates_cells():
    rows=[row('A','最小值',[0,0,22,10]),row('B','6',[22,0,30,10])]
    grid=[dict(type='AcDbLine',block='*Model_Space',a=[21,-5],b=[21,15])]
    assert handles(api().build_units(rows,grid)) == [('A',),('B',)]
