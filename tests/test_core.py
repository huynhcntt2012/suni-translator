import queue
import numpy as np
import pytest
from engine.audio import to_mono_16k, validate_voice_route
from engine.config import Settings
from engine.export import to_srt, srt_time
from engine.pipeline import Segment, offer_latest
from storage.database import Database

def test_resample_preserves_frequency_and_duration():
    rate = 44100
    mono = np.sin(2*np.pi*440*np.arange(rate)/rate).astype(np.float32)
    result = to_mono_16k(np.column_stack([mono, mono]), rate)
    assert len(result) == 16000
    assert abs(np.fft.rfftfreq(len(result), 1/16000)[np.argmax(abs(np.fft.rfft(result)))]-440) < 2

def test_voice_refuses_capture_endpoint():
    with pytest.raises(ValueError):
        validate_voice_route("Speakers [Loopback]", "Speakers")
    with pytest.raises(ValueError):
        validate_voice_route("Speakers [Loopback]", "")
    validate_voice_route("Speakers [Loopback]", "USB Headphones")

def test_queue_drops_oldest_under_overload():
    q = queue.Queue(maxsize=2)
    assert not offer_latest(q, 1)
    assert not offer_latest(q, 2)
    assert offer_latest(q, 3)
    assert [q.get(), q.get()] == [2, 3]

def test_sessions_and_settings_survive_restart(tmp_path):
    path = tmp_path / "test.sqlite3"
    db = Database(path)
    settings = Settings(target="ja", font_size=36)
    db.save_settings(settings)
    first, second = db.session("vi", "subtitles"), db.session("ja", "subtitles")
    db.append(first, Segment(0, 2, "en", "Hello", "Xin chào", 0.5))
    db.close()
    reopened = Database(path)
    assert reopened.settings().target == "ja"
    assert len(reopened.history(first)) == 1
    assert reopened.history(second) == []
    assert reopened.history(first)[0][4] == "Xin chào"
    reopened.close()

def test_srt_timestamps_carry_and_unicode():
    assert srt_time(59.9996) == "00:01:00,000"
    result = to_srt([(0, 1.5, "en", "Hello", "Xin chào", 0.2)])
    assert "00:00:00,000 --> 00:00:01,500" in result
    assert "Hello\nXin chào" in result

def test_future_settings_ignored():
    assert Settings.from_dict({"unknown_future_field": True, "target": "ko"}).target == "ko"
