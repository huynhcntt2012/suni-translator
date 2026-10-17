# Phát triển

Chạy `scripts/setup.ps1`. Kiểm thử: `.venv\Scripts\python -m pytest -q --basetemp tmp/pytest-local`. Test GUI: `.venv\Scripts\python scripts/verify_ui.py`. Ảnh render trong `tmp/verification/`. Test phần cứng/AI: `.venv\Scripts\python scripts/verify_local.py`; script phát câu mẫu bằng Windows SAPI, thu WASAPI thật và dịch bằng model local. Không chạy khi người dùng đang phát nội dung riêng tư vì loopback thu toàn bộ endpoint.

Build: `scripts/build.ps1`. Inno Setup tùy chọn: compile `installer/Suni.iss` sau khi có `dist/Suni`. Model không gộp vào EXE; model manager lưu ngoài thư mục cài đặt. Đóng gói native ML khá lớn, sử dụng onedir.

Các bước tiếp theo: C++ ActivateAudioInterfaceAsync/process loopback với INCLUDE_TARGET_PROCESS_TREE, giao thức PCM timestamp qua named pipe, kiểm tra Windows build và quyền. Route Replacement cần virtual endpoint thật + rollback và watchdog độc lập. Chỉ enable UI sau khi probe chứng minh đúng nguồn/đầu ra, kiểm tra echo và khôi phục.

Nguồn API đối chiếu: https://github.com/s0d3s/PyAudioWPatch ; https://github.com/SYSTRAN/faster-whisper ; https://huggingface.co/docs/transformers/v4.50.0/model_doc/nllb .
