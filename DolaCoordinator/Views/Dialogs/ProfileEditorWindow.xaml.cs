using System.Windows;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

public partial class ProfileEditorWindow : Window
{
    public bool IsEditMode { get; }

    public string ProfileName { get; set; } = string.Empty;
    public int Count { get; set; } = 1;

    public string Notes { get; set; } = string.Empty;
    public bool OpenAfterCreate { get; set; } = true;

    /// <summary>Cách đăng nhập (thủ công / Google / Facebook / cookie Facebook) và việc làm sau khi xong.</summary>
    public LoginOptions Login { get; private set; } = new();

    /// <summary>Thêm tài khoản mới.</summary>
    public ProfileEditorWindow() : this(null) { }

    /// <summary>Sửa tài khoản có sẵn (chỉ đổi được ghi chú: tên là khóa của gateway).</summary>
    public ProfileEditorWindow(AccountProfile? editing, LoginOptions? savedLogin = null, string? statusNote = null)
    {
        if (savedLogin != null) Login = savedLogin;
        IsEditMode = editing != null;

        InitializeComponent();
        DarkTitleBar.Attach(this);

        if (editing != null)
        {
            ProfileName = editing.Name;
            Notes = editing.Notes ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(statusNote))
            {
                StatusNoteText.Text = statusNote;
                StatusNoteBox.Visibility = Visibility.Visible;
            }
            Title = "Sửa tài khoản";
            TitleText.Text = "Sửa tài khoản";
            SubtitleText.Text = "Tên là tên thư mục accounts/… của gateway nên không đổi được. Sửa được cách đăng nhập, tài khoản/mật khẩu đã lưu và ghi chú.";
            NameBox.IsReadOnly = true;
            CountPanel.Visibility = Visibility.Collapsed;
            CountColumn.Width = new GridLength(0);
            OpenAfterBox.Visibility = Visibility.Collapsed;
            OkButton.Content = "Lưu thay đổi";
        }
        else
        {
            Title = "Thêm tài khoản Dola";
            TitleText.Text = "Thêm tài khoản Dola";
            SubtitleText.Text = "Mỗi tài khoản là một profile Chromium riêng của gateway (accounts/<tên>). Đăng nhập thủ công hoặc tự động bằng Google / Facebook / cookie Facebook; lần sau mở lại vẫn còn phiên.";
            OkButton.Content = "Tạo tài khoản";
        }

        DataContext = this;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
        {
            ProfileName = ProfileName.Trim();
            if (!GatewayLocator.IsValidAccountName(ProfileName))
            {
                ShowError("Tên chỉ gồm A-Z a-z 0-9 _ - (không dấu, không khoảng trắng), tối đa 32 ký tự — đúng quy định của gateway.");
                return;
            }
            if (Count < 1 || Count > 100)
            {
                ShowError("Số lượng phải từ 1 đến 100.");
                return;
            }

            if (Login.IsAutomatic && Count > 1)
            {
                ShowError("Đăng nhập tự động chỉ dùng khi tạo 1 tài khoản (mỗi tài khoản có thông tin và có thể có captcha/2FA riêng).");
                return;
            }
        }

        // Sửa tài khoản + bỏ tích "Ghi nhớ" = xóa thông tin đã lưu, không cần đủ mật khẩu
        var loginError = IsEditMode && !Login.Remember ? null : Login.Validate();
        if (loginError != null)
        {
            ShowError(loginError);
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
