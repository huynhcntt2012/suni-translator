"""Real per-process English speech -> STT -> Vietnamese -> SQLite -> Stop."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import json
import subprocess
import threading
import time
import psutil
import logging
logging.basicConfig(level=logging.INFO)
from engine.config import Settings
from engine.pipeline import Pipeline
from storage.database import Database

folder = Path('tmp/verification')
source = folder/'source.wav'
if not source.exists():
    raise RuntimeError('Run scripts/verify_local.py to generate English speech first')
player_code = 'import sys,winsound; sys.stdin.readline(); winsound.PlaySound(sys.argv[1],winsound.SND_FILENAME); sys.stdin.readline()'
player = subprocess.Popen([sys.executable,'-c',player_code,str(source.resolve())],stdin=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
ready,done = threading.Event(),threading.Event()
segments,errors = [],[]
def status(message):
    print(message,flush=True)
    if 'Đang nghe riêng PID' in message: ready.set()
    if message.startswith('Lỗi:'): errors.append(message); ready.set()
db = Database(folder/'process-pipeline.sqlite3')
settings = Settings(capture_kind='process',process_pid=player.pid,process_created=psutil.Process(player.pid).create_time(),source='en',chunk_seconds=4,max_age=60)
pipeline = Pipeline(settings,db,segments.append,status,lambda _:None,done.set)
try:
    pipeline.start()
    if not ready.wait(90) or errors: raise RuntimeError(str(errors) or 'Start timeout')
    player.stdin.write(b'\n'); player.stdin.flush()
    deadline = time.monotonic()+50
    while not segments and not errors and time.monotonic()<deadline: time.sleep(0.2)
finally:
    pipeline.stop()
    if not done.wait(60): raise RuntimeError('Stop timeout')
    try:
        player.stdin.write(b'\n'); player.stdin.flush(); player.wait(timeout=10)
    except (OSError,subprocess.TimeoutExpired): player.terminate(); player.wait(timeout=3)
    player.stdin.close()
if errors or not segments: raise RuntimeError(str(errors) or 'No translation')
assert len(db.history(pipeline.session_id)) == len(segments)
result = {'pid':settings.process_pid,'segments':[s.__dict__ for s in segments],'clean_stop':done.is_set(),'persisted':len(db.history(pipeline.session_id))}
(folder/'process-pipeline-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
db.close()
print(json.dumps(result,ensure_ascii=False,indent=2))
