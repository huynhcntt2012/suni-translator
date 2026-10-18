import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import json
import subprocess
import threading
import time
import psutil
from engine.process_audio import capture_process

player = subprocess.Popen([sys.executable,'-c','import sys; sys.stdin.readline()'],stdin=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
created = psutil.Process(player.pid).create_time()
def start_capture():
    stop,ready = threading.Event(),threading.Event()
    errors=[]
    def record():
        try: capture_process(player.pid,created,stop,lambda *_:None,lambda _:None,ready.set)
        except Exception as exc: errors.append(str(exc));ready.set()
    thread=threading.Thread(target=record);thread.start()
    if not ready.wait(15) or errors: raise RuntimeError(str(errors) or 'Activation timeout')
    return stop,thread,errors
try:
    timings=[]
    for _ in range(3):
        stop,thread,errors=start_capture()
        started=time.monotonic();stop.set();thread.join(timeout=5)
        assert not thread.is_alive() and not errors, str(errors)
        timings.append(time.monotonic()-started)
    stop,thread,errors=start_capture()
    player.terminate();player.wait(timeout=3)
    thread.join(timeout=5)
    if thread.is_alive(): stop.set();thread.join(timeout=5)
    assert not thread.is_alive() and errors and 'exited' in errors[0], str(errors)
    result={'silent_start_stop_cycles':3,'stop_seconds':timings,'source_exit_detected':True,'error':errors[0]}
    Path('tmp/verification/process-lifecycle.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False,indent=2))
finally:
    if player.poll() is None: player.terminate();player.wait(timeout=3)
    player.stdin.close()
