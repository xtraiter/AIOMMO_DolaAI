using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using DolaCoordinator.Models;

namespace DolaCoordinator.Helpers;

/// <summary>Tạo file Excel mẫu / xuất Excel cho prompt và tài khoản (có sheet "Hướng dẫn" ghi rõ từng cột và từng loại).</summary>
public static class ImportTemplates
{
    private const string HeaderColor = "#0F766E";

    // ------------------------------------------------------------------ prompt

    public static readonly string[] PromptHeaders =
    {
        "Tên prompt", "Nội dung (nhiều dòng)", "Tỷ lệ", "Thời lượng (giây)", "Model", "Ghi chú", "Ảnh tham chiếu (đường dẫn, cách nhau bằng dấu ;)",
    };

    public static List<PromptItem> SamplePrompts() => new()
    {
        new PromptItem
        {
            Title = "Quảng cáo serum 30s (mẫu nhiều dòng)",
            Text = "Cảnh 1: Cận cảnh cô gái 25 tuổi thức dậy, da sạm, quầng thâm.\n" +
                   "Cảnh 2: Cô soi gương, sờ vào má khô ráp, vẻ lo lắng.\n" +
                   "Cảnh 3: Lọ serum trên mặt nước, một giọt rơi tạo gợn sóng.\n" +
                   "Cảnh 4: Serum thoa lên má, da căng bóng.\n" +
                   "Cảnh 5: Cô mỉm cười, làn da rạng rỡ dưới nắng.\n" +
                   "Cảnh 6: Lọ serum cạnh logo, chữ ƯU ĐÃI 30% - MUA NGAY.",
            Ratio = "9:16", Duration = 30, Model = "seedance-2.5",
            Notes = "Xuống dòng trong ô bằng Alt+Enter; toàn bộ ô là MỘT prompt. Không ghi thời lượng (30s, 0-3s...) trong nội dung: chọn ở cột Thời lượng.",
        },
        new PromptItem
        {
            Title = "Biển hoàng hôn (mẫu một dòng)",
            Text = "Cảnh biển lúc hoàng hôn, sóng nhẹ, máy quay lướt chậm từ trái sang phải.",
            Ratio = "16:9", Duration = 10, Model = "seedance-2.5", Notes = "Ví dụ ngắn",
        },
    };

    public static void WritePromptWorkbook(string path, IEnumerable<PromptItem> items)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Prompt");
        Header(ws, PromptHeaders);

        var widths = new double[] { 34, 80, 10, 14, 16, 34, 46 };
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.Column(2).Style.Alignment.WrapText = true;
        ws.Column(6).Style.Alignment.WrapText = true;
        ws.Column(7).Style.Alignment.WrapText = true;
        ws.Range(2, 1, 2000, 7).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

        var r = 2;
        foreach (var p in items)
        {
            ws.Cell(r, 1).Value = p.Title;
            ws.Cell(r, 2).Value = p.Text.Replace("\r\n", "\n");
            ws.Cell(r, 3).Value = p.Ratio;
            ws.Cell(r, 4).Value = p.Duration;
            ws.Cell(r, 5).Value = p.Model;
            ws.Cell(r, 6).Value = p.Notes ?? string.Empty;
            ws.Cell(r, 7).Value = string.Join(";", p.ReferenceLocalPaths);
            var lines = Math.Max(1, p.Text.Split('\n').Length);
            ws.Row(r).Height = Math.Min(400, Math.Max(20, lines * 15 + 6));
            r++;
        }

        ws.Range(2, 3, 2000, 3).CreateDataValidation().List(string.Join(",", PromptFileParser.Ratios), true);
        ws.Range(2, 4, 2000, 4).CreateDataValidation().List(string.Join(",", PromptFileParser.Durations), true);
        ws.Range(2, 5, 2000, 5).CreateDataValidation().List(string.Join(",", PromptFileParser.Models), true);
        ws.SheetView.FreezeRows(1);

        var g = wb.Worksheets.Add(TableFile.HelpSheetName);
        Guide(g, "HƯỚNG DẪN NHẬP PROMPT", new[]
        {
            ("Mỗi dòng của sheet 'Prompt'", "là MỘT prompt. Ô 'Nội dung' có thể nhiều dòng (Alt+Enter), dạng bảng, danh sách… — được gửi nguyên văn."),
            ("Tên prompt", "Tên gợi nhớ. Bỏ trống thì lấy dòng đầu của nội dung."),
            ("Nội dung (nhiều dòng)", "BẮT BUỘC. Dòng không có nội dung bị bỏ qua."),
            ("Tỷ lệ", "1:1, 3:4, 4:3, 9:16, 16:9, 21:9 (giống ô chọn của Dola). Bỏ trống = 9:16."),
            ("Thời lượng (giây)", "5, 10 hoặc 30 (giống ô chọn của Dola). Số khác được đổi về giá trị gần nhất. Bỏ trống = 30. 30 giây chỉ có ở Seedance 2.5. ĐỪNG ghi thời lượng trong nội dung (30s, 0-3s, 00:00-00:03...): Dola sẽ hỏi lại thay vì tạo video — app tự bỏ các chỗ đó khi gửi."),
            ("Model", "seedance-2.0 hoặc seedance-2.5. Bỏ trống = seedance-2.0 (tự đổi sang 2.5 nếu chọn 30 giây)."),
            ("Ghi chú", "Tùy chọn."),
            ("Ảnh tham chiếu", "Tùy chọn: đường dẫn ảnh trên máy này, nhiều ảnh cách nhau bằng dấu ; (ví dụ C:\\anh\\a.png;C:\\anh\\b.jpg)."),
            ("Xóa dòng mẫu", "Hai dòng mẫu trong sheet Prompt sẽ được nhập như prompt thật — hãy xóa hoặc sửa trước khi nhập."),
        });
        wb.SaveAs(path);
    }

    // ------------------------------------------------------------------ tài khoản

    public static readonly string[] AccountHeaders =
    {
        "Tên tài khoản", "Loại đăng nhập", "Email / SĐT", "Mật khẩu", "Khóa 2FA (TOTP)", "Cookie", "Ghi chú", "Dùng để chạy (Có/Không)", "Sau khi đăng nhập (Đóng/Giữ)",
    };

    public static readonly string[] AccountKinds =
    {
        "Thủ công", "Google (Gmail)", "Facebook (email/SĐT + mật khẩu)", "Facebook cookie", "Cookie Dola",
    };

    private static readonly string[][] AccountSamples =
    {
        new[] { "(mẫu) thu_cong", "Thủ công", "", "", "", "", "Tạo tài khoản trống, tự đăng nhập bằng tay sau", "Có", "Đóng" },
        new[] { "(mẫu) gmail", "Google (Gmail)", "ten.ban@gmail.com", "mat-khau-gmail", "ABCD EFGH IJKL MNOP", "", "Khóa 2FA để trống nếu không bật 2 bước hoặc muốn tự nhập mã", "Có", "Đóng" },
        new[] { "(mẫu) facebook", "Facebook (email/SĐT + mật khẩu)", "0912345678", "mat-khau-facebook", "", "", "Email hoặc số điện thoại Facebook", "Có", "Đóng" },
        new[] { "(mẫu) fb_cookie", "Facebook cookie", "", "", "", "c_user=1000xxxxxxxxx; xs=xx%3Axxxxxxxx; datr=xxxx; fr=xxxx", "Cookie Facebook phải có c_user và/hoặc xs", "Có", "Đóng" },
        new[] { "(mẫu) dola_cookie", "Cookie Dola", "", "", "", "sessionid=xxxxxxxx; sid_guard=xxxx; uid_tt=xxxx", "Cookie dola.com phải có sessionid=", "Có", "Đóng" },
    };

    public static void WriteAccountTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Tài khoản");
        Header(ws, AccountHeaders);

        var widths = new double[] { 22, 34, 26, 22, 24, 56, 44, 20, 26 };
        for (var i = 0; i < widths.Length; i++)
        {
            ws.Column(i + 1).Width = widths[i];
            ws.Column(i + 1).Style.NumberFormat.Format = "@"; // giữ nguyên số điện thoại có số 0 đầu, mật khẩu toàn số
        }
        ws.Column(6).Style.Alignment.WrapText = true;
        ws.Column(7).Style.Alignment.WrapText = true;

        for (var i = 0; i < AccountSamples.Length; i++)
            for (var c = 0; c < AccountSamples[i].Length; c++)
            {
                var cell = ws.Cell(i + 2, c + 1);
                cell.Value = AccountSamples[i][c];
                cell.Style.Font.FontColor = XLColor.FromHtml("#6B7280");
                cell.Style.Font.Italic = true;
            }

        ws.Range(2, 2, 2000, 2).CreateDataValidation().List(string.Join(",", AccountKinds), true);
        ws.Range(2, 8, 2000, 8).CreateDataValidation().List("Có,Không", true);
        ws.Range(2, 9, 2000, 9).CreateDataValidation().List("Đóng,Giữ", true);
        ws.SheetView.FreezeRows(1);

        var g = wb.Worksheets.Add(TableFile.HelpSheetName);
        Guide(g, "HƯỚNG DẪN NHẬP TÀI KHOẢN", new[]
        {
            ("Mỗi dòng sheet 'Tài khoản'", "là một tài khoản Dola. Các dòng có tên bắt đầu bằng \"(mẫu)\" là ví dụ và KHÔNG bao giờ được nhập; thêm dòng thật bên dưới."),
            ("Tên tài khoản", "Không bắt buộc. Bỏ trống thì lấy từ email (phần trước @). Chỉ gồm A-Z a-z 0-9 _ - (tối đa 32 ký tự); ký tự khác được đổi thành _. Trùng tên tài khoản đã có thì bị bỏ qua."),
            ("Loại đăng nhập", "Chọn trong danh sách. Bỏ trống thì app tự đoán: có cookie c_user/xs → Facebook cookie; có sessionid → Cookie Dola; email @gmail.com + mật khẩu → Google; email/SĐT + mật khẩu → Facebook; còn lại → Thủ công."),
            ("Thủ công", "Không cần điền gì thêm. Tạo tài khoản trống; sau đó bạn Mở và tự đăng nhập."),
            ("Google (Gmail)", "Cần Email + Mật khẩu. Khóa 2FA (TOTP, chuỗi Base32) để app tự điền mã 2 bước; không có thì tự nhập mã khi cửa sổ dừng chờ."),
            ("Facebook (email/SĐT + mật khẩu)", "Cần Email hoặc SĐT + Mật khẩu. Khóa 2FA tùy chọn như trên. Captcha / kiểm tra bảo mật bạn tự xử lý."),
            ("Facebook cookie", "Cần cột Cookie chứa c_user= và/hoặc xs=. App nạp cookie, vào Facebook xác nhận, rồi đăng nhập Dola bằng Facebook."),
            ("Cookie Dola", "Cần cột Cookie chứa sessionid=. Dùng thẳng, không cần đăng nhập; nên bấm Kiểm tra phiên sau khi nhập."),
            ("Dùng để chạy", "Có (mặc định) = tham gia chạy ở trang Vận hành; Không = loại khỏi vận hành (chỉ áp dụng được với tài khoản đã có phiên)."),
            ("Sau khi đăng nhập", "Đóng (mặc định) = tự đóng trình duyệt khi đăng nhập xong; Giữ = giữ cửa sổ mở."),
            ("BẢO MẬT", "File này chứa mật khẩu / cookie ở dạng chữ thường. Sau khi nhập xong hãy XÓA file. App lưu thông tin đăng nhập đã mã hóa bằng Windows DPAPI (chỉ giải mã được trên tài khoản Windows này)."),
        });
        wb.SaveAs(path);
    }

    /// <summary>Bản CSV của file mẫu tài khoản (cho ai không dùng Excel).</summary>
    public static string AccountTemplateCsv()
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { string.Join(",", AccountHeaders.Select(Q)) };
        lines.AddRange(AccountSamples.Select(row => string.Join(",", row.Select(Q))));
        return string.Join("\r\n", lines) + "\r\n";
    }

    // ------------------------------------------------------------------ chung

    private static void Header(IXLWorksheet ws, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        var range = ws.Range(1, 1, 1, headers.Length);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderColor);
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 26;
    }

    private static void Guide(IXLWorksheet ws, string title, (string Item, string Text)[] rows)
    {
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Column(1).Width = 34;
        ws.Column(2).Width = 120;
        ws.Column(2).Style.Alignment.WrapText = true;
        var r = 3;
        foreach (var (item, text) in rows)
        {
            ws.Cell(r, 1).Value = item;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = text;
            ws.Range(r, 1, r, 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            r++;
        }
    }
}
