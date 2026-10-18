# Suni Live Translator

Repository: [huynhcntt2012/suni-translator](https://github.com/huynhcntt2012/suni-translator).

Ứng dụng desktop Windows theo tài liệu Suni Live Translator Dev Brief 1.0. Đây là bản triển khai MVP 0.1, chưa phải bản nghiệm thu đủ tám phase.

## Chạy trên máy này

Nhấp đúp `Start-Suni.cmd`, hoặc chạy `.venv\Scripts\python.exe run.py` trong thư mục dự án. Bản đóng gói nằm tại `dist/Suni/Suni.exe`; giữ nguyên thư mục `_internal` cạnh EXE. Model Whisper small, NLLB và giọng Piper Việt vais1000 được tải vào `%LOCALAPPDATA%\SuniLiveTranslator\models` trên máy triển khai. Model không nằm trong bộ EXE để tránh nhân đôi dữ liệu.

1. Chọn phạm vi **Toàn bộ thiết bị** rồi chọn thiết bị System Audio; hoặc chọn **Ứng dụng riêng** rồi chọn Chrome/Zoom/Discord và PID tương ứng.
2. Chọn ngôn ngữ nguồn (nên chọn English khi nguồn đã biết) và đích Tiếng Việt.
3. Giữ **Live Subtitles**, bấm **Bắt đầu phiên dịch**. Lần đầu chờ nạp model.
4. Trong Cấu hình, bật mở khóa phụ đề để kéo vị trí, tắt để khôi phục click-through.
5. Bấm Dừng trước khi đổi thiết bị/provider. Lịch sử lưu cục bộ và xuất được SRT/CSV theo từng phiên.

Khi thu riêng ứng dụng, chọn tiến trình gốc để bao gồm các tiến trình con (đặc biệt với Chrome/Discord). Bấm **Làm mới thiết bị** để đồng thời làm mới danh sách ứng dụng. Nếu ứng dụng đã khởi động lại, chọn lại PID; Suni kiểm tra thời điểm tạo tiến trình để tránh dùng PID cũ. Thu theo tiến trình không phụ thuộc endpoint phát. Nguồn riêng hiện hỗ trợ **Live Subtitles**; Voice Overlay với ducking riêng audio session sẽ bổ sung sau.

## Cài máy khác

Windows 11 x64, Python 3.10–3.12. Chạy `powershell -ExecutionPolicy Bypass -File scripts/setup.ps1`, sau đó `Start-Suni.cmd`. Tải model trong tab Model AI. CPU chạy được; CUDA cần bản CTranslate2/CUDA/cuDNN tương thích, tự fallback CPU khi khởi tạo thất bại. NLLB chạy CPU. Không mặc định tải model trong lúc Start.

## Tính năng có code thực

- WASAPI loopback theo thiết bị hoặc theo cây tiến trình; waveform RMS từ âm thanh thực, resample 16 kHz.
- Faster-whisper + Silero VAD, tự nhận diện ngôn ngữ hoặc chọn nguồn, timestamp.
- NLLB offline; adapter HTTP Ollama có URL/model cấu hình.
- Phụ đề always-on-top, click-through, song ngữ, cỡ chữ và vị trí lưu lại.
- Piper TTS từ model người dùng cung cấp, kiểm tra ngôn ngữ giọng; phát qua thiết bị riêng và ducking thiết bị nguồn.
- SQLite cấu hình/lịch sử; SRT/CSV; system tray; Ctrl+Shift+S và Ctrl+Shift+O khi cửa sổ ứng dụng có focus.
- Worker capture/inference/TTS, queue giới hạn, loại dữ liệu quá cũ, log STT/dịch/TTS/end-to-end.
- Nhật ký khôi phục âm lượng: Stop và thoát bình thường khôi phục ngay; crash khôi phục khi mở lại ứng dụng.

## Voice Overlay

Chọn Piper `.onnx` và file `.onnx.json` nằm cạnh nhau, đúng ngôn ngữ đích. Chọn thiết bị phát AI **khác** thiết bị thu. Ví dụ nguồn phát qua loa, AI phát qua USB headphone; hoặc cấu hình thiết bị ảo và monitor riêng do người dùng kiểm chứng. Không nối đường phát AI vào đường thu. Kiểm tra tên endpoint chỉ ngăn trùng thiết bị; không chứng minh topology virtual mixer bên ngoài. Nếu tên Core Audio không khớp duy nhất, ứng dụng dừng thay vì chỉnh sai âm lượng.

Ducking hiện áp dụng toàn bộ endpoint nguồn, không riêng một ứng dụng. Một số driver có thể làm thay đổi cả mức loopback. Không có AEC, không hỗ trợ phát AI an toàn trên cùng endpoint trong bản này. Giọng Việt vais1000 đã tải và synthesize thành WAV trên máy triển khai; chất lượng nghe và Voice Overlay hai endpoint vẫn cần nghiệm thu riêng. Nguồn giọng: https://huggingface.co/rhasspy/piper-voices/tree/main/vi/vi_VN/vais1000/medium .

## Phạm vi chưa hoàn thành

- Voice Replacement, virtual-device provisioning, tách giọng/nhạc nền.
- Voice Overlay khi chọn nguồn tiến trình; kiểm chứng Zoom/Discord và các trường hợp đặc quyền/DRM.
- Hotkey toàn hệ thống; thống kê GPU trực tiếp; cloud STT/TTS có xác thực.
- Tự phục hồi thiết bị đang chạy: hiện báo lỗi, dừng và yêu cầu làm mới/Start.
- Watchdog khôi phục volume ngay sau crash: hiện khôi phục ở lần khởi động sau.
- Benchmark RTX 5060 Ti, kiểm tra Chrome/YouTube thực, kiểm tra rút/cắm thiết bị và giọng Việt hai endpoint.

Phân đoạn cố định 3 giây có thể cắt câu. STT lọc VAD trong mỗi phân đoạn; không phải decoder streaming incremental. Auto-language có thể sai với đoạn ngắn hoặc tiếng ồn. Chọn ngôn ngữ nguồn giúp ổn định. Không cam kết độ trễ 1–3 giây.

## Dữ liệu và giấy phép

Audio chỉ ở RAM/trong tệp tạm TTS; không lưu thu âm phiên. Văn bản, bản dịch và cấu hình được lưu SQLite không mã hóa trong hồ sơ người dùng. Ollama gửi văn bản tới URL đã cấu hình khi chọn adapter đó. Log ghi trạng thái/benchmark, không ghi nội dung câu dịch. Model tải từ Hugging Face. NLLB-200 dùng CC-BY-NC-4.0: cần thay model hoặc xử lý quyền trước khi dùng thương mại. Kiểm tra thêm giấy phép Piper/model giọng và các dependency trước khi phân phối.

Xem `docs/ARCHITECTURE.md`, `docs/TESTING.md`, `docs/RELEASE.md`.
