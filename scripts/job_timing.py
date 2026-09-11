"""Wall-clock accounting, including time between model-facing commands."""
import json
import time
from pathlib import Path


def record(job, event, started=None, *, now=None, retry_from=None):
    job = Path(job)
    path = job / "artifacts" / "workflow-timing.json"
    now = time.time() if now is None else now
    previous = None
    if retry_from is not None:
        parent = Path(retry_from).resolve()
        if parent == job.resolve():
            raise ValueError("Retry must use a fresh job")
        old_config, new_config = [json.loads((p / "config/export-job.json").read_text(encoding="utf-8"))
                                  for p in (parent, job)]
        keys = ("sourceSha256", "outputMode", "sourceLanguage", "targetLanguage")
        if any(not old_config.get(k) or old_config[k] != new_config.get(k) for k in keys):
            raise ValueError("Retry timing requires the same source, mode and language direction")
        previous = json.loads((parent / "artifacts/workflow-timing.json").read_text(encoding="utf-8"))
        if previous["startedAtEpoch"] > now:
            raise ValueError("Retry timing cannot start before its previous attempt")
    data = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {
        "startedAtEpoch": started if started is not None else now,
        "budgetSeconds": 1200, "events": []}
    if event == "delivery-ready" and data["events"] and data["events"][-1]["event"] == event:
        return data
    if previous is not None and not path.is_file():
        data = previous
        data["retryFromJob"] = str(parent)
        data["attemptStartedAtEpoch"] = started if started is not None else now
        for item in data["events"]:
            item.setdefault("job", str(parent))
    data["events"].append({"event": event, "atEpoch": now, "job": str(job.resolve())})
    data["elapsedSeconds"] = round(now - data["startedAtEpoch"], 3)
    data["remainingSeconds"] = round(max(0, data["budgetSeconds"] - data["elapsedSeconds"]), 3)
    data["budgetExceeded"] = data["elapsedSeconds"] > data["budgetSeconds"]
    data["measurementScope"] = "first export invocation through latest event; includes inter-command waiting; excludes time before export"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return data


def summarize(job):
    """Read recorded spans without extending a finished task or claiming CPU time."""
    path = Path(job) / 'artifacts/workflow-timing.json'
    if not path.is_file():
        return {'status': 'not-recorded'}
    data = json.loads(path.read_text(encoding='utf-8'))
    events = data.get('events', [])
    if not events:
        return {'status': 'not-recorded'}
    end = events[-1]['atEpoch']
    start = data['startedAtEpoch']
    spans = []
    for left, right in zip(events, events[1:]):
        seconds = right['atEpoch'] - left['atEpoch']
        operation = left['event'].removesuffix('-start')
        managed = (left['event'].endswith('-start') and
                   right['event'] in {operation + '-finished', operation + '-failed'} and
                   left.get('job') == right.get('job'))
        spans.append({'from': left['event'], 'to': right['event'],
                      'seconds': seconds, 'command': managed})
    command_seconds = sum(s['seconds'] for s in spans if s['command'])
    return {'status': 'recorded', 'recordedTaskSeconds': round(end - start, 3),
            'attemptSeconds': round(end - data.get('attemptStartedAtEpoch', start), 3),
            'commandSpanSeconds': round(command_seconds, 3),
            'betweenCommandSeconds': round(end - start - command_seconds, 3),
            'lastEvent': events[-1]['event'], 'retryFromJob': data.get('retryFromJob'),
            'largestSpans': [dict(s, seconds=round(s['seconds'], 3))
                             for s in sorted(spans, key=lambda s: s['seconds'], reverse=True)[:5]],
            'scope': 'Recorded events only; gaps include translation, review, debugging and waiting, not just waste. '
                     'Earlier unlinked attempts and pre-export time are not included.',
            'path': str(path)}
