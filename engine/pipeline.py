import logging
import queue
import threading
import time
import tempfile
from dataclasses import dataclass
from pathlib import Path
import numpy as np
from engine import audio
from engine.providers import Speech, Translator, Voice

log = logging.getLogger(__name__)

@dataclass
class Segment:
    start: float
    end: float
    language: str
    original: str
    translated: str
    latency: float

def offer_latest(q, item):
    try:
        q.put_nowait(item)
        return False
    except queue.Full:
        try:
            q.get_nowait()
        except queue.Empty:
            pass
        q.put_nowait(item)
        return True

class Pipeline:
    def __init__(self, settings, db, on_segment, status, meter, finished):
        self.settings, self.db = settings, db
        self.on_segment, self.status, self.meter, self.finished = on_segment, status, meter, finished
        self.stop_event = threading.Event()
        self.audio_queue = queue.Queue(maxsize=40)
        self.voice_queue = queue.Queue(maxsize=3)
        self.thread = None
        self.session_id = None
        self.last_overload_notice = 0

    def start(self):
        if self.thread and self.thread.is_alive():
            return
        self.thread = threading.Thread(target=self.run, name="suni-inference", daemon=True)
        self.thread.start()

    def stop(self):
        self.stop_event.set()

    def fail(self, exc):
        log.exception("Pipeline failed")
        self.status(f"Lỗi: {exc}")
        self.stop_event.set()

    def capture_worker(self):
        try:
            if self.settings.capture_kind == 'process':
                from engine.process_audio import capture_process
                capture_process(self.settings.process_pid, self.settings.process_created, self.stop_event, self.receive, self.meter,
                    lambda: self.status(f'Đang nghe riêng PID {self.settings.process_pid} và tiến trình con'))
            else:
                audio.capture(self.settings.capture_name, self.stop_event, self.receive, self.meter)
        except Exception as exc:
            self.fail(exc)

    def receive(self, data, timestamp):
        if offer_latest(self.audio_queue, (data, timestamp)) and timestamp - self.last_overload_notice > 5:
            self.last_overload_notice = timestamp
            self.status("Xử lý chậm: bỏ audio cũ để giữ độ trễ có giới hạn.")

    def voice_worker(self, voice):
        try:
            # COM objects must be created and used on the same thread.
            import comtypes
            comtypes.CoInitialize()
            from engine.ducking import Ducking
            with tempfile.TemporaryDirectory(prefix="suni-") as folder:
                while not self.stop_event.is_set():
                    try:
                        text, timestamp = self.voice_queue.get(timeout=0.2)
                    except queue.Empty:
                        continue
                    if time.monotonic() - timestamp > self.settings.max_age:
                        continue
                    path = Path(folder) / "voice.wav"
                    started = time.monotonic()
                    voice.synthesize(text, path)
                    log.info("tts_ms=%.0f", (time.monotonic() - started) * 1000)
                    if self.stop_event.is_set() or time.monotonic() - timestamp > self.settings.max_age:
                        continue
                    with Ducking(self.settings.capture_name, self.settings.duck):
                        audio.play_wave(path, self.settings.output_name, self.stop_event)
        except Exception as exc:
            self.fail(exc)
        finally:
            comtypes.CoUninitialize()

    def run(self):
        capture_thread = voice_thread = None
        try:
            s = self.settings
            if s.mode == "replacement":
                raise ValueError("Voice Replacement chưa có routing cô lập đã kiểm chứng.")
            if s.mode == "voice":
                if s.capture_kind == 'process':
                    raise ValueError('Nguồn riêng từng ứng dụng hiện hỗ trợ Live Subtitles. Voice Overlay cần bổ sung ducking đúng audio session của tiến trình.')
                audio.validate_voice_route(s.capture_name, s.output_name)
            if s.capture_kind == 'process':
                from engine.process_audio import validate_process, capability_error
                error = capability_error()
                if error:
                    raise ValueError(error)
                validate_process(s.process_pid, s.process_created)
            self.status("Đang nạp model cục bộ…")
            speech = Speech(s, self.status)
            if self.stop_event.is_set():
                return
            translator = Translator(s)
            voice = Voice(s.voice, s.target) if s.mode == "voice" else None
            if self.stop_event.is_set():
                return
            self.session_id = self.db.session(s.target, s.mode)
            origin = time.monotonic()
            capture_thread = threading.Thread(target=self.capture_worker, name="suni-capture", daemon=True)
            capture_thread.start()
            if voice:
                voice_thread = threading.Thread(target=self.voice_worker, args=(voice,), name="suni-voice", daemon=True)
                voice_thread.start()
            self.status("Đang mở nguồn thu tiến trình…" if s.capture_kind == 'process' else "Đang nghe • WASAPI loopback")
            chunks, size, begin, previous_end = [], 0, None, 0
            while not self.stop_event.is_set():
                try:
                    data, stamp = self.audio_queue.get(timeout=0.2)
                except queue.Empty:
                    continue
                # A gap means frames were dropped: do not join unrelated audio.
                if chunks and stamp - previous_end > 0.25:
                    chunks, size, begin = [], 0, None
                previous_end = stamp
                if begin is None:
                    begin = stamp - len(data) / 16000
                chunks.append(data)
                size += len(data)
                if size < int(s.chunk_seconds * 16000):
                    continue
                samples, chunk_start = np.concatenate(chunks), begin
                chunks, size, begin = [], 0, None
                if time.monotonic() - stamp > s.max_age:
                    continue
                started = time.monotonic()
                parts, language = speech.transcribe(samples)
                log.info("stt_ms=%.0f audio_seconds=%.2f", (time.monotonic() - started) * 1000, len(samples) / 16000)
                for part in parts:
                    if self.stop_event.is_set():
                        break
                    original = part.text.strip()
                    if not original or part.no_speech_prob > 0.8:
                        continue
                    started = time.monotonic()
                    translated = translator.translate(original, language)
                    now = time.monotonic()
                    log.info("translation_ms=%.0f end_to_end_ms=%.0f", (now-started)*1000, (now-chunk_start-part.end)*1000)
                    if self.stop_event.is_set() or now - (chunk_start + part.end) > s.max_age:
                        continue
                    segment = Segment(max(0, chunk_start-origin+part.start), max(0, chunk_start-origin+part.end), language, original, translated, max(0, now-chunk_start-part.end))
                    self.db.append(self.session_id, segment)
                    self.on_segment(segment)
                    if voice:
                        offer_latest(self.voice_queue, (translated, chunk_start + part.end))
        except Exception as exc:
            self.fail(exc)
        finally:
            self.stop_event.set()
            for thread in (capture_thread, voice_thread):
                if thread:
                    thread.join()
            self.meter(0)
            self.finished()
