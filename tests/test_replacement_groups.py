import importlib.util
import json
import subprocess
import sys
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


def test_every_visible_source_record_has_one_group_owner():
    rows = [row('A', '基础', [0, 0, 20, 10]),
            row('B', 'C30', [21, 0, 41, 10]),
            row('C', '型号', [0, 30, 20, 40], kind='TABLE')]
    members = [m['handle'] for unit in api().build_units(rows) for m in unit['members']]
    assert sorted(members) == ['A', 'B', 'C']


def test_cli_uses_extracted_grid_and_writes_source_units(tmp_path):
    source = tmp_path / 'extracted.json'
    output = tmp_path / 'units.json'
    source.write_text(json.dumps(dict(sourceSha256='a' * 64, cadInputSha256='b' * 64,
                                      rows=[row('A', '设备', [0, 0, 20, 10]),
                                             row('B', '名称', [22, 0, 42, 10])],
                                      geometry=[dict(type='AcDbLine', block='*Model_Space',
                                                     a=[21, -5, 0], b=[21, 15, 0])])), encoding='utf-8')
    script = Path(__file__).parents[1] / 'scripts' / 'replacement_groups.py'
    subprocess.run([sys.executable, str(script), str(source), str(output)], check=True)
    result = json.loads(output.read_text(encoding='utf-8'))
    assert result['sourceSha256'] == 'a' * 64
    assert result['cadInputSha256'] == 'b' * 64
    assert handles(result['units']) == [('A',), ('B',)]
