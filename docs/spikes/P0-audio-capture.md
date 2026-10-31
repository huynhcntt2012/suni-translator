# Spike P0 — Thu âm theo tiến trình (WASAPI Process Loopback)

- Ngày: 31/10/2026
- Máy test: Windows 11 Pro build 26200, x64, thiết bị phát mặc định của máy
- Code: `src/Suni.Audio/WasapiLoopbackCapture.cs`, `AudioSourceEnumerator.cs`, công cụ `tools/Suni.CaptureSpike`
- Tái hiện: `tools/Suni.CaptureSpike/run-isolation-test.ps1 [-BackgroundPids <pid gốc app đang phát>]`

## Kết luận

1. **Process loopback viết bằng C# (COM interop) hoạt động tốt** → **bỏ DLL C++** (ADR-03 sửa lại).
   Lợi ích: build chỉ cần .NET SDK, installer không cần VC++ runtime, một ngôn ngữ duy nhất.
2. **Cách ly theo tiến trình đạt yêu cầu TC-01**: tín hiệu của tiến trình khác không đo được (≤ -90 dBFS).
3. **Chế độ "toàn hệ thống trừ chính mình" (ADR-04) hoạt động** → giải pháp chống tự thu tiếng TTS đã được kiểm chứng về cơ chế.
4. Chi phí CPU không đáng kể: callback trung bình ~40–60 µs cho mỗi gói 10 ms (< 0,6 % một lõi).

## Phương pháp

Hai tiến trình `powershell.exe` độc lập phát hai tone (A = 440 Hz, B = 1000 Hz, -20 dBFS) qua `System.Media.SoundPlayer`.
Trong lúc đó chạy song song 5–7 phiên thu 4 giây, mỗi phiên một chế độ, rồi đo biên độ tại 440/1000 Hz bằng Goertzel.
Chrome của người dùng đang phát nhạc trong suốt bài test (nhiễu nền thật), nên thu thêm Chrome để đối chứng.

## Kết quả

| Phiên thu | 440 Hz (A) | 1000 Hz (B) | Peak | Nhận xét |
|---|---|---|---|---|
| Include PID A | **-20.1** | -90 (sàn) | -20 | Chỉ có A |
| Include PID B | -90 (sàn) | **-20.1** | -20 | Chỉ có B |
| Exclude PID A | -61.7 | **-20.1** | -4.4 | Có B + nhạc Chrome; A không lọt (xem dòng Chrome) |
| Include Chrome (gốc 12740) | -62.9 | -76 | -5.5 | Nhạc nền: chính là phần -61.7 dB ở dòng trên |
| System trừ chính mình | -20.0 | -20.1 | -3.5 | Đủ A + B + Chrome |
| Endpoint loopback (mặc định) | -20.0 | -20.0 | -3.1 | Giống System |
| Include app im lặng (QtWebEngine) | -90 | -90 | -90 | Vẫn nhận gói đều, toàn số 0 |

Thông số luồng (mọi chế độ): 48 kHz float32 stereo, gói 10 ms, ~100 gói/s, khoảng hở tối đa giữa hai gói 11 ms,
gói đầu tiên tới sau 12–28 ms (process) / < 1 ms (endpoint). Process loopback: 0 discontinuity; endpoint: 1 discontinuity ngay lúc bắt đầu (bình thường).

## Hành vi cần biết khi tích hợp

| Tình huống | Hành vi quan sát | Xử lý trong code |
|---|---|---|
| Nguồn im lặng | Process loopback **vẫn phát gói liên tục** (số 0, không có cờ SILENT) | Sample clock liên tục, không cần chèn im lặng |
| Tiến trình nguồn thoát giữa chừng (TC-08) | Luồng vẫn chạy, trả im lặng; không crash | `Process.Exited` → `CaptureState.SourceLost` sau ~0 ms (đã kiểm chứng) |
| PID không tồn tại | **Windows vẫn kích hoạt thành công** và chỉ trả im lặng | Kiểm tra PID trước khi start → `CAP_PROCESS_NOT_FOUND` (đã sửa + kiểm chứng) |
| Gom tiến trình con | Audio Chrome phát từ tiến trình con, thu PID gốc + include tree lấy được | `ProcessTree.FindRoot` gom theo tên exe (unit test) |
| **Cây nhiều tầng** (bổ sung 31/10, phát hiện khi test overlay) | `INCLUDE_TARGET_PROCESS_TREE` chỉ gồm tiến trình đích và **con trực tiếp**: ông → cha → trình phát, thu PID ông = im lặng (-90 dBFS), thu cha hoặc trình phát = -20 dBFS | Danh sách nguồn vẫn gom theo gốc (một dòng/app), nhưng thu theo `CapturePid` = cha trực tiếp cùng exe của session đang phát (`ProcessTree.FindCaptureTarget`) |
| Liệt kê nguồn | Thấy đúng Chrome (gom về PID gốc), QtWebEngine, PowerShell (2 session gom 1) | `AudioSourceEnumerator` |
| Format | Tự khai báo float32 48 kHz stereo + `AUTOCONVERTPCM`, không cần `GetMixFormat` | Dùng cho cả process và endpoint |

## Chưa kiểm chứng (cần làm tay / ở bước Hardening)

- **TC-07 đổi thiết bị phát** (loa ↔ tai nghe Bluetooth) giữa chừng — cần thao tác tay. Code đã map `AUDCLNT_E_DEVICE_INVALIDATED` → `DeviceChanged`, nhưng chưa biết process loopback có tự theo thiết bị mới hay không.
- **Phiên dài 60 phút** (rò rỉ handle/bộ nhớ, trôi đồng hồ).
- **Ứng dụng thật khác**: Discord, Zoom, game borderless; ứng dụng chạy quyền admin (không mở được handle → mất cảnh báo SourceLost, vẫn thu).
- **Chrome nhiều tab**: xác nhận lại giới hạn đã biết — thu cả cây tiến trình nên mọi tab đều vào.
- **Self-exclusion khi app tự phát TTS**: cơ chế exclude đã kiểm chứng với PID khác; kiểm lại khi có Suni.Tts (P2) chạy trong process riêng — khi đó phải loại trừ cả process TTS (là tiến trình con của app → nằm trong cây bị loại trừ).
- Benchmark ASR (P0 phần b) và LLM trên server LAN (P0 phần c) — spike riêng.
