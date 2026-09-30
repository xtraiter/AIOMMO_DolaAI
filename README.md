# AIOMMO DolaAI (Dola Coordinator + Dola Render Gateway)

Điều phối sinh video trên Dola AI với nhiều tài khoản.

| Thành phần | Công nghệ | Vai trò |
|---|---|---|
| [`DolaCoordinator/`](DolaCoordinator) | WPF · .NET 8 · MVVM (CommunityToolkit) · LiteDB · MS DI | Giao diện: quản lý tài khoản, hàng đợi prompt, điều phối, tải video |
| [`dola-render-gateway/`](dola-render-gateway) | Python · FastAPI · patchright (Chromium) | Điều khiển trình duyệt Dola: đăng nhập, gửi prompt, chờ video, quản lý hạn ngạch |

Tài liệu: [Kiến trúc](docs/ARCHITECTURE.md)

## Chạy ngay (clone về là dùng được)

```powershell
git clone https://github.com/xtraiter/AIOMMO_DolaAI.git
cd AIOMMO_DolaAI\release\AIOMMO_DolaAI
& ".\AIOMMO DolaAI.exe"
```

Bản đóng gói sẵn nằm trong [`release/AIOMMO_DolaAI/`](release/AIOMMO_DolaAI). **Không cần cài Python hay .NET, không có file .bat nào phải chạy.** Yêu cầu: Windows 10/11 x64, có mạng ở lần chạy đầu tiên (tải Chromium). Thư mục `release` nặng khoảng 370 MB nên clone lần đầu hơi lâu. Clone vào đường dẫn ngắn (ví dụ `D:\App\`): Windows giới hạn đường dẫn 260 ký tự và bản đóng gói có vài file nằm sâu; nếu Git báo `Filename too long` thì chạy `git config --global core.longpaths true` rồi clone lại.

```
AIOMMO DolaAI.exe            Ứng dụng
gateway\dola-gateway.exe     Gateway đóng gói sẵn — app tự chạy ngầm, tự tắt khi đóng app
gateway\accounts\            (tự tạo) phiên đăng nhập của từng tài khoản
```

- Gateway chạy ngầm (không hiện cửa sổ) khi bạn dùng điều phối, *Kiểm tra phiên*, *Kiểm tra kết nối* hoặc mở/đăng nhập tài khoản; log ở `%LOCALAPPDATA%\DolaCoordinator\gateway.log`.
- **Lần đầu dùng trên một máy**, app tự tải trình duyệt Chromium (~150 MB, có thể mất vài phút; cần mạng). Từ lần sau không tải lại.
- Cài trình duyệt tự động thử 3 cách: (1) bộ tải của Playwright (dùng proxy hệ thống nếu có), (2) tải trực tiếp cùng các file đó từ storage.googleapis.com / cdn.playwright.dev theo cấu hình proxy của Windows (như trình duyệt web), (3) gói dự phòng `dola-browser-1243.zip` trên GitHub Releases (tùy chọn, tạo bằng `scripts\pack-browser.ps1`). Nếu vẫn lỗi: **Cài đặt → Trình duyệt Chromium → Cài đặt trình duyệt** hoặc nút **Cài đặt ngay** ở thẻ vàng cột trái.
- Đặt cả thư mục ở nơi có quyền ghi (không đặt trong `Program Files`), vì dữ liệu tài khoản nằm cạnh file exe.

1. **Tab Quản lý tài khoản**: *Thêm tài khoản* → đăng nhập thủ công / Google / Facebook / cookie Facebook. Mỗi tài khoản là một thư mục `gateway\accounts\<tên>`; phiên được lưu lại dùng lâu dài.
   - Tích ô đầu dòng rồi dùng các nút ở hàng trên cho các tài khoản đã tích: **Mở/Đóng đã chọn**, **Đăng nhập tự động**, **Sửa**, **Kiểm tra phiên**, **Reset hạn ngạch**, **Mở thư mục**, **Xóa đã chọn**. Cột cuối mỗi dòng chỉ còn nút Mở/Đóng.
      - **Nhập hàng loạt tài khoản từ Excel / CSV** (menu *Nhập / Xuất / Gateway → Nhập tài khoản từ Excel / CSV…*; *Tải file mẫu Excel* cho file có sẵn từng loại và sheet hướng dẫn). Hỗ trợ: **Google (Gmail)**, **Facebook thường** (email/SĐT + mật khẩu), **Facebook cookie**, **Cookie Dola**, **Thủ công**; cột Loại bỏ trống thì app tự đoán. Mỗi dòng lỗi được báo kèm số dòng; sau khi nhập có thể đăng nhập tự động luôn. File chứa mật khẩu/cookie dạng chữ thường — nhập xong hãy xóa.
- Thông tin đăng nhập (tài khoản, mật khẩu, khóa 2FA, cookie Facebook) được **ghi nhớ** nếu để tích "Ghi nhớ": mã hóa bằng Windows DPAPI (chỉ giải mã được bởi tài khoản Windows này, lưu trong `coordinator.db`), lần sau chỉ bấm *Đăng nhập tự động*. Sửa hoặc xóa thông tin đã lưu bằng nút **Sửa** (tích đúng 1 dòng).
2. **Tab Quản lý prompt**: thư viện prompt. Mỗi prompt có tên, **nội dung nhiều dòng** (giữ nguyên xuống dòng; có thể là bảng phân cảnh, danh sách…), model (Seedance 2.0 / 2.5), tỷ lệ (1:1, 3:4, 4:3, 9:16, 16:9, 21:9), thời lượng (5s, 10s, 30s — đúng các ô chọn của Dola) và ảnh tham chiếu mặc định. Khi gửi, các dòng xuống dòng được gõ bằng Shift+Enter (Enter sẽ gửi tin ngay) nên prompt nhiều dòng đi trọn vẹn trong một lần gửi. *Thêm prompt* (bấm đúp một dòng để sửa), *Nhập từ file* (Excel `.xlsx` — mỗi hàng một prompt, ô nhiều dòng xuống dòng bằng Alt+Enter; `.csv`; `.txt`/`.md`: các prompt cách nhau bằng một dòng `---`). Bấm *File mẫu Excel* để có file mẫu kèm sheet hướng dẫn từng cột, *Xuất CSV*, *Nhân bản*. Tích các prompt rồi bấm **Thêm vào hàng đợi** để chọn tỷ lệ / thời lượng / số video mỗi prompt / độ ưu tiên và bắt đầu làm video.
3. **Tab Vận hành**: không nhập prompt mà **quản lý tiến trình**: *Bắt đầu / Tạm dừng / Tiếp tục* (tạm dừng = không nhận việc mới, video đang làm chạy nốt), *Dừng tất cả* (hủy cả video đang làm), và trên các dòng đã tích: *Dừng tác vụ*, *Thử lại*, *Lên đầu*, *Tăng/Giảm ưu tiên*, *Xem video*, *Mở thư mục*, *Xóa*. Có bộ lọc trạng thái, tìm kiếm và chọn thư mục lưu video. Phần **Cơ chế chạy** (lưu ngay, áp dụng tức thì):
   - *Cơ chế chạy tài khoản*: **Chia đều** (mặc định: mỗi tài khoản một video, chạy hết một vòng mới sang lượt 2) hoặc **Hết từng tài khoản** (dồn vào tài khoản đầu tới khi hết lượt / lỗi mới sang tài khoản kế). Tài khoản lỗi được bỏ qua ở các video khác trong 15 phút (tắt được bằng ô *Bỏ qua tài khoản lỗi*).
   - *Số luồng tối đa*: số video chạy cùng lúc (1–30, mặc định 2), đổi là có hiệu lực ngay. Mỗi luồng mở một cửa sổ Chromium nên máy yếu hãy để số nhỏ.
   - *Ẩn Chromium (chạy nền)*: cửa sổ Chromium nằm ngoài màn hình khi chạy vận hành (vẫn là cửa sổ thật nên Dola không thấy khác; có thể vẫn thấy biểu tượng trên thanh taskbar). Trang Quản lý tài khoản luôn hiện Chromium để đăng nhập.
   - Cột **Dùng để chạy** ở trang Quản lý tài khoản (mặc định đã tích) quyết định tài khoản nào tham gia vận hành; có nút *Đưa vào / Loại khỏi vận hành* cho các dòng đã tích.
   - Sau mỗi video, nhật ký ghi **thời lượng thực tế** đọc từ file (`[Thời lượng] …`) và lời Dola viết kèm (`[Dola nói] …`). Bảng Vận hành có cột **Yêu cầu** và **Thực tế**: khi Dola tạo ngắn/dài hơn thời lượng đã chọn (ví dụ chọn 15s ra 10s) dòng đó đổi màu cam và ghi rõ "do Dola, không phải lỗi của app".
   - Thanh **Tiến độ tổng** ở đáy cửa sổ (hiện ở mọi trang): trung bình tiến độ của cả đợt đang chạy, đạt 100% khi xong toàn bộ.
4. **Tab Cài đặt**: hạn ngạch/ngày, thư mục lưu video, trình duyệt Chromium, và **Khi Dola hỏi lại** (mặc định bật). Prompt của bạn được gửi nguyên văn, app không chèn thêm gì. Dola hay hỏi lại thay vì tạo video (ví dụ "kịch bản 30 giây nhưng tôi chỉ hỗ trợ 4–15 giây, dùng 15 giây được không?" hoặc "bạn có ảnh khuôn mặt không, A hay B?"). App phát hiện câu hỏi đó, tự gõ câu trả lời "có, tiếp tục ngay, chọn B, tự tạo nhân vật" (sửa được trong Cài đặt) để Dola tạo video luôn, tối đa 3 lần mỗi video; nhật ký ghi lại ở dòng `[Dola nói]`. Nếu sau 3 lần Dola vẫn hỏi, tác vụ báo lỗi kèm câu hỏi của Dola.

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
.\scripts\build-release.ps1 -Version 1.0.0 -ToRelease   # cập nhật release\AIOMMO_DolaAI\ (app + gateway, ~370 MB), rồi commit để ai clone cũng chạy được
.\scripts\build-release.ps1 -Version 1.0.0              # xuất ra dist\ (không commit); thêm -Zip nếu cần file zip để gửi
.\scripts\build-gateway.ps1                   # chỉ đóng gói gateway -> dist\gateway\dola-gateway.exe
.\scripts\pack-update.ps1 -Version 1.0.1 -DownloadBaseUrl https://<host-cua-ban>/dola   # gói cập nhật (chỉ exe app) + version.json
.\scripts\export-handover.ps1                 # bản MÃ NGUỒN sạch để bàn giao (không có phiên đăng nhập)
```

Tự cập nhật **tắt mặc định**. Muốn bật: đặt URL HTTPS của `version.json` trong *Cài đặt*. App chỉ cài gói cùng host,
có SHA-256 khớp, và không chứa đường dẫn thoát thư mục.

## Dữ liệu cục bộ — KHÔNG đưa vào git / không bàn giao

- `gateway\accounts\` (bản đóng gói) hoặc `dola-render-gateway/accounts/` (bản mã nguồn) — **phiên đăng nhập thật**
- `*.db` cạnh gateway — lịch sử tác vụ, khóa API, hạn ngạch
- `%LOCALAPPDATA%\DolaCoordinator` (LiteDB `coordinator.db`, token mã hóa DPAPI, `gateway.log`)
- `downloads/`, `dist/` (còn `release/` thì được commit, trừ dữ liệu chạy sinh ra trong `release/AIOMMO_DolaAI/gateway/`)

`.gitignore` đã loại các mục trên; `scripts\export-handover.ps1` cũng bỏ chúng khi tạo gói bàn giao.
