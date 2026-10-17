# Kiến trúc

`apps/desktop`: Qt dashboard, overlay, tab model/lịch sử và signal bridge. Main thread không chạy AI. `engine/audio.py`: PyAudioWPatch gọi PortAudio/WASAPI native, chỉ expose thiết bị loopback và đầu ra WASAPI. Chưa có C++ process loopback riêng.

`engine/pipeline.py`: supervisor inference load Speech → Translator → Voice; sau đó khởi động capture và voice workers. Capture đọc block 100 ms, mono/resample 16 kHz, đẩy queue 40 block. Inference gom khoảng 3 giây, STT + VAD rồi dịch. Phân đoạn có timestamp theo monotonic clock; bỏ phân đoạn quá 20 giây và audio cũ khi queue đầy. Các block không overlap nên không lặp do overlap; không loại câu lặp hợp lệ của người nói. Gap trong capture khiến bộ gom reset.

Voice queue tối đa 3 câu; câu cũ tự hết hạn trước/sau synthesize. TTS ghi WAV trong TemporaryDirectory, resample về định dạng endpoint khi phát; kiểm tra stop giữa các block. Ducking journal được ghi trước khi đổi volume. Exception/Stop đi qua finally và khôi phục. Khóa đơn phiên QLockFile bảo vệ journal.

`storage`: SQLite WAL với RLock, bảng settings/sessions/segments. Mỗi phiên có id riêng. Export chỉ lấy phiên được chọn. `engine/export.py` tạo timestamp SRT chuẩn.

Offline NLLB tải bằng local_files_only. Ollama là adapter HTTP explicit opt-in; không có credential hoặc fallback cloud ngầm. Piper đọc config ngôn ngữ trước khi dùng. STT CUDA init thất bại thì thử CPU; runtime CUDA failure dừng rõ ràng.

Chủ ý an toàn: Replacement bị khóa, không giả lập bằng mute. Route khác endpoint vẫn cần người dùng đảm bảo mixer ngoài không feedback. Inference chưa có hủy cứng; Stop chờ lời gọi model hiện tại kết thúc để tránh giải phóng resource đang sử dụng.
