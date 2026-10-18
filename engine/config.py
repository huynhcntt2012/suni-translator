from dataclasses import dataclass, asdict
from pathlib import Path
import os

DATA = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "SuniLiveTranslator"
LANGUAGES = {"vi": "Tiếng Việt", "en": "English", "zh": "中文", "ja": "日本語", "ko": "한국어"}
NLLB_CODES = {"vi": "vie_Latn", "en": "eng_Latn", "zh": "zho_Hans", "ja": "jpn_Jpan", "ko": "kor_Hang", "fr": "fra_Latn", "de": "deu_Latn", "es": "spa_Latn", "ru": "rus_Cyrl", "th": "tha_Thai", "id": "ind_Latn"}

@dataclass
class Settings:
    capture_kind: str = "system"
    process_pid: int = 0
    process_created: float = 0.0
    source: str = "auto"
    target: str = "vi"
    mode: str = "subtitles"
    capture_name: str = ""
    output_name: str = ""
    compute: str = "cpu"
    whisper: str = "small"
    translation: str = "nllb"
    ollama_url: str = "http://localhost:11434"
    ollama_model: str = "qwen2.5:7b"
    voice: str = ""
    bilingual: bool = True
    font_size: int = 28
    subtitle_seconds: int = 10
    chunk_seconds: float = 3.0
    max_age: float = 20.0
    duck: float = 0.3
    overlay_x: int = -1
    overlay_y: int = -1

    def dict(self):
        return asdict(self)

    @classmethod
    def from_dict(cls, values):
        return cls(**{k: v for k, v in values.items() if k in cls.__dataclass_fields__})
