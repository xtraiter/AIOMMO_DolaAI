# AIOMMO DolaAI (Dola Coordinator + Dola Render Gateway)

Điều phối sinh video trên Dola AI với nhiều tài khoản.

| Thành phần | Công nghệ | Vai trò |
|---|---|---|
| [`DolaCoordinator/`](DolaCoordinator) | WPF · .NET 8 · MVVM (CommunityToolkit) · LiteDB · MS DI | Giao diện: quản lý tài khoản, hàng đợi prompt, điều phối, tải video |
| [`dola-render-gateway/`](dola-render-gateway) | Python · FastAPI · patchright (Chromium) | Điều khiển trình duyệt Dola: đăng nhập, gửi prompt, chờ video, quản lý hạn ngạch |

Tài liệu: [Kiến trúc](docs/ARCHITECTURE.md)

## Cài đặt bằng bộ cài (cách dành cho người dùng)

Chạy **`AIOMMO_DolaAI_Setup_<phiên bản>.exe`** (tạo bằng `scripts\build-installer.ps1`, nằm trong `dist\installer\`): đọc và đồng ý **Điều khoản sử dụng**, chọn thư mục cài (mặc định **`C:\Program Files\AIOMMO DolaAI`**, cần quyền Admin; không có quyền Admin thì chọn *chỉ cho tôi* để cài vào `%LOCALAPPDATA%\Programs`), tích tạo biểu tượng ngoài màn hình nền, và để tích *Tải trình duyệt Chromium ngay* để cài đủ môi trường. Bộ cài đặt sẵn app, gateway, ffmpeg, tạo shortcut ở Start Menu / màn hình nền và mục gỡ cài đặt trong Settings → Apps.

**Dữ liệu của bạn nằm riêng ở `%APPDATA%\AIOMMO DolaAI`** (cơ sở dữ liệu và cài đặt `coordinator.db`, nhật ký `gateway.log`, và `gateway\accounts\<tên>` là hồ sơ trình duyệt, cookie, proxy của từng tài khoản), không nằm trong thư mục chương trình (chỉ đọc). Nhờ vậy cài đè bản mới hay gỡ cài đặt không làm mất tài khoản, proxy, prompt; gỡ cài đặt hỏi có xóa dữ liệu không (mặc định giữ). Video tạo ra lưu ở thư mục bạn chọn trong app (mặc định `Videos`). Điều khoản ở `installer\Terms_vi.txt`, thành phần bên thứ ba ở `installer\THIRD_PARTY_NOTICES.txt`.

Tạo bộ cài (máy build cần Inno Setup 6: `winget install JRSoftware.InnoSetup --scope user`):

```powershell
.\scripts\build-installer.ps1 -Version 1.0.0     # build app + gateway + ffmpeg rồi đóng thành 1 file .exe (không đụng thư mục release)
```

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
tools\ffmpeg.exe             ffmpeg (lấy khung hình cuối, ghép video) — đã đóng gói sẵn
```

- Gateway chạy ngầm (không hiện cửa sổ) khi bạn dùng điều phối, *Kiểm tra phiên*, *Kiểm tra kết nối* hoặc mở/đăng nhập tài khoản; log ở `%LOCALAPPDATA%\DolaCoordinator\gateway.log`.
- **Lần đầu dùng trên một máy**, app tự tải trình duyệt Chromium (~150 MB, có thể mất vài phút; cần mạng). Từ lần sau không tải lại.
- Cài trình duyệt tự động thử 3 cách: (1) bộ tải của Playwright (dùng proxy hệ thống nếu có), (2) tải trực tiếp cùng các file đó từ storage.googleapis.com / cdn.playwright.dev theo cấu hình proxy của Windows (như trình duyệt web), (3) gói dự phòng `dola-browser-1243.zip` trên GitHub Releases (tùy chọn, tạo bằng `scripts\pack-browser.ps1`). Nếu vẫn lỗi: **Cài đặt → Trình duyệt Chromium → Cài đặt trình duyệt** hoặc nút **Cài đặt ngay** ở thẻ vàng cột trái.
- Đặt cả thư mục ở nơi có quyền ghi (không đặt trong `Program Files`), vì dữ liệu tài khoản nằm cạnh file exe.

1. **Tab Quản lý tài khoản**: *Thêm tài khoản* → đăng nhập thủ công / Google / Facebook / cookie Facebook. Mỗi tài khoản là một thư mục `gateway\accounts\<tên>`; phiên được lưu lại dùng lâu dài.
   - Tích ô đầu dòng rồi dùng các nút ở hàng trên cho các tài khoản đã tích: **Mở/Đóng đã chọn**, **Đăng nhập tự động**, **Sửa**, **Kiểm tra phiên**, **Reset hạn ngạch**, **Mở thư mục**, **Xóa đã chọn**. Cột cuối mỗi dòng chỉ còn nút Mở/Đóng.
      - **Nhập hàng loạt tài khoản từ Excel / CSV** (menu *Nhập / Xuất / Gateway → Nhập tài khoản từ Excel / CSV…*; *Tải file mẫu Excel* cho file có sẵn từng loại và sheet hướng dẫn). Hỗ trợ: **Google (Gmail)**, **Facebook thường** (email/SĐT + mật khẩu), **Facebook cookie**, **Cookie Dola**, **Thủ công**; cột Loại bỏ trống thì app tự đoán. Mỗi dòng lỗi được báo kèm số dòng; sau khi nhập có thể đăng nhập tự động luôn. File chứa mật khẩu/cookie dạng chữ thường — nhập xong hãy xóa.
- Thông tin đăng nhập (tài khoản, mật khẩu, khóa 2FA, cookie Facebook) được **ghi nhớ** nếu để tích "Ghi nhớ": mã hóa bằng Windows DPAPI (chỉ giải mã được bởi tài khoản Windows này, lưu trong `coordinator.db`), lần sau chỉ bấm *Đăng nhập tự động*. Sửa hoặc xóa thông tin đã lưu bằng nút **Sửa** (tích đúng 1 dòng).
2. **Tab Quản lý proxy**: danh sách proxy dùng chung cho các tài khoản.
   - *Thêm proxy* (kiểu http / https / socks5, host, port, tài khoản + mật khẩu nếu có; mật khẩu mã hóa bằng Windows DPAPI) hoặc *Nhập nhiều proxy* (dán mỗi dòng một proxy: `host:port`, `host:port:user:pass`, `user:pass@host:port`, `http://user:pass@host:port`, `socks5://host:port`). Proxy trùng được bỏ qua, dòng sai được báo kèm số dòng.
   - *Kiểm tra đã chọn / tất cả*: đi qua proxy tới dịch vụ hỏi IP (ipinfo.io) và cho biết **IP thoát, quốc gia, độ trễ**; proxy có IP ngoài Nhật/Hàn được cảnh báo (Dola thường cần IP Nhật hoặc Hàn). Chromium không hỗ trợ socks5 có tài khoản/mật khẩu nên app không cho lưu tổ hợp đó.
   - Cột *Tài khoản đang dùng* cho biết proxy đang gán cho tài khoản nào.
   - **Gán proxy cho tài khoản** ở trang *Quản lý tài khoản*: tích các tài khoản → nút **Gán proxy** → chọn *một proxy cho tất cả*, *chia vòng tròn các proxy* (mỗi tài khoản một IP riêng) hoặc *bỏ proxy*. Cột **Proxy** của bảng tài khoản hiện proxy đang gán (kèm quốc gia IP thoát). App ghi `gateway\accounts\<tên>\proxy.txt`; gateway đọc file đó mỗi lần mở Chromium cho tài khoản (đăng nhập, kiểm tra phiên, tạo video) nên mỗi tài khoản đi qua proxy riêng của mình; tài khoản không có proxy riêng thì dùng biến `DOLA_PROXY` của gateway nếu có, không thì đi thẳng. Đổi proxy có hiệu lực từ lần mở Chromium kế tiếp của tài khoản (đang mở thì đóng rồi mở lại).
3. **Tab Quản lý prompt** có hai trang con (nút chuyển ở đầu trang): **Prompt** và **Kịch bản lớn**. Trang **Prompt**: thư viện prompt. Mỗi prompt có tên, **nội dung nhiều dòng** (giữ nguyên xuống dòng; có thể là bảng phân cảnh, danh sách…), model (Seedance 2.0 / 2.5), tỷ lệ (1:1, 3:4, 4:3, 9:16, 16:9, 21:9), thời lượng (5s, 10s, 30s — đúng các ô chọn của Dola) và ảnh tham chiếu mặc định. Khi gửi, các dòng xuống dòng được gõ bằng Shift+Enter (Enter sẽ gửi tin ngay) nên prompt nhiều dòng đi trọn vẹn trong một lần gửi. *Thêm prompt* (bấm đúp một dòng để sửa), *Nhập từ file* (Excel `.xlsx` — mỗi hàng một prompt, ô nhiều dòng xuống dòng bằng Alt+Enter; `.csv`; `.txt`/`.md`: các prompt cách nhau bằng một dòng `---`). Bấm *File mẫu Excel* để có file mẫu kèm sheet hướng dẫn từng cột, *Xuất CSV*, *Nhân bản*. Tích các prompt rồi bấm **Thêm vào hàng đợi** để chọn tỷ lệ / thời lượng / số video mỗi prompt / độ ưu tiên và bắt đầu làm video.
   - **Nhân vật và bối cảnh** ngay trong khung sửa prompt: thêm **nhiều nhân vật** (mỗi nhân vật có tên, mô tả và **ảnh tham chiếu riêng**, nhiều ảnh được), rồi **bối cảnh** (mô tả + ảnh). Khi gửi, app ghép thành "Nhân vật: Lan (ảnh tham chiếu 2, 3): …  Bối cảnh (ảnh tham chiếu 4): …" rồi tới nội dung prompt, ảnh được đánh số theo đúng thứ tự gửi. Dola chỉ nhận tối đa **10 ảnh** mỗi video (app báo nếu vượt). Nút **Xem prompt sẽ gửi** cho xem trước prompt cuối và thứ tự ảnh. Nhân vật/bối cảnh nhập bằng giao diện (chưa có trong file Excel).
   **Trạng thái của prompt**: mỗi prompt có cột **Trạng thái** — *Chưa làm*, *Đang làm*, *Đã xong*, *Xong một phần* (có cả video xong lẫn lỗi), *Lỗi* — kèm số video (`2 xong · 1 lỗi`), cập nhật theo hàng đợi (thử lại một video lỗi thì prompt tự chuyển lại *Đang làm*; video bị hủy không tính là xong hay lỗi). Có ô **lọc theo trạng thái**, bốn thẻ thống kê ở đầu trang, và nút **Dọn dẹp** để xóa nhanh *các prompt đã xong / bị lỗi / chưa làm video nào* (có hỏi lại), ngoài nút *Xóa đã chọn*.
   **Kịch bản lớn** (trang con của Quản lý prompt): một kịch bản dài (vd. quảng cáo 60–90 giây) tách thành **nhiều phần, mỗi phần một video**, chạy **nối tiếp**:
   - *Thêm kịch bản lớn*: đặt tên, chọn model / tỷ lệ / thời lượng **mỗi phần**, nhập nhân vật + bối cảnh dùng chung cho mọi phần, dán kịch bản đầy đủ rồi bấm **Tách thành các phần** (theo từng "Cảnh N / Phần N / Scene N" hoặc theo đoạn / dòng `---`, gom N cảnh thành một phần). Sửa tay từng phần, thêm, xóa, dời lên xuống.
   - **Chạy kịch bản**: làm phần 1; xong thì app lấy **khung hình cuối** của video (ffmpeg) làm **ảnh tham chiếu đầu tiên** của phần 2, và cứ thế tới phần cuối. Mỗi phần tự được gắn hai câu dặn AI (sửa được trong phần *Câu dặn AI về phần nối tiếp*): ở **cuối** các phần trừ phần cuối — "phía sau còn phần tiếp nối, hãy kết thúc bằng một khung hình rõ nét, ổn định" — và ở **đầu** các phần từ phần 2 — "nối tiếp NGAY phần trước, ảnh tham chiếu đầu tiên là khung hình cuối của phần trước, bắt đầu đúng từ đó". Không cài được ffmpeg thì phần sau vẫn chạy nhưng không có ảnh nối.
   - Bảng các phần cho **xem và sắp xếp**: trạng thái từng phần, video và thời lượng thực tế, *Xem video*, *Ảnh cuối* (khung hình cuối đã lấy), *Xem prompt* (prompt sẽ gửi), *Sửa*, *Làm lại phần này* (chỉ một phần), *Chạy từ đây* (phần này rồi các phần sau), *Dừng*. Phần lỗi làm kịch bản dừng để bạn sửa rồi chạy tiếp, phần đã xong được giữ lại.
   - **Ghép video**: ghép các video theo đúng thứ tự trong bảng thành một file MP4 (thử ghép nguyên bản không giảm chất lượng, không được thì mã hóa lại), lưu cạnh các video; bật *Tự ghép* để làm luôn khi xong. Dời phần lên/xuống cũng là đổi thứ tự ghép.
4. **Tab Tạo video**: không nhập prompt mà **quản lý tiến trình**: *Bắt đầu / Tạm dừng / Tiếp tục* (tạm dừng = không nhận việc mới, video đang làm chạy nốt), *Dừng tất cả* (hủy cả video đang làm), và trên các dòng đã tích: *Dừng tác vụ*, *Thử lại*, *Lên đầu*, *Tăng/Giảm ưu tiên*, *Xem video*, *Mở thư mục*, *Xóa*. Có bộ lọc trạng thái, tìm kiếm và chọn thư mục lưu video. Phần **Cơ chế chạy** (lưu ngay, áp dụng tức thì):
   - *Cơ chế chạy tài khoản*: **Chia đều** (mặc định: mỗi tài khoản một video, chạy hết một vòng mới sang lượt 2) hoặc **Hết từng tài khoản** (dồn vào tài khoản đầu tới khi hết lượt / lỗi mới sang tài khoản kế). Tài khoản lỗi được bỏ qua ở các video khác trong 15 phút (tắt được bằng ô *Bỏ qua tài khoản lỗi*).
   - *Số luồng tối đa*: số video chạy cùng lúc (1–30, mặc định 2), đổi là có hiệu lực ngay. Mỗi luồng mở một cửa sổ Chromium nên máy yếu hãy để số nhỏ.
   - *Ẩn Chromium (chạy nền)*: cửa sổ Chromium nằm ngoài màn hình khi tạo video (vẫn là cửa sổ thật nên Dola không thấy khác; có thể vẫn thấy biểu tượng trên thanh taskbar). Trang Quản lý tài khoản luôn hiện Chromium để đăng nhập.
   - Cột **Dùng để chạy** ở trang Quản lý tài khoản (mặc định đã tích) quyết định tài khoản nào tham gia tạo video; có nút *Đưa vào / Loại khỏi tạo video* cho các dòng đã tích.
   - Sau mỗi video, nhật ký ghi **thời lượng thực tế** đọc từ file (`[Thời lượng] …`) và lời Dola viết kèm (`[Dola nói] …`). Bảng Tạo video có cột **Yêu cầu** và **Thực tế**: khi Dola tạo ngắn/dài hơn thời lượng đã chọn (ví dụ chọn 15s ra 10s) dòng đó đổi màu cam và ghi rõ "do Dola, không phải lỗi của app".
   - Thanh **Tiến độ tổng** ở đáy cửa sổ (hiện ở mọi trang): trung bình tiến độ của cả đợt đang chạy, đạt 100% khi xong toàn bộ.
5. **Tab Cài đặt**: hạn ngạch/ngày, thư mục lưu video, trình duyệt Chromium, và **Khi Dola hỏi lại** (mặc định bật). Prompt của bạn được gửi nguyên văn, app không chèn thêm gì. Dola hay hỏi lại thay vì tạo video (ví dụ "kịch bản 30 giây nhưng tôi chỉ hỗ trợ 4–15 giây, dùng 15 giây được không?" hoặc "bạn có ảnh khuôn mặt không, A hay B?"). App phát hiện câu hỏi đó, tự gõ câu trả lời "có, tiếp tục ngay, chọn B, tự tạo nhân vật" (sửa được trong Cài đặt) để Dola tạo video luôn, tối đa 3 lần mỗi video; nhật ký ghi lại ở dòng `[Dola nói]`. Nếu sau 3 lần Dola vẫn hỏi, tác vụ báo lỗi kèm câu hỏi của Dola.

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
