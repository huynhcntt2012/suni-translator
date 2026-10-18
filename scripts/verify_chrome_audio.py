"""Verify Chrome's WebAudio child process is included in its selected tree."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import json
import subprocess
import threading
import time
import uuid
import psutil
import numpy as np
from engine.process_audio import capture_process

folder = Path('tmp/verification').resolve()
page = folder/'chrome-audio.html'
page.write_text('''<!doctype html><meta charset="utf-8"><title>Suni audio isolation test</title>
<p>Controlled Suni audio test: 440 Hz WebAudio.</p><script>
const context = new AudioContext(); const oscillator = context.createOscillator();
const gain = context.createGain(); gain.gain.value = 0.1;
oscillator.frequency.value = 440; oscillator.connect(gain);gain.connect(context.destination);
oscillator.start(); context.resume();</script>''',encoding='utf-8')
profile = folder/('chrome-profile-'+uuid.uuid4().hex[:8])
chrome = subprocess.Popen([r'C:\Program Files\Google\Chrome\Application\chrome.exe','--headless=new','--remote-debugging-port=0','--no-first-run','--no-default-browser-check','--autoplay-policy=no-user-gesture-required',f'--user-data-dir={profile}',page.as_uri()],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
player_code = 'import sys,winsound; winsound.PlaySound(sys.argv[1],winsound.SND_FILENAME|winsound.SND_ASYNC|winsound.SND_LOOP); sys.stdin.readline()'
other = subprocess.Popen([sys.executable,'-c',player_code,str(folder/'tone-1200.wav')],stdin=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
root = psutil.Process(chrome.pid)
stop,ready = threading.Event(),threading.Event()
blocks,errors = [],[]
def record():
    try: capture_process(root.pid,root.create_time(),stop,lambda data,stamp:blocks.append(data),lambda _:None,ready.set)
    except Exception as exc: errors.append(str(exc));ready.set()
thread = threading.Thread(target=record)
try:
    thread.start()
    if not ready.wait(15) or errors: raise RuntimeError(str(errors) or 'Activation timeout')
    time.sleep(6)
finally:
    stop.set();thread.join(timeout=8)
    try: owned = root.children(recursive=True)+[root]
    except psutil.NoSuchProcess: owned=[]
    for process in owned:
        try: process.terminate()
        except psutil.NoSuchProcess: pass
    psutil.wait_procs(owned,timeout=5)
    other.stdin.write(b'\n');other.stdin.flush();other.wait(timeout=3);other.stdin.close()
if errors or not blocks: raise RuntimeError(str(errors) or 'No Chrome audio')
samples = np.concatenate(blocks)
frequencies = np.fft.rfftfreq(len(samples),1/16000)
spectrum = abs(np.fft.rfft(samples*np.hanning(len(samples))))
selected=float(spectrum[abs(frequencies-440)<3].max())
other_amplitude=float(spectrum[abs(frequencies-1200)<3].max())
isolation=float(20*np.log10(max(selected,1e-10)/max(other_amplitude,1e-10)))
result={'application':'Chrome headless WebAudio','pid':root.pid,'capture_seconds':len(samples)/16000,'selected_amplitude':selected,'other_amplitude':other_amplitude,'isolation_db':isolation,'clean_stop':not thread.is_alive()}
if selected<1 or isolation<35 or thread.is_alive(): raise RuntimeError('Chrome isolation failed: '+json.dumps(result))
(folder/'chrome-result.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result,indent=2))
