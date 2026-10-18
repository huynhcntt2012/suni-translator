"""Process-tree loopback via an isolated Windows Core Audio bridge."""
from pathlib import Path
import os
import struct
import subprocess
import sys
import threading
import time
import numpy as np
import psutil
from engine.audio import to_mono_16k

def helper_path():
    root = Path(getattr(sys, '_MEIPASS', Path(__file__).resolve().parents[1]))
    return root / 'native' / 'windows_audio' / 'SuniProcessLoopback.exe'

def capability_error():
    if sys.platform != 'win32' or sys.getwindowsversion().build < 20348:
        return 'Thu riêng ứng dụng cần Windows build 20348 trở lên.'
    if not helper_path().is_file():
        return 'Thiếu bộ thu tiến trình. Chạy scripts/build_audio.ps1 hoặc cài lại bản đầy đủ.'
    return ''

def validate_process(pid, created):
    process = psutil.Process(pid)
    if abs(process.create_time() - created) > 0.01:
        raise RuntimeError('PID đã được Windows dùng lại. Làm mới và chọn lại ứng dụng.')
    protected = {os.getpid()} | {p.pid for p in psutil.Process().parents()}
    if pid in protected:
        raise RuntimeError('Không thu Suni hoặc tiến trình cha của Suni để tránh thu lại giọng AI.')
    return process

def applications():
    """Group same-executable children under their running root (Chrome/Discord)."""
    own = psutil.Process()
    excluded = {own.pid} | {p.pid for p in own.parents()}
    session_user = own.username().casefold()
    processes = {}
    for process in psutil.process_iter(['pid', 'ppid', 'name', 'create_time', 'username']):
        data = process.info
        if data['pid'] in excluded or not data['name'] or not data['username'] or data['username'].casefold() != session_user:
            continue
        processes[data['pid']] = data
    roots = []
    for pid, data in processes.items():
        parent = processes.get(data['ppid'])
        if parent and parent['name'].casefold() == data['name'].casefold():
            continue
        roots.append(data)
    preferred = ('chrome.exe','zoom.exe','discord.exe','msedge.exe','vlc.exe','firefox.exe','teams.exe','ms-teams.exe')
    return sorted(roots, key=lambda p: (p['name'].casefold() not in preferred, p['name'].casefold(), p['pid']))

def read_exact(stream, length):
    result = bytearray()
    while len(result) < length:
        part = stream.read(length - len(result))
        if not part:
            raise EOFError('Bộ thu tiến trình đã đóng kết nối.')
        result.extend(part)
    return bytes(result)

def capture_process(pid, created, stop, emit, meter, ready=lambda: None):
    error = capability_error()
    if error:
        raise RuntimeError(error)
    try:
        target = validate_process(pid, created)
    except (psutil.NoSuchProcess, psutil.AccessDenied) as exc:
        raise RuntimeError('Ứng dụng đã đóng hoặc không đủ quyền truy cập; chọn lại tiến trình.') from exc
    child = subprocess.Popen([str(helper_path()), str(pid)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    completed = threading.Event()
    def watch_stop():
        while not completed.wait(0.1):
            if stop.is_set():
                try:
                    child.stdin.write(b'\n')
                    child.stdin.flush()
                    child.wait(timeout=2)
                except (OSError, subprocess.TimeoutExpired):
                    child.terminate()
                return
    watcher = threading.Thread(target=watch_stop, name='suni-process-stop', daemon=True)
    watcher.start()
    try:
        magic, rate, channels, bits = struct.unpack('<4sIHH', read_exact(child.stdout, 12))
        if (magic, rate, channels, bits) != (b'SLP1', 44100, 2, 16):
            raise RuntimeError('Sai định dạng giao thức bộ thu tiến trình.')
        ready()
        parts, total = [], 0
        clock_offset = time.monotonic()-time.perf_counter()
        while not stop.is_set():
            frames, qpc, flags = struct.unpack('<IQI', read_exact(child.stdout, 16))
            if frames == 0 or frames > rate * 2:
                raise RuntimeError('Kích thước gói audio không hợp lệ.')
            pcm = read_exact(child.stdout, frames * channels * 2)
            samples = np.frombuffer(pcm, dtype='<i2').reshape(-1, channels).astype(np.float32) / 32768
            data = to_mono_16k(samples, rate)
            stamp = qpc/10_000_000 + frames/rate + clock_offset if qpc and not flags & 4 else time.monotonic()
            if flags & 1:  # Discontinuity: never join samples across a drop.
                parts, total = [], 0
            parts.append(data)
            total += len(data)
            if total >= 1600:
                chunk = np.concatenate(parts)
                meter(float(np.sqrt(np.mean(chunk*chunk))))
                emit(chunk, stamp)
                parts, total = [], 0
    except EOFError as exc:
        if not stop.is_set():
            try:
                child.wait(timeout=2)
            except subprocess.TimeoutExpired:
                child.terminate()
            details = child.stderr.read(8192).decode('utf-8', errors='replace').strip()
            raise RuntimeError(f'Thu riêng PID {pid}: {details or str(exc)}') from exc
    finally:
        completed.set()
        if child.poll() is None:
            try:
                child.stdin.write(b'\n')
                child.stdin.flush()
                child.wait(timeout=2)
            except (OSError, subprocess.TimeoutExpired):
                child.terminate()
                child.wait(timeout=2)
        watcher.join(timeout=3)
        for stream in (child.stdin, child.stdout, child.stderr):
            try:
                stream.close()
            except OSError:
                pass  # Windows may report a broken pipe again during flush/close.
