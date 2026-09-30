using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

public partial class LoginOptionsPanel : UserControl
{
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
}
