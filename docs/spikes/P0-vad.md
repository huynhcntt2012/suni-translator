# Spike P0 — Resample + VAD

- Ngày: 31/10/2026, cùng máy với [P0-audio-capture.md](P0-audio-capture.md)
- Code: `src/Suni.Audio/Processing/` (`Downsampler`, `SileroVad`, `VadSegmenter`)
- Model: Silero VAD **v5.1.2** (MIT), `models/vad/silero_vad.onnx`, 2 327 524 byte,
  SHA-256 `2623A2953F6FF3D2C1E61740C6CDB7168133479B267DFEF114A4A3CC5BDD788F`,
  nguồn `github.com/snakers4/silero-vad/raw/v5.1.2/src/silero_vad/data/silero_vad.onnx`
- Runtime: `Microsoft.ML.OnnxRuntime` 1.31.1, CPU, 1 luồng
- Fixture giọng nói: 4 câu tiếng Anh do Windows SAPI (Zira) sinh offline — `fixtures/audio/make-tts-fixtures.ps1`

## Kết quả

### Downsampler 48 kHz stereo → 16 kHz mono (unit test)

| Kiểm tra | Kết quả |
|---|---|
| 100 Hz – 6,5 kHz | lệch ≤ 0,5 dB |
| 9 / 12 / 20 kHz (sẽ bị gập phổ nếu không lọc) | < -60 dB |
| Chia khối ngẫu nhiên vs một lần | giống hệt từng mẫu |

### Chuỗi đầy đủ trên timeline có câu nói + im lặng + nhạc (`SileroVadTests`)

Timeline 16 kHz được upsample lên 48 kHz stereo rồi đưa qua chuỗi thật theo gói 10 ms.

| Vùng chuẩn | ms | Đoạn VAD giữ lại |
|---|---|---|
| en_1 | 1104–3408 | 952–3606 |
| en_2 | 5475–9109 | 5368–9552 (cắt ép ở 4 s, phần đuôi chỉ là overlap → không gửi) |
| nhạc tổng hợp 3 s | 10660–13660 | **không có** |
| en_3 ("Press start.") | 14570–15304 | 14456–15510 |
| en_4 (câu dài 7,3 s) | 17181–24469 | 16984–20656 · 20356–24016 · 23716–24630 (2 lần cắt ép, chồng 300 ms) |

- Mỗi câu được phủ 100 %, không mất chữ ở đầu (pre-roll) hay cuối.
- 0 ms nhạc lọt vào ASR — Silero tự cho xác suất thấp, MusicGate chưa phải dùng tới.

### Thu trực tiếp (`Suni.CaptureSpike vad --pid`)

PowerShell phát en_2 rồi en_4 qua loa; thu theo PID, chạy VAD thời gian thực:

| Chỉ số | Giá trị |
|---|---|
| Độ trễ phát đoạn (từ cuối đoạn tới lúc segmenter trả ra) | ~360 ms khi người nói dừng; 370–530 ms khi cắt ép |
| Tốc độ VAD | RTF 0,018–0,020 (~50× thời gian thực, ~2 % một lõi) |
| Nguồn thoát giữa chừng | `SourceLost` → dừng sạch, đoạn cuối vẫn được flush |

Độ trễ ~360 ms chủ yếu do `min_silence_ms` = 500 trừ `post_roll_ms` = 150: đây là núm chỉnh
giữa "cắt sớm" và "cắt giữa câu". Ngân sách end-to-end p50 ≤ 5 s (SRS 7) còn ~4,6 s cho ASR + LLM.

## Chưa kiểm chứng

- Giọng người thật, nhiều người nói chồng, nhạc có lời, tiếng game, tiếng Nhật/Hàn/Trung/Việt — cần bộ fixture thực tế (SRS 8).
- Đặt giá trị `min_silence_ms` theo profile (họp 600–800 ms, phim 400 ms) — FR-26.
- Resampler cho tần số không chia hết (44,1 kHz) khi nhập file — FR-13.
