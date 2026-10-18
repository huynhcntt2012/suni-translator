# Kiểm thử và nghiệm thu

9 unit tests thực hiện resample có giữ tần số/thời lượng, từ chối route cùng endpoint, bounded queue bỏ mẫu cũ, SQLite persistence/isolated sessions, timestamp SRT và Unicode, từ chối packet pipe bị cắt, PID tái sử dụng và chính cây audio Suni.

Smoke GUI dựng dashboard, 4 tab, overlay và đóng an toàn bằng Qt offscreen. Đây là kiểm tra render/lifecycle, không chứng nhận tương tác click-through trên desktop thật.

`scripts/verify_local.py` phát SAPI English, thu WASAPI thật, dùng Whisper small CPU + NLLB. Kết quả JSON ở `tmp/verification/result.json`. Đây là test bằng tín hiệu tổng hợp đi qua endpoint thật, không thay cho nghiệm thu YouTube hoặc microphone người thật. Nguồn đặt English để tránh sai nhận diện ngôn ngữ ở đoạn ngắn. Thời gian STT trong script bao gồm nạp model, không phải benchmark live end-to-end.

Checklist manual còn phải làm:

| Test | Pass |
|---|---|
| Chrome YouTube English → Việt 10 phút | Không rơi pipeline; nghĩa đúng; timestamp tiến; ghi p50/p95 latency |
| 20 lần Start/Stop | Không treo, không trùng worker, UI còn phản hồi |
| Rút thiết bị thu/phát | Báo lỗi; dừng an toàn; thiết bị reconnect xuất hiện sau refresh |
| Voice Overlay hai endpoint | Giọng Việt nghe được; không tự nhận diện giọng AI; duck/restore đúng |
| Stop giữa TTS | Dừng phát; volume khôi phục |
| Kill process giữa ducking rồi restart | Journal khôi phục volume nếu endpoint còn kết nối |
| Offline ngắt Internet | Model đã tải vẫn nhận diện và dịch |
| Auto VI/EN/ZH/JA/KO | Ghi độ chính xác trên các đoạn đủ dài, nhiều người nói |
| Chrome/Zoom/Discord riêng | Chọn root PID, nguồn khác không lọt vào bản thu; test Chrome tone đã pass, Zoom/Discord chưa nghiệm thu |
| CUDA/CPU fallback | Thử máy có runtime CUDA tương thích và máy thiếu runtime |
| Replacement | Chưa triển khai; không đánh dấu pass |

Không coi unit tests/provider giả lập là bằng chứng hoàn thành thu âm hay dịch thời gian thực. Không có mock được dùng để báo nghiệm thu audio.
