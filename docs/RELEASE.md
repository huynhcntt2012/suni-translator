# Release 0.1.0 — MVP preview

Bản triển khai gồm source, launcher, bản portable `dist/Suni/Suni.exe`, cấu hình packaging PyInstaller/Inno Setup, test và hướng dẫn. Self-test trên EXE đã kiểm tra nạp model, dịch offline và giao diện. Giữ nguyên thư mục `_internal` cạnh EXE; không chép riêng EXE. Không phải release production đầy đủ tài liệu Dev Brief. Chưa xuất bản repository GitHub vì người dùng chưa chỉ định repository và chưa yêu cầu publish.

Model Whisper small (~0.5 GB) và NLLB (~2.5 GB) được lưu riêng. Bộ cài phải giữ nguyên model/data khi nâng cấp; không xóa profile khi uninstall mặc định. NLLB không dành cho phân phối thương mại dưới giấy phép hiện tại.

Đã bổ sung capture riêng ứng dụng theo cây tiến trình. Đã kiểm chứng Chrome WebAudio và test cách ly hai nguồn. Zoom/Discord chưa được nghiệm thu thực tế. Nguồn riêng chỉ bật Live Subtitles; Voice Overlay vẫn dùng nguồn System Audio.

Hạn chế phát hành: Replacement không khả dụng; chưa xác nhận chất lượng nghe Piper tiếng Việt; chưa đo RTX 5060 Ti; chưa có code signing; cần test binary trên máy Windows sạch, driver audio thật và thiết bị USB. Cấu hình Inno Setup chưa đồng nghĩa đã compile installer.
