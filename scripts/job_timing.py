"""Wall-clock accounting, including time between model-facing commands."""
import json
import time
from pathlib import Path


def record(job, event, started=None, *, now=None):
    job = Path(job)
    path = job / "artifacts" / "workflow-timing.json"
    now = time.time() if now is None else now
    data = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {
        "startedAtEpoch": started if started is not None else now,
        "budgetSeconds": 1200, "events": []}
    data["events"].append({"event": event, "atEpoch": now})
    data["elapsedSeconds"] = round(now - data["startedAtEpoch"], 3)
    data["remainingSeconds"] = round(max(0, data["budgetSeconds"] - data["elapsedSeconds"]), 3)
    data["budgetExceeded"] = data["elapsedSeconds"] > data["budgetSeconds"]
    data["measurementScope"] = "first export invocation through latest event; includes inter-command waiting; excludes time before export"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return data
