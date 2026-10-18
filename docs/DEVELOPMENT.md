# Phát triển

Chạy `scripts/setup.ps1`. Kiểm thử: `.venv\Scripts\python -m pytest -q --basetemp tmp/pytest-local`. Test GUI: `.venv\Scripts\python scripts/verify_ui.py`. Ảnh render trong `tmp/verification/`. Test phần cứng/AI: `.venv\Scripts\python scripts/verify_local.py`; script phát câu mẫu bằng Windows SAPI, thu WASAPI thật và dịch bằng model local. Không chạy khi người dùng đang phát nội dung riêng tư vì loopback thu toàn bộ endpoint.

Build: `scripts/build.ps1`. Inno Setup tùy chọn: compile `installer/Suni.iss` sau khi có `dist/Suni`. Model không gộp vào EXE; model manager lưu ngoài thư mục cài đặt. Đóng gói native ML khá lớn, sử dụng onedir.

Build riêng bộ thu ứng dụng: `scripts/build_audio.ps1` dùng compiler .NET Framework x64 có sẵn trên Windows. Các script setup/build cũng gọi bước này. Bridge được PyInstaller đóng gói trong `_internal/native/windows_audio/`. Không cần C++ SDK trên máy hiện tại. Mã C# chứa khai báo COM ABI tương ứng header Windows; sự khác biệt với stack C++ đề xuất không thay đổi API capture thực.

Kiểm thử process capture: `scripts/verify_process_audio.py` (hai process tone); `scripts/verify_chrome_audio.py` (Chrome thật, profile test riêng + WebAudio); `scripts/verify_process_pipeline.py` (speech English → Việt qua worker và SQLite); `scripts/verify_process_lifecycle.py` (3 Start/Stop khi im lặng, source exit). Script Chrome kết thúc đúng cây tiến trình test do script tạo, không dùng profile người dùng. Thư mục profile test trong tmp/ không tự xóa.

Các bước tiếp theo: kiểm chứng Zoom/Discord thực, quyền elevation/DRM, ducking audio session khi chọn nguồn riêng. Route Replacement cần virtual endpoint thật + rollback và watchdog độc lập. Chỉ enable sau khi probe chứng minh đúng nguồn/đầu ra, kiểm tra echo và khôi phục.

Nguồn API đối chiếu: https://github.com/s0d3s/PyAudioWPatch ; https://github.com/SYSTRAN/faster-whisper ; https://huggingface.co/docs/transformers/v4.50.0/model_doc/nllb .
