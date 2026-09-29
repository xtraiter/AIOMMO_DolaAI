# Kiến trúc

```
┌──────────────────────── DolaCoordinator (WPF, .NET 8) ────────────────────────┐
│ Views ─► ViewModels ─► Services                                               │
│  Queue/Profiles/Settings   Queue · Profiles · Sessions/Quota · Network ·      │
│                            Downloader · Storage(LiteDB) · Update · Security   │
└───────────────────────────────┬───────────────────────────────────────────────┘
                                │ HTTP (127.0.0.1:8000) + đọc/ghi thư mục accounts/
┌───────────────────────────────▼───────────────────────────────────────────────┐
│ dola-render-gateway (FastAPI)                                                 │
│ server.py ─► browser_pool.py ─► video_worker_ui.py ─► warmup.py / browser.py  │
│   admin API     xoay tài khoản     điều khiển UI Dola     chào hỏi, Chromium  │
│ open_profile.py (đăng nhập, IPC bằng file)   store.py / pool_usage.db (SQLite)│
└───────────────────────────────────────────────────────────────────────────────┘
```

## Ứng dụng WPF

| Thư mục | Nội dung |
|---|---|
| `DolaModule.cs` | `services.AddDolaModule()` — điểm đăng ký DI duy nhất của module |
| `Views/` | `QueueView`, `ProfilesView`, `SettingsView` + `Dialogs/` (thêm tài khoản, đăng nhập tự động) |
| `ViewModels/` | CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), `WeakReferenceMessenger` |
| `Services/Queue` | `TaskDispatcher`: chọn tài khoản, gửi, poll, tải, đổi tài khoản khi lỗi |
| `Services/Sessions` | `QuotaTracker` (hạn ngạch/ngày do app quản), `SessionValidator` |
| `Services/Profiles` | `AccountProfileService`: mỗi tài khoản = thư mục `accounts/<tên>` của gateway; mở/đóng/đăng nhập |
| `Services/Gateway` | `GatewayHost`: bật ngầm gateway khi cần (`EnsureRunningAsync`), chờ `/health`, ghi log, gắn Job Object để tắt cùng app kể cả khi app crash; bản đóng gói tự tải Chromium lần đầu (`EnsureBrowserAsync`) |
| `Services/Network` | `DolaGatewayClient` (REST tới gateway) |
| `Services/Storage` | `LiteDbDatabaseService` (`coordinator.db`) |
| `Services/Update` | `AutoUpdateService` (HTTPS, cùng host, SHA-256, chống zip-slip; tắt mặc định) |
| `Themes/` | `DolaModule.xaml` (điểm vào) → `AllInOneTheme.xaml` + converters. Mỗi màn hình tự merge, **không** đặt ở `App.xaml` |

### Điều phối một prompt

1. `TaskDispatcher` lấy tác vụ (chỉ một luồng nhận mỗi tác vụ) và chọn tài khoản còn hạn ngạch, không bị gateway đánh dấu chặn cứng.
2. Trừ hạn ngạch khi giao; hoàn lại nếu tác vụ không tiêu tốn lượt.
3. Gateway: mở Chromium bằng profile của tài khoản → (nếu hôm nay chưa chào hỏi) chào hỏi, chờ trả lời → chat mới → nhập prompt/ảnh/tỷ lệ/thời lượng → chờ video.
4. Lỗi có mã (`account_limited`, `credit`, `risk_control`, `login_required`, `unhealthy`, `timeout`, …) → đổi tài khoản khác. Sau khi gateway **đã nhận** prompt thì không gửi lại; lỗi mạng thoáng qua chỉ poll lại.
5. App tải MP4 về thư mục đã chọn (mặc định *Videos*).

### Đăng nhập tài khoản

`open_profile.py` mở Chromium của gateway và ghi trạng thái vào `.profile_status.json` (pid, đã đăng nhập, giai đoạn, cần người can thiệp). App đọc file này và gửi `.close_request` để đóng. Thông tin đăng nhập Google/Facebook truyền qua **stdin** (một dòng JSON), không ghi ra đĩa; captcha/2FA do người dùng giải. Phiên được lưu ở `cookie.txt` và nạp lại mỗi lần chạy. "Đã đăng nhập" xác định bằng `is_login` từ `/alice/user/launch` của Dola.

## Gateway

Hai dạng chạy, cùng mã nguồn: **đóng gói** (`gateway\dola-gateway.exe`, PyInstaller, điểm vào `gateway_main.py` với lệnh `serve` / `open-profile` / `install-browser`, không cần Python) và **mã nguồn** (`server.py` + Python). `GatewayLocator.BuildCommand` chọn dạng theo việc có `dola-gateway.exe` hay không.

Giữ nguyên kiến trúc gốc (xem `dola-render-gateway/README.md`, `DEVELOPMENT.md`); phần bổ sung: `warmup.py` (chào hỏi, mỗi tài khoản một lần/ngày lưu ở `accounts_meta.warmup_day`), `dola_errors.py`, `open_profile.py`, ảnh tham chiếu từ đường dẫn cục bộ (chỉ loopback, kiểm tra bằng PIL), trường `stage` / `failure_code` / `account` trong phản hồi tác vụ, chế độ `DOLA_DRY_RUN`.

Biến môi trường chính: `DOLA_WARMUP` (chào hỏi, mặc định bật), `DOLA_WARMUP_TIMEOUT`, `DOLA_WARMUP_QUESTIONS`, `DOLA_DAILY_LIMIT`, `DOLA_DRY_RUN`, `DOLA_API_KEYS`.

## Bảo mật

- Gateway do app bật chỉ lắng nghe `127.0.0.1`; app chỉ tự bật khi `GatewayUrl` trỏ về máy này (không mở ra mạng LAN).
- Token/khóa lưu bằng DPAPI (`DpapiSecurityService`).
- Mật khẩu Google/Facebook chỉ nằm trong bộ nhớ trong lúc đăng nhập.
- Tự cập nhật tắt mặc định (gói cập nhật ghi đè file chạy nên bắt buộc HTTPS + SHA-256 + cùng host).
