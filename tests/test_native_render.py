from pathlib import Path
import importlib.util
import pytest


def test_render_refuses_output_beside_original_source(tmp_path):
    path = Path(__file__).parents[1]/'scripts'/'native_render.py'
    if not path.exists():pytest.fail('Native render helper unavailable')
    spec=importlib.util.spec_from_file_location('native_render',path)
    mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
    source=tmp_path/'source.dwg';source.write_bytes(b'original')
    with pytest.raises(ValueError,match='working directory'):
        mod.render(source,tmp_path/'view.png',tmp_path/'engine.exe',job=tmp_path/'job')
    assert source.read_bytes()==b'original'
