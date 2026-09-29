using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

public partial class LoginOptionsPanel : UserControl
{
    public LoginOptionsPanel()
    {
        InitializeComponent();
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
