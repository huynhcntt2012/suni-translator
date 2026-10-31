# Spike P0 — Benchmark ASR (whisper.cpp)

- Ngày: 31/10/2026
- Máy: Intel i5-14400F (10 nhân / 16 luồng), RAM 16 GB, NVIDIA RTX 5060 Ti — **mạnh hơn nhiều so với máy baseline i5-9400 trong SRS**;
  để ước lượng máy yếu, CPU được đo với 2 / 4 / 8 luồng.
- Engine: whisper.cpp qua Whisper.net 1.9.1 (runtime CPU và Vulkan), chạy trong `Suni.AsrWorker` — đo **qua đúng đường của app**
  (VAD cắt đoạn → `AsrWorkerClient` → named pipe → worker), độ trễ là thời gian thực tế phía app.
- Dữ liệu: 4 câu tiếng Anh TTS (`fixtures/audio/tts`) → VAD cắt thành 6 đoạn (16,1 s, câu dài nhất bị cắt ép 2 lần);
  cộng 3 đoạn không lời đưa thẳng vào ASR (3 s nhạc, 2 s im lặng, 3 s tiếng ồn) để thử "ảo giác".
- Tái hiện: `dotnet run --project tools/Suni.AsrBench` → `artifacts/bench/asr-*.md|json`.
- faster-whisper **chưa đo** (máy không có Python); thiết kế giữ nguyên protocol để thêm sau nếu cần.

| Model | File | SHA-256 |
|---|---|---|
| tiny q5_1 | ggml-tiny-q5_1.bin (31 MB) | `818710568DA3CA15689E31A743197B520007872FF9576237BDA97BD1B469C3D7` |
| base q5_1 | ggml-base-q5_1.bin (57 MB) | `422F1AE452ADE6F30A004D7E5C6A43195E4433BC370BF23FAC9CC591F01A8898` |
| small q5_1 | ggml-small-q5_1.bin (181 MB) | `AE85E4A935D7A567BD102FE55AFC16BB595BDB618E11B2FC7591BC08120411BB` |
| large-v3-turbo q5_0 | ggml-large-v3-turbo-q5_0.bin (547 MB) | `394221709CD5AD1F40C46E6031CA61BCE88931E6E088C188294C6D5A55FFA7E2` |

Nguồn: `huggingface.co/ggerganov/whisper.cpp` (MIT).

## Kết quả

Ngôn ngữ = auto trừ khi ghi khác. p50/p95 = thời gian xử lý một đoạn (đoạn 0,9–4,2 s). RTF = tổng thời gian xử lý / tổng thời lượng âm thanh.

| Model | Thiết bị | Luồng | audio_ctx | Nạp | RTF | p50 | p95 | WER | RAM worker |
|---|---|---|---|---|---|---|---|---|---|
| tiny | CPU | 2 | 1500 | 81 ms | 0.63 | 1.69 s | 1.78 s | 0 % | 221 MB |
| tiny | CPU | 4 | 1500 | 78 ms | 0.38 | 1.02 s | 1.08 s | 0 % | 221 MB |
| tiny | CPU | 8 | 1500 | 104 ms | 0.26 | 0.67 s | 0.79 s | 0 % | 221 MB |
| tiny | CPU | 2 | **256** | 64 ms | 0.32 | 0.86 s | 0.92 s | 0 % | 241 MB |
| base | CPU | 2 | 1500 | 131 ms | 1.33 | 3.57 s | 3.63 s | 0 % | 292 MB |
| base | CPU | 4 | 1500 | 81 ms | 0.77 | 2.03 s | 2.14 s | 0 % | 292 MB |
| base | CPU | 8 | 1500 | 85 ms | 0.50 | 1.34 s | 1.40 s | 0 % | 293 MB |
| base | CPU | 2 | **256** | 81 ms | 0.70 | 1.86 s | 1.94 s | 0 % | 292 MB |
| small | CPU | 2 | 1500 | 232 ms | 4.66 | 12.4 s | 13.0 s | 0 % | 643 MB |
| small | CPU | 4 | 1500 | 164 ms | 2.61 | 6.96 s | 7.16 s | 0 % | 649 MB |
| small | CPU | 8 | 1500 | 180 ms | 1.81 | 4.81 s | 5.10 s | 0 % | 650 MB |
| small | CPU | 8 | 1500 | 183 ms | **0.93** | **2.46 s** | 2.57 s | 0 % | 531 MB | ← ngôn ngữ cố định `en`
| small | CPU | 4 | **256** | 164 ms | 1.45 | 3.88 s | 4.01 s | 0 % | 650 MB |
| large-v3-turbo | CPU | 8 | 1500 | 645 ms | 9.04 | 24.0 s | 25.5 s | 0 % | 1000 MB |
| large-v3-turbo | CPU | 8 | 256 | 394 ms | 5.15 | 13.6 s | 15.3 s | 0 % | 1067 MB |
| small | **Vulkan** | 4 | 1500 | 4055 ms* | 0.05 | 0.14 s | 0.17 s | 0 % | 437 MB |
| large-v3-turbo | **Vulkan** | 4 | 1500 | 637 ms | **0.15** | **0.41 s** | 0.43 s | 0 % | 543 MB |
| large-v3-turbo | Vulkan | 4 | 256 | 645 ms | 0.26 | 0.31 s | 2.29 s | **22.5 %** | 418 MB |

\* lần nạp Vulkan đầu tiên biên dịch shader (~4 s); các lần sau ~0,6 s.
Benchmark chạy song song một phần với việc build/test khác trên cùng máy — số CPU có thể lệch vài %.

## Nhận xét

1. **GPU thay đổi hoàn toàn bài toán**: large-v3-turbo trên Vulkan xử lý một đoạn trong ~0,4 s (RTF 0,15) với RAM 0,5 GB.
   Kiểm thử trực tiếp trong app (thu theo PID → VAD → turbo/Vulkan): câu hiện ra ~1 s sau khi người nói dừng.
2. **Chỉ CPU: small không theo kịp thời gian thực** ngay cả 8 luồng của CPU đời mới (RTF 1,8) khi để Auto Detect.
   Trên i5-9400 (6 nhân, cũ hơn ~2 thế hệ) ước tính chỉ **tiny/base** khả dụng.
3. **Auto Detect tốn gấp đôi**: small 8 luồng 4,8 s → 2,5 s khi cố định ngôn ngữ (whisper.cpp chạy thêm một lượt encoder để nhận diện).
   → Triển khai "sticky language" (SDD 4.5): nhận diện vài đoạn đầu rồi khoá ngôn ngữ, chỉ dò lại định kỳ hoặc khi điểm tin cậy giảm.
4. **audio_ctx 256 nhanh gần gấp đôi trên CPU cho tiny/base/small mà không tăng WER** trên bộ dữ liệu này,
   nhưng **làm hỏng large-v3-turbo** (lặp nguyên câu, WER 22,5 %). → Chỉ bật cho tiny/base/small; cần thêm dữ liệu thật trước khi bật mặc định.
5. **Ảo giác thấy thật**: im lặng → `[BLANK_AUDIO]`, nhạc → `(upbeat music)` / `Bye.`, tiếng ồn → câu vô nghĩa với ngôn ngữ `nn`
   và xác suất token 0,08–0,31 (lời thật ≥ 0,53). `AsrResultFilter` sau khi bổ sung loại được **toàn bộ** các trường hợp đã thấy.
6. WER 0 % ở mọi cấu hình (trừ dòng audio_ctx của turbo) vì giọng TTS rất sạch — **chưa phản ánh** chất lượng với giọng người,
   tiếng Nhật/Hàn/Trung/Việt, nhạc nền. Cần bộ fixture thật (SRS 8) trước khi chốt model mặc định cho từng ngôn ngữ.

## Quyết định (cập nhật SDD 4.6)

| Cấu hình máy | Mặc định | Ghi chú |
|---|---|---|
| Có GPU hỗ trợ Vulkan | **large-v3-turbo q5_0, Vulkan** | ~0,4 s/đoạn; worker tự lùi về CPU nếu Vulkan lỗi |
| Chỉ CPU (≥ 8 luồng) | **base q5_1** | ~1,3 s/đoạn auto; nhanh hơn khi có sticky language + audio_ctx 256 |
| CPU yếu (baseline i5-9400) | **tiny q5_1** hoặc base + ngôn ngữ cố định | cần đo trên máy thật |

Engine: **whisper.cpp** (không cần Python, có Vulkan, model q5 nhỏ). faster-whisper chỉ xem xét lại nếu chất lượng tiếng Việt/Nhật của whisper.cpp kém hơn rõ rệt.
