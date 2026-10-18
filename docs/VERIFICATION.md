# Kết quả kiểm tra trên máy triển khai — 18/10/2026

- Windows x64, thiết bị WASAPI Remote Audio; GPU được hệ thống báo GTX 1060 6 GB. AI chạy CPU.
- 9 unit tests pass: resample, guard route, queue, persistence, SRT, schema settings, packet protocol, PID reuse, từ chối cây Suni.
- Qt dashboard, cấu hình, model, lịch sử và overlay dựng/render thành công; chữ Việt đã kiểm tra qua ảnh render.
- Thu thực 9,1 giây từ Remote Audio loopback, RMS 0,0896. Nguồn là Windows SAPI English phát qua endpoint thật.
- Whisper small: “Hello. Welcome to SUNY Live translator. This application translates English into Vietnamese.” NLLB: “Chào mừng đến với SUNY Live Translator. Ứng dụng này dịch tiếng Anh sang tiếng Việt.”
- Nhận diện gồm nạp model 2,828 giây; bước dịch 3,860 giây. Đây là một mẫu, không phải p50/p95.
- Test pipeline worker thực: nhận câu dịch, lưu một segment vào SQLite và dừng sạch. Độ trễ câu 5,922 giây. Phát hiện overload và bỏ block cũ khi cần.
- Đã phát hiện/sửa race native PortAudio init/terminate bằng audio owner dùng chung và khóa vòng đời; test pipeline sau sửa pass.
- Piper vais1000 tiếng Việt: synthesize WAV thành công. Chưa đánh giá chất lượng nghe, chưa test cùng pipeline trên hai endpoint vì phiên này chỉ có Remote Audio.
- Ducking thật trên Remote Audio: giảm xuống 95%, phát sinh exception có kiểm soát, khôi phục từ 1,0 về đúng 1,0 và xóa recovery journal. Chưa thử rút thiết bị giữa thao tác hoặc kill process.
- Bản EXE: self-test thành công với một thiết bị thu/phát, nạp Whisper local, chạy STT trên silence, dịch NLLB sang Việt và dựng/đóng giao diện. Báo cáo tại `%LOCALAPPDATA%/SuniLiveTranslator/self-test.json`; ảnh tại `self-test.png` cùng thư mục.
- Đã sửa xung đột DLL khi đóng gói: loại ICU của Poppler bị lấy nhầm từ PATH; Qt dùng ICU do Windows cung cấp.

Kết quả thô: `tmp/verification/result.json`, `tmp/verification/pipeline-result.json`, `tmp/verification/voice-vi.wav`. Những file này là artifact cục bộ, không đưa vào Git mặc định. Các test dùng speech thực hoặc synthetic speech qua driver thật, không mock capture/STT/translation.

Process capture bổ sung:

- Hai tiến trình Windows phát đồng thời 440 Hz và 1200 Hz; chọn PID nguồn 440 Hz: thu 3,9 giây, mức cách ly tone 103,74 dB, Stop sạch. Đây là phép đo tín hiệu thử, không phải cam kết cách ly cho mọi driver/ứng dụng.
- Chrome headless thật với profile test riêng phát WebAudio từ audio subprocess. Chọn PID Chrome root trong khi process khác phát 1200 Hz: thu 5,9 giây; mức cách ly tone 106,50 dB. Xác nhận include child-process tree, không chỉ PID root.
- Luồng đầy đủ nguồn speech process: Whisper small → NLLB Việt → SQLite → Stop pass. Câu “Hello. Welcome to SUNY Live translator.” thành “Chào mừng đến với SUNY Live Translator.”; độ trễ mẫu 3,706 giây.
- 3 lần Start/Stop nguồn im lặng, Stop ~15–31 ms. Nguồn đóng giữa phiên được phát hiện và báo `Target process exited`; không rơi sang thu toàn hệ thống.
- File thô: `tmp/verification/process-result.json`, `chrome-result.json`, `process-pipeline-result.json`, `process-lifecycle.json`.

Chưa nghiệm thu: YouTube thực, Zoom/Discord thực, unplug/replug driver, độ trễ dài hạn, CUDA và Replacement. Tự nhận diện ngôn ngữ ở bài thử đầu có nhầm sang Việt; kiểm thử thành công dùng nguồn English tường minh.
