from .replace import ReplacePipeline
from .bilingual import BilingualPipeline

_PIPELINES = {
    "replace": ReplacePipeline(),
    "bilingual": BilingualPipeline(),
}

def get_pipeline(mode: str):
    try:
        return _PIPELINES[mode]
    except KeyError as error:
        raise ValueError(f"Unsupported output mode: {mode}") from error
