"""Real WASAPI loopback; indices are resolved afresh from saved device names."""
import math
import time
import threading
from contextlib import contextmanager
import numpy as np
from scipy.signal import resample_poly

_audio_lock = threading.RLock()
_audio_instance = None
_audio_users = 0

@contextmanager
def audio_context():
    # PortAudio Initialize/Terminate are process-global and must not race
    # between playback and capture workers. Share one reference-counted owner.
    global _audio_instance, _audio_users
    import pyaudiowpatch as pa
    with _audio_lock:
        if _audio_instance is None:
            _audio_instance = pa.PyAudio()
        _audio_users += 1
        instance = _audio_instance
    try:
        yield instance
    finally:
        with _audio_lock:
            _audio_users -= 1
            if _audio_users == 0:
                _audio_instance.terminate()
                _audio_instance = None

def devices():
    import pyaudiowpatch as pa
    with audio_context() as p:
        host = p.get_host_api_info_by_type(pa.paWASAPI)["index"]
        inputs = list(p.get_loopback_device_info_generator())
        outputs = [p.get_device_info_by_index(i) for i in range(p.get_device_count())]
        outputs = [d for d in outputs if d["hostApi"] == host and d["maxOutputChannels"] > 0 and not d.get("isLoopbackDevice")]
    return inputs, outputs

def endpoint_name(name):
    return name.replace("[Loopback]", "").strip().casefold()

def validate_voice_route(capture_name, output_name):
    if not output_name or endpoint_name(capture_name) == endpoint_name(output_name):
        raise ValueError("Voice Overlay cần thiết bị phát AI khác thiết bị đang thu để tránh vòng lặp. Chọn tai nghe/USB hoặc thiết bị ảo riêng.")

def to_mono_16k(samples, rate):
    mono = samples.mean(axis=1) if samples.ndim > 1 else samples
    divisor = math.gcd(int(rate), 16000)
    return resample_poly(mono, 16000 // divisor, int(rate) // divisor).astype(np.float32)

def capture(name, stop, emit, meter):
    import pyaudiowpatch as pa
    with audio_context() as p:
        found = [d for d in p.get_loopback_device_info_generator() if d["name"] == name]
        if not found:
            raise RuntimeError("Thiết bị thu đã ngắt kết nối. Chọn lại thiết bị và Start.")
        d = found[0]
        rate, channels = int(d["defaultSampleRate"]), int(d["maxInputChannels"])
        frames = rate // 10
        stream = p.open(format=pa.paFloat32, channels=channels, rate=rate, input=True, input_device_index=d["index"], frames_per_buffer=frames)
        try:
            while not stop.is_set():
                raw = stream.read(frames, exception_on_overflow=True)
                data = np.frombuffer(raw, dtype=np.float32).reshape(-1, channels)
                meter(float(np.sqrt(np.mean(data * data))))
                emit(to_mono_16k(data, rate), time.monotonic())
        finally:
            stream.stop_stream()
            stream.close()

def play_wave(path, output_name, stop):
    import wave
    import pyaudiowpatch as pa
    with wave.open(str(path), "rb") as wav, audio_context() as p:
        host = p.get_host_api_info_by_type(pa.paWASAPI)["index"]
        found = [p.get_device_info_by_index(i) for i in range(p.get_device_count())]
        found = [d for d in found if d["name"] == output_name and d["hostApi"] == host and d["maxOutputChannels"] > 0]
        if not found:
            raise RuntimeError("Thiết bị phát AI không còn kết nối.")
        device = found[0]
        # WASAPI commonly rejects a TTS model's native 22.05 kHz/mono format.
        # Convert to the endpoint's actual mix rate and channel layout first.
        width = wav.getsampwidth()
        raw = wav.readframes(wav.getnframes())
        if width == 2:
            samples = np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768
        elif width == 1:
            samples = (np.frombuffer(raw, dtype=np.uint8).astype(np.float32)-128) / 128
        elif width == 4:
            samples = np.frombuffer(raw, dtype='<i4').astype(np.float32) / 2147483648
        else:
            raise ValueError(f"Unsupported WAV sample width: {width}")
        samples = samples.reshape(-1, wav.getnchannels()).mean(axis=1)
        rate = int(device['defaultSampleRate'])
        divisor = math.gcd(rate, wav.getframerate())
        samples = resample_poly(samples, rate//divisor, wav.getframerate()//divisor).astype(np.float32)
        channels = min(2, int(device['maxOutputChannels']))
        samples = np.repeat(samples[:, None], channels, axis=1)
        stream = p.open(format=pa.paFloat32, channels=channels, rate=rate, output=True, output_device_index=device['index'])
        try:
            for offset in range(0, len(samples), 1024):
                if stop.is_set():
                    break
                stream.write(samples[offset:offset+1024].tobytes())
        finally:
            stream.stop_stream()
            stream.close()
