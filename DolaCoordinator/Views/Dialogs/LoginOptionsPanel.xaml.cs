using System;
using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

public partial class LoginOptionsPanel : UserControl
{
    /// <summary>Phát ra UID khi người dùng dán nguyên dòng mua (để cửa sổ cha gợi ý đặt tên tài khoản).</summary>
    public event Action<string>? AccountLineParsed;

    private bool _rewritingCookie;
    public LoginOptionsPanel()
    {
        InitializeComponent();
        // Ô mật khẩu không binding được: đổ giá trị đã lưu vào khi có LoginOptions (sửa tài khoản / đăng nhập lại)
        DataContextChanged += (_, _) => Prefill();
        Loaded += (_, _) => Prefill();
    }

    private void Prefill()
    {
        if (DataContext is not LoginOptions o) return;
        if (PasswordBox.Password != o.Password) PasswordBox.Password = o.Password;
        if (TotpBox.Password != o.Totp) TotpBox.Password = o.Totp;
    }

    // PasswordBox không hỗ trợ binding nên chuyển giá trị vào LoginOptions bằng code-behind
    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginOptions o) o.Password = PasswordBox.Password;
    }

    private void Totp_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginOptions o) o.Totp = TotpBox.Password.Replace(" ", string.Empty);
    }

    // Dán nguyên dòng mua (UID|mật khẩu|cookie|token|…): tách lấy cookie, giữ UID + mật khẩu để đăng nhập lại.
    private void Cookie_Changed(object sender, TextChangedEventArgs e)
    {
        if (_rewritingCookie || DataContext is not LoginOptions o) return;
        if (!FacebookAccountLine.TryParse(CookieBox.Text, out var line)) return;
        // Chỉ xử lý khi thực sự là dòng nhiều cột (ô còn chứa dấu '|'), tránh đụng vào cookie thuần.
        if (!CookieBox.Text.Contains('|')) return;

        _rewritingCookie = true;
        try
        {
            CookieBox.Text = line.Cookie;
            CookieBox.CaretIndex = CookieBox.Text.Length;
        }
        finally { _rewritingCookie = false; }

        o.Cookie = line.Cookie;
        if (!string.IsNullOrEmpty(line.Uid)) o.Email = line.Uid;      // dùng cho đăng nhập lại kiểu Facebook thường
        if (!string.IsNullOrEmpty(line.Password)) o.Password = line.Password;

        var bits = "cookie";
        if (!string.IsNullOrEmpty(line.Uid)) bits += $" + UID {line.Uid}";
        if (!string.IsNullOrEmpty(line.Password)) bits += " + mật khẩu (giữ để đăng nhập lại)";
        CookieParsedHint.Text = $"Đã tách: {bits}. Token bị bỏ.";
        CookieParsedHint.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(line.Uid)) AccountLineParsed?.Invoke(line.Uid);
    }
}
