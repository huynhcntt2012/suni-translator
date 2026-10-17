"""Endpoint ducking with write-ahead recovery journal; never alter routing."""
import json
import logging
from engine.config import DATA
from engine.audio import endpoint_name

JOURNAL = DATA / "volume-recovery.json"

def recover():
    if not JOURNAL.exists():
        return
    from pycaw.pycaw import AudioUtilities
    saved = json.loads(JOURNAL.read_text(encoding="utf-8"))
    for device in AudioUtilities.GetAllDevices():
        if device.id == saved["id"]:
            device.EndpointVolume.SetMasterVolumeLevelScalar(saved["volume"], None)
            JOURNAL.unlink(missing_ok=True)
            return
    raise RuntimeError("Thiết bị có âm lượng cần khôi phục đang ngắt kết nối; kết nối lại trước khi dùng Voice Overlay.")

class Ducking:
    def __init__(self, name, factor):
        self.name, self.factor = endpoint_name(name), max(0, min(1, factor))
        self.device = None

    def __enter__(self):
        from pycaw.pycaw import AudioUtilities
        recover()
        candidates = [d for d in AudioUtilities.GetAllDevices() if endpoint_name(d.FriendlyName or "") == self.name]
        if len(candidates) != 1:
            raise RuntimeError("Không xác định duy nhất thiết bị để giảm âm lượng; dừng Voice Overlay.")
        self.device = candidates[0]
        self.volume = self.device.EndpointVolume.GetMasterVolumeLevelScalar()
        DATA.mkdir(parents=True, exist_ok=True)
        temporary = JOURNAL.with_suffix(".tmp")
        temporary.write_text(json.dumps({"id": self.device.id, "volume": self.volume}), encoding="utf-8")
        temporary.replace(JOURNAL)
        try:
            self.device.EndpointVolume.SetMasterVolumeLevelScalar(self.volume * self.factor, None)
        except Exception:
            recover()
            raise
        return self

    def __exit__(self, *_):
        # Leave journal intact if disconnected, for recovery on next start.
        try:
            self.device.EndpointVolume.SetMasterVolumeLevelScalar(self.volume, None)
            JOURNAL.unlink(missing_ok=True)
        except Exception:
            logging.exception("Could not restore volume; recovery journal retained")
