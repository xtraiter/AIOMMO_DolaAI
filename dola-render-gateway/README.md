# Gateway của AIOMMO DolaAI

Gateway chạy ngầm bên dưới app: nhận lệnh tạo video, điều khiển Chromium (có extension Dola30 để mở tùy chọn 30 giây), tải video về.
Khi đóng gói được build thành `dola-gateway.exe` (không cần cài Python).

## Nguồn gốc

Đây là **[Roins-hub/dola-pool](https://github.com/Roins-hub/dola-pool)** (bản `2.1.3`, commit `c910f3a40ea9282a7b7d7e0799269d93bef89b69`, 28/09/2026)
kèm một số chỗ chỉnh cho app desktop. Repo gốc **không có license**: chỉ dùng nội bộ, đừng phát hành lại gateway này ở nơi công khai nếu chưa được tác giả đồng ý.

Những file thêm/sửa đều đánh dấu **`[AIOMMO]`** trong code, để lần sau cập nhật theo dola-pool chỉ cần gắn lại các chỗ này:

| File | Chỗ chỉnh |
|---|---|
| `app_extras.py` (mới) | Toàn bộ phần riêng: tùy chọn theo từng tác vụ (`RunOptions`), gõ prompt nhiều dòng bằng Shift+Enter, bỏ chữ thời lượng khỏi prompt, trả lời khi Dola hỏi lại (ngoài câu hỏi về thời lượng), ảnh tham chiếu trên máy, mã lỗi |
| `prompt_clean.py` (mới) | Bỏ `30s`, `00:00 - 00:03`, `Giây 0 đến 3`... khỏi prompt (README extension Dola30 dặn không ghi thời lượng trong prompt) |
| `gateway_main.py`, `open_profile.py`, `add_account_cookie.py`, `fb_to_dola.py` (mới) | Điểm vào của bản đóng gói (`serve` / `open-profile` / `install-browser`), đăng nhập Google/Facebook/cookie từ app |
| `server.py` | Thêm trường `account`, `hide_window`, `auto_reply`, `strip_duration_words`, `reference_local_paths`; trả thêm `failure_code`, `account`, `stage`, `note`; `DELETE /v1/videos/{id}` (hủy thật, đóng Chromium); `login_ok` khi verify; khóa admin đơn giản; `import_cookie` / `import_fb_cookie`; không chạy lại tác vụ dở dang của lần trước |
| `browser_pool.py` | Tác vụ **ghim tài khoản**: chỉ dùng đúng tài khoản app chọn, bỏ qua nhóm hạn ngạch của pool, báo lỗi thật thay vì tự đổi tài khoản |
| `browser.py` | Cửa sổ Chromium ẩn (đặt ngoài màn hình), nạp lại phiên từ `cookie.txt` |
| `video_worker_ui.py` | Gọi các hàm trên: làm sạch prompt, gõ nhiều dòng, báo giai đoạn, ghi chú của Dola, `DOLA_DRY_RUN` |
| `store.py` | Thêm cột `stage`, `note`, hàm `fail_unfinished` |
| `config.py` | Không proxy mặc định (bản gốc mặc định `127.0.0.1:7890`), `DOLA_DRY_RUN`, `DOLA_STRIP_DURATION_WORDS`, `DOLA_RESUME_TASKS` |

Mặc định khi chạy bằng app (đặt trong `gateway_main.py`, biến môi trường vẫn được ưu tiên): `DOLA_PURE_API=0` (đường HTTP ký bằng Node.js cần cài Node;
đường trình duyệt thì không), `DOLA_VIDEO_TIMEOUT=1800`, `DOLA_TASK_DEADLINE=1800`.

## Cập nhật theo dola-pool

1. Tải bản mới của dola-pool, chép đè các file Python (giữ lại `app_extras.py`, `prompt_clean.py`, `gateway_main.py`, `open_profile.py`, `add_account_cookie.py`, `fb_to_dola.py`).
2. Gắn lại các chỗ `[AIOMMO]` ở bảng trên (tìm bằng `git diff` với bản cũ).
3. Chạy lại bộ thử: dựng gateway, tạo tác vụ có `account`, hủy tác vụ, kiểm tra `failure_code` (xem ghi chú trong commit "Switch to dola-pool gateway").
4. Build: `scripts\build-release.ps1 -Version 1.0.0 -ToRelease`.

Tài liệu gốc của dola-pool: `API.md`, `CHANGELOG.md`, `DEVELOPMENT.md` (tiếng Trung).
