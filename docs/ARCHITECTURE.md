# Kiến trúc

`apps/desktop`: Qt dashboard, overlay, tab model/lịch sử và signal bridge. Main thread không chạy AI. `engine/audio.py`: PyAudioWPatch gọi PortAudio/WASAPI native cho thiết bị loopback và đầu ra WASAPI. `engine/process_audio.py` quản lý nguồn riêng ứng dụng.

Process loopback: `native/windows_audio/ProcessLoopback.cs` gọi Windows Core Audio qua COM/PInvoke, biên dịch x64 thành bridge .NET Framework 4.x (có trên Windows 11), không cần cài .NET SDK để chạy. Bridge riêng dùng ActivateAudioInterfaceAsync với INCLUDE_TARGET_PROCESS_TREE, callback agile/MTA, event-driven IAudioCaptureClient. Không thay routing hoặc volume. Kiểm tra build Windows, PID + thời điểm tạo trước khi Start; từ chối Suni và các tiến trình cha để bảo vệ giọng AI. Danh sách ứng dụng nhóm tiến trình con cùng executable dưới root.

Giao thức pipe SLP1: header little-endian 12 byte (`4s,I,H,H`: magic, 44100 Hz, 2 channels, 16 bits), mỗi packet header 16 byte (`I,Q,I`: frames, QPC time 100 ns, WASAPI flags) rồi PCM16. Python kiểm tra giới hạn packet, chuyển mono16k, gom ~100 ms, quy QPC sang monotonic time của phiên. Discontinuity xóa phần gom dang dở. Stop/EOF stdin và parent crash làm bridge thoát; timeout cưỡng bức terminate chỉ bridge do Suni tạo. Tiến trình nguồn đóng sẽ báo lỗi, không fallback âm thầm sang thu toàn máy.

`engine/pipeline.py`: supervisor inference load Speech → Translator → Voice; sau đó khởi động capture và voice workers. Capture đọc block 100 ms, mono/resample 16 kHz, đẩy queue 40 block. Inference gom khoảng 3 giây, STT + VAD rồi dịch. Phân đoạn có timestamp theo monotonic clock; bỏ phân đoạn quá 20 giây và audio cũ khi queue đầy. Các block không overlap nên không lặp do overlap; không loại câu lặp hợp lệ của người nói. Gap trong capture khiến bộ gom reset.

Voice queue tối đa 3 câu; câu cũ tự hết hạn trước/sau synthesize. TTS ghi WAV trong TemporaryDirectory, resample về định dạng endpoint khi phát; kiểm tra stop giữa các block. Ducking journal được ghi trước khi đổi volume. Exception/Stop đi qua finally và khôi phục. Khóa đơn phiên QLockFile bảo vệ journal.

`storage`: SQLite WAL với RLock, bảng settings/sessions/segments. Mỗi phiên có id riêng. Export chỉ lấy phiên được chọn. `engine/export.py` tạo timestamp SRT chuẩn.

Offline NLLB tải bằng local_files_only. Ollama là adapter HTTP explicit opt-in; không có credential hoặc fallback cloud ngầm. Piper đọc config ngôn ngữ trước khi dùng. STT CUDA init thất bại thì thử CPU; runtime CUDA failure dừng rõ ràng.

Chủ ý an toàn: Replacement bị khóa, không giả lập bằng mute. Route khác endpoint vẫn cần người dùng đảm bảo mixer ngoài không feedback. Inference chưa có hủy cứng; Stop chờ lời gọi model hiện tại kết thúc để tránh giải phóng resource đang sử dụng.
