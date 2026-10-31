# Suni AI Live Translator

Ứng dụng Windows 11 thu âm thanh từ một app (Chrome, Discord, Zoom…), nhận dạng giọng nói offline,
dịch bằng Ollama (PC hoặc server LAN) và hiển thị phụ đề song ngữ.

- Đặc tả: `Suni_AI_Live_Translator_Dev_Spec_v1.0.docx`
- Thiết kế: [docs/SOFTWARE_DESIGN.md](docs/SOFTWARE_DESIGN.md)

## Yêu cầu build

- .NET SDK 10 (không cần C++ toolchain)

## Build & test

```powershell
dotnet build SuniTranslator.slnx
dotnet test SuniTranslator.slnx
./build/build.ps1 -Publish   # build + test + publish win-x64
```

Bản đóng gói (self-contained, máy đích không cần cài .NET): `artifacts/publish/SuniTranslator/Suni.App.exe`
kèm ASR worker (`workers/whisper`), Silero VAD và model whisper chọn bằng `-Models`
(mặc định `large-v3-turbo-q5_0`, `base-q5_1`, `tiny-q5_1` — lấy từ `models/asr/whisper.cpp/` của repo). Khoảng 970 MB.
Copy cả thư mục sang máy khác là chạy được; vẫn cần Ollama (máy đó hoặc LAN) để dịch.

Thử thu âm (spike P0):

```powershell
dotnet run --project tools/Suni.CaptureSpike -- list
dotnet run --project tools/Suni.CaptureSpike -- capture --pid <PID> --seconds 10 --out test.wav
./tools/Suni.CaptureSpike/run-isolation-test.ps1   # tự kiểm cách ly 2 tiến trình (phát tiếng bíp nhỏ)
dotnet run --project tools/Suni.CaptureSpike -- vad --pid <PID> --seconds 30 --dump vad-out   # VAD trực tiếp
dotnet run --project tools/Suni.CaptureSpike -- live --pid <PID> --seconds 30 --ollama http://127.0.0.1:11434 --llm qwen3.5:9b   # như app
dotnet run --project tools/Suni.AsrBench                                                      # benchmark ASR
```

## Cấu trúc

| Thư mục | Vai trò |
|---|---|
| `src/Suni.Core` | Domain (Segment, Session, state machine), interfaces, sự kiện, cấu hình — không phụ thuộc IO |
| `src/Suni.Audio` | WASAPI process/endpoint loopback (C# COM interop), liệt kê nguồn, ring buffer, đo mức, WAV, downsample 48k→16k, Silero VAD, cắt đoạn lời nói |
| `models/vad` | Silero VAD v5.1.2 ONNX (MIT) — đi kèm installer |
| `fixtures/audio` | Fixture giọng nói (sinh bằng SAPI offline) cho test |
| `tools/Suni.CaptureSpike` | CLI thu âm / đo tín hiệu / chạy VAD — xem `docs/spikes/` |
| `src/Suni.Asr` | Protocol named pipe với ASR worker; (P1) client + supervisor |
| `src/Suni.AsrWorker` | Worker whisper.cpp (hiện chỉ có handshake) |
| `src/Suni.Translation` | Ollama client, prompt, parser output, retry |
| `src/Suni.Storage` | SQLite schema/migration, xuất TXT/SRT/JSON |
| `src/Suni.Pipeline` | Event bus, sequencer; (P1) orchestrator, channels, backpressure |
| `src/Suni.Tts` | Interface TTS (P2) |
| `src/Suni.App` | WPF: composition root, Dashboard |
| `tests/*` | xUnit |

## Trạng thái

Khung solution. Đã có và có test: domain + state machine, sequencer, event bus, Ollama translation engine
(prompt, structured output, retry, parser), phân loại URL Local/LAN, schema SQLite + FTS, xuất SRT/TXT/JSON,
ring buffer, level meter, protocol pipe ASR, thu âm theo PID / toàn hệ thống trừ chính mình / endpoint
(đã kiểm chứng trên máy thật), liệt kê audio session gom theo tiến trình gốc, downsample + Silero VAD + cắt đoạn
(kiểm chứng với giọng nói và nhạc).

**App chạy được**: chọn ứng dụng nguồn → (Kết nối Ollama) → Start → thu âm → VAD → whisper.cpp (GPU Vulkan hoặc CPU)
→ hiện câu gốc → dịch bằng Ollama (máy này hoặc LAN) → hiện bản dịch dưới câu gốc. Cài đặt được nhớ giữa các lần mở
(`%LOCALAPPDATA%\Suni\settings.json`). Model ASR đặt trong `models/asr/whisper.cpp/` (xem `docs/spikes/P0-asr.md`).
Mỗi phiên được lưu vào SQLite (`%LOCALAPPDATA%\Suni\data\suni.db`); màn **History** cho tìm, xem, copy, xuất
TXT/SRT/JSON, xoá. **Overlay** phụ đề nổi trên ứng dụng khác (nút Overlay, Ctrl+Alt+O; khoá chuột-xuyên-qua Ctrl+Alt+L;
Start/Stop Ctrl+Alt+S — đổi được trong **Settings**). Icon ở **khay hệ thống**: đóng cửa sổ khi đang dịch thì app chạy nền,
có thông báo khi mất nguồn/mất kết nối. Trong History sửa được bản dịch bằng tay (nháy đúp / ✎, hoàn tác ↺);
file xuất dùng bản đã sửa. **Đọc bản dịch** bằng giọng offline của Windows (ô "Đọc bản dịch"; giọng, thiết bị phát,
tốc độ trong Settings; giọng tiếng Việt cần cài thêm trong Windows Settings → Speech).

Chưa làm (đánh dấu `TODO(P0)`/`TODO(P1)` trong code): tích hợp whisper.cpp, orchestrator, repositories, overlay, history, model manager, settings, installer.
