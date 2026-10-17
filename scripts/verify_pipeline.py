"""Integration smoke: real device -> bounded workers -> SQLite callback -> stop."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import threading
import time
import json
from engine.audio import devices, play_wave
from engine.config import Settings
from engine.pipeline import Pipeline
from storage.database import Database

folder = Path('tmp/verification')
inputs, outputs = devices()
ready, done = threading.Event(), threading.Event()
segments, errors = [], []
def status(message):
    print(message, flush=True)
    if 'WASAPI loopback' in message:
        ready.set()
    if message.startswith('Lỗi:'):
        errors.append(message)
        ready.set()
db = Database(folder / 'pipeline.sqlite3')
pipeline = Pipeline(Settings(source='en', capture_name=inputs[0]['name'], chunk_seconds=4, max_age=60), db, segments.append, status, lambda _: None, done.set)
pipeline.start()
try:
    if not ready.wait(90) or errors:
        raise RuntimeError(str(errors) or 'Model loading timeout')
    play_wave(folder/'source.wav', outputs[0]['name'], pipeline.stop_event)
    deadline = time.monotonic()+45
    while not segments and not errors and time.monotonic() < deadline:
        time.sleep(0.2)
finally:
    pipeline.stop()
    if not done.wait(60):
        raise RuntimeError('Stop timeout')
if errors or not segments:
    raise RuntimeError(str(errors) or 'No translated segments')
assert len(db.history(pipeline.session_id)) == len(segments)
result = {'segments': [s.__dict__ for s in segments], 'clean_stop': done.is_set(), 'persisted': len(db.history(pipeline.session_id))}
(folder/'pipeline-result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
db.close()
print(json.dumps(result, ensure_ascii=False, indent=2))
