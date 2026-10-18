"""Two real processes emit 440 and 1200 Hz; capture only the 440 Hz PID."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import json
import subprocess
import threading
import time
import wave
import psutil
import numpy as np
from engine.process_audio import capture_process

folder = Path('tmp/verification')
folder.mkdir(parents=True, exist_ok=True)
for frequency in (440, 1200):
    rate = 44100
    samples = (0.13*np.sin(2*np.pi*frequency*np.arange(rate*4)/rate)*32767).astype('<i2')
    with wave.open(str(folder/f'tone-{frequency}.wav'), 'wb') as output:
        output.setnchannels(1); output.setsampwidth(2); output.setframerate(rate)
        output.writeframes(samples.tobytes())
player = "import sys,winsound; sys.stdin.readline(); winsound.PlaySound(sys.argv[1],winsound.SND_FILENAME|winsound.SND_ASYNC|winsound.SND_LOOP); sys.stdin.readline(); winsound.PlaySound(None,0)"
children = [subprocess.Popen([sys.executable,'-c',player,str((folder/f'tone-{f}.wav').resolve())],stdin=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW) for f in (440,1200)]
stop, ready = threading.Event(), threading.Event()
blocks, errors = [], []
def record():
    try:
        capture_process(children[0].pid, psutil.Process(children[0].pid).create_time(), stop, lambda data, stamp: blocks.append(data), lambda _:None, ready.set)
    except Exception as exc:
        errors.append(str(exc)); ready.set()
thread = threading.Thread(target=record)
try:
    thread.start()
    if not ready.wait(15) or errors:
        raise RuntimeError(str(errors) or 'Activation timeout')
    for child in children:
        child.stdin.write(b'\n'); child.stdin.flush()
    time.sleep(4)
finally:
    stop.set()
    thread.join(timeout=8)
    for child in children:
        try:
            child.stdin.write(b'\n'); child.stdin.flush(); child.wait(timeout=3)
        except (OSError,subprocess.TimeoutExpired):
            child.terminate(); child.wait(timeout=3)
        child.stdin.close()
if errors or not blocks:
    raise RuntimeError(str(errors) or 'No audio blocks')
samples = np.concatenate(blocks)
frequencies = np.fft.rfftfreq(len(samples),1/16000)
spectrum = abs(np.fft.rfft(samples*np.hanning(len(samples))))
def band(f): return float(spectrum[abs(frequencies-f)<3].max())
selected, rejected = band(440), band(1200)
ratio = float(20*np.log10(max(selected,1e-10)/max(rejected,1e-10)))
result = {'target_hz':440,'other_process_hz':1200,'selected_amplitude':selected,'other_amplitude':rejected,'isolation_db':ratio,'capture_seconds':len(samples)/16000,'clean_stop':not thread.is_alive()}
if selected < 1 or ratio < 35 or thread.is_alive():
    raise RuntimeError('Process isolation failed: '+json.dumps(result))
(folder/'process-result.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result,indent=2))
