"""Real local checks, no audio/provider mocks. Run from repository root."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import json
import threading
import time
import wave
import numpy as np
from engine.audio import capture, devices, play_wave
from engine.config import Settings
from engine.providers import Speech, Translator

def main():
    folder = Path("tmp/verification")
    folder.mkdir(parents=True, exist_ok=True)
    inputs, outputs = devices()
    if not inputs or not outputs:
        raise RuntimeError("No real WASAPI endpoint")
    # Windows' installed speech voice produces a real input recording.
    import comtypes.client
    voice = comtypes.client.CreateObject("SAPI.SpVoice")
    english = [v for v in voice.GetVoices() if v.GetAttribute('Language').lower().startswith('409')]
    if english:
        voice.Voice = english[0]
    stream = comtypes.client.CreateObject("SAPI.SpFileStream")
    speech_file = (folder / "source.wav").resolve()
    stream.Open(str(speech_file), 3)
    voice.AudioOutputStream = stream
    voice.Speak("Hello. Welcome to Suni Live Translator. This application translates English into Vietnamese.")
    stream.Close()
    stop = threading.Event()
    blocks, errors = [], []
    def record():
        try:
            capture(inputs[0]["name"], stop, lambda data, stamp: blocks.append(data), lambda _: None)
        except Exception as exc:
            errors.append(str(exc))
    thread = threading.Thread(target=record)
    thread.start()
    time.sleep(0.5)
    try:
        play_wave(speech_file, outputs[0]["name"], stop)
        time.sleep(0.5)
    finally:
        stop.set()
        thread.join(timeout=10)
    if errors or not blocks:
        raise RuntimeError(str(errors))
    samples = np.concatenate(blocks)
    settings = Settings(source="en")
    started = time.monotonic()
    speech = Speech(settings, print)
    segments, language = speech.transcribe(samples)
    original = " ".join(segment.text for segment in segments).strip()
    stt_seconds = time.monotonic()-started
    if not original:
        raise RuntimeError("Real capture returned no recognized speech")
    translator = Translator(settings)
    started = time.monotonic()
    translated = translator.translate(original, language)
    if translated == original:
        raise RuntimeError("Translation did not change the English source")
    result = {"input": inputs[0]["name"], "output": outputs[0]["name"], "capture_seconds": len(samples)/16000, "rms": float(np.sqrt(np.mean(samples*samples))), "language": language, "original": original, "translated": translated, "stt_including_load_seconds": stt_seconds, "translation_seconds": time.monotonic()-started}
    (folder / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    main()
