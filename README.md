# Dola Coordinator + Dola Render Gateway

Điều phối sinh video trên Dola AI với nhiều tài khoản.

| Thành phần | Công nghệ | Vai trò |
|---|---|---|
| [`DolaCoordinator/`](DolaCoordinator) | WPF · .NET 8 · MVVM (CommunityToolkit) · LiteDB · MS DI | Giao diện: quản lý tài khoản, hàng đợi prompt, điều phối, tải video |
| [`dola-render-gateway/`](dola-render-gateway) | Python · FastAPI · patchright (Chromium) | Điều khiển trình duyệt Dola: đăng nhập, gửi prompt, chờ video, quản lý hạn ngạch |

Tài liệu: [Kiến trúc](docs/ARCHITECTURE.md)

## Chạy ngay (clone về là dùng được)

```powershell
git clone https://github.com/xtraiter/AIOMMO_DolaAI.git
cd AIOMMO_DolaAI\release\DolaCoordinator
.\DolaCoordinator.exe
```

Bản đóng gói sẵn nằm trong [`release/DolaCoordinator/`](release/DolaCoordinator). **Không cần cài Python hay .NET, không có file .bat nào phải chạy.** Yêu cầu: Windows 10/11 x64, có mạng ở lần chạy đầu tiên (tải Chromium). Thư mục `release` nặng khoảng 370 MB nên clone lần đầu hơi lâu. Clone vào đường dẫn ngắn (ví dụ `D:\App\`): Windows giới hạn đường dẫn 260 ký tự và bản đóng gói có vài file nằm sâu; nếu Git báo `Filename too long` thì chạy `git config --global core.longpaths true` rồi clone lại.

```
DolaCoordinator.exe          Ứng dụng
gateway\dola-gateway.exe     Gateway đóng gói sẵn — app tự chạy ngầm, tự tắt khi đóng app
gateway\accounts\            (tự tạo) phiên đăng nhập của từng tài khoản
```

- Gateway chạy ngầm (không hiện cửa sổ) khi bạn dùng điều phối, *Kiểm tra phiên*, *Kiểm tra kết nối* hoặc mở/đăng nhập tài khoản; log ở `%LOCALAPPDATA%\DolaCoordinator\gateway.log`.
- **Lần đầu dùng trên một máy**, app tự tải trình duyệt Chromium (~150 MB, có thể mất vài phút; cần mạng). Từ lần sau không tải lại.
- Cài trình duyệt tự động thử 3 cách: (1) bộ tải của Playwright (dùng proxy hệ thống nếu có), (2) tải trực tiếp cùng các file đó từ storage.googleapis.com / cdn.playwright.dev theo cấu hình proxy của Windows (như trình duyệt web), (3) gói dự phòng `dola-browser-1243.zip` trên GitHub Releases (tùy chọn, tạo bằng `scripts\pack-browser.ps1`). Nếu vẫn lỗi: **Cài đặt → Trình duyệt Chromium → Cài đặt trình duyệt** hoặc nút **Cài đặt ngay** ở thẻ vàng cột trái.
- Đặt cả thư mục ở nơi có quyền ghi (không đặt trong `Program Files`), vì dữ liệu tài khoản nằm cạnh file exe.

1. **Tab Dola Super**: *Thêm tài khoản* → đăng nhập thủ công / Google / Facebook / cookie Facebook. Mỗi tài khoản là một thư mục `gateway\accounts\<tên>`; phiên được lưu lại dùng lâu dài.
2. **Tab Vận hành**: nhập prompt (mỗi dòng một video), chọn tỷ lệ, thời lượng, ảnh tham chiếu, thư mục lưu → *Bắt đầu điều phối*.
3. **Tab Cài đặt**: hạn ngạch/ngày, số luồng, thư mục lưu video.

### Cách điều phối chạy

- Chọn tài khoản còn hạn ngạch; lỗi (hết lượt, hết credit, kiểm tra an toàn, mất đăng nhập, quá thời gian…) → tự đổi sang tài khoản khác.
- Mỗi tài khoản **chào hỏi một lần mỗi ngày** (một câu hỏi ngẫu nhiên, chờ Dola trả lời) để kiểm tra hoạt động, rồi mở chat mới và gửi prompt.
- Hạn ngạch/ngày chỉnh ở *Cài đặt* (áp dụng cho mọi tài khoản, reset 00:00).
- Prompt đã được gateway nhận thì không bao giờ gửi lại khi lỗi mạng thoáng qua (tránh tốn lượt).

## Phát triển

Cần .NET 8 SDK; chạy gateway từ mã nguồn thì cần thêm Python 3.10+:

```powershell
cd dola-render-gateway
pip install -r requirements.txt
python -m patchright install chromium
cd ..
dotnet run --project DolaCoordinator      # app tự dò dola-render-gateway/ và chạy bằng Python
```

Không có thư mục `gateway\dola-gateway.exe` thì app dùng `dola-render-gateway\server.py` + lệnh Python trong *Cài đặt* (mặc định `py -3`). Muốn tự bật gateway riêng: `python gateway_main.py serve` (trong `dola-render-gateway/`).

## Cấu trúc thư mục

```
DolaCoordinator/        Ứng dụng WPF (xem docs/ARCHITECTURE.md)
dola-render-gateway/    Gateway Python (README riêng; gateway_main.py là điểm vào khi đóng gói)
scripts/                build-release.ps1 · build-gateway.ps1 · pack-update.ps1 · export-handover.ps1
docs/                   ARCHITECTURE.md
DolaAI.sln
```

## Đóng gói (máy build cần .NET 8 SDK + Python 3.10+)

```powershell
.\scripts\build-release.ps1 -Version 1.2.0 -ToRelease   # cập nhật release\DolaCoordinator\ (app + gateway, ~370 MB), rồi commit để ai clone cũng chạy được
.\scripts\build-release.ps1 -Version 1.2.0              # xuất ra dist\ (không commit); thêm -Zip nếu cần file zip để gửi
.\scripts\build-gateway.ps1                   # chỉ đóng gói gateway -> dist\gateway\dola-gateway.exe
.\scripts\pack-update.ps1 -Version 1.2.1 -DownloadBaseUrl https://<host-cua-ban>/dola   # gói cập nhật (chỉ exe app) + version.json
.\scripts\export-handover.ps1                 # bản MÃ NGUỒN sạch để bàn giao (không có phiên đăng nhập)
```

Tự cập nhật **tắt mặc định**. Muốn bật: đặt URL HTTPS của `version.json` trong *Cài đặt*. App chỉ cài gói cùng host,
có SHA-256 khớp, và không chứa đường dẫn thoát thư mục.

## Dữ liệu cục bộ — KHÔNG đưa vào git / không bàn giao

- `gateway\accounts\` (bản đóng gói) hoặc `dola-render-gateway/accounts/` (bản mã nguồn) — **phiên đăng nhập thật**
- `*.db` cạnh gateway — lịch sử tác vụ, khóa API, hạn ngạch
- `%LOCALAPPDATA%\DolaCoordinator` (LiteDB `coordinator.db`, token mã hóa DPAPI, `gateway.log`)
- `downloads/`, `dist/` (còn `release/` thì được commit, trừ dữ liệu chạy sinh ra trong `release/DolaCoordinator/gateway/`)

`.gitignore` đã loại các mục trên; `scripts\export-handover.ps1` cũng bỏ chúng khi tạo gói bàn giao.
