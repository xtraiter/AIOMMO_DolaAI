using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Một lựa chọn trong ô Proxy: Id = null nghĩa là chưa gán proxy.</summary>
public sealed record ProxyChoice(string? Id, string Label);

public partial class ProfileEditorWindow : Window
{
    public const string NoProxyLabel = "Chưa gán proxy (đi thẳng bằng IP nhà bạn)";

    private readonly bool _hasProxies;

    public bool IsEditMode { get; }

    /// <summary>Proxy đã chọn (ProxyItem.Id); null = chưa gán proxy.</summary>
    public string? SelectedProxyId => (ProxyBox.SelectedItem as ProxyChoice)?.Id;

    public string ProfileName { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
    public bool OpenAfterCreate { get; set; } = true;

    /// <summary>Cách đăng nhập (thủ công / Google / Facebook / cookie Facebook) và việc làm sau khi xong.</summary>
    public LoginOptions Login { get; private set; } = new();

    /// <summary>Thêm tài khoản mới.</summary>
    public ProfileEditorWindow() : this(null) { }

    /// <summary>Sửa tài khoản có sẵn (chỉ đổi được ghi chú: tên là khóa của gateway).</summary>
    public ProfileEditorWindow(AccountProfile? editing, LoginOptions? savedLogin = null, string? statusNote = null,
                               IReadOnlyList<ProxyItem>? proxies = null)
    {
        if (savedLogin != null) Login = savedLogin;
        IsEditMode = editing != null;

        InitializeComponent();
        DarkTitleBar.Attach(this);

        // ô Proxy: "Chưa gán proxy" + danh sách proxy (kèm quốc gia IP thoát nếu đã kiểm tra)
        var choices = new List<ProxyChoice> { new(null, NoProxyLabel) };
        foreach (var px in proxies ?? new List<ProxyItem>())
        {
            var where = px.LastOk == true && !string.IsNullOrEmpty(px.LastCountry) ? $" · {px.LastCountry}" : string.Empty;
            choices.Add(new ProxyChoice(px.Id, $"{px.DisplayName} · {px.Address}{where}"));
        }
        _hasProxies = choices.Count > 1;
        ProxyBox.ItemsSource = choices;
        ProxyBox.SelectedItem = choices.FirstOrDefault(c => c.Id == editing?.ProxyId) ?? choices[0];
        UpdateProxyHint();

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

    private void ProxyBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProxyHint();

    private void UpdateProxyHint()
    {
        if (ProxyHint == null) return;
        if (!_hasProxies)
            ProxyHint.Text = "Chưa có proxy nào. Có thể tạo tài khoản trước (bỏ tích đăng nhập ngay), thêm proxy ở mục Quản lý proxy rồi bấm 'Gán proxy' và đăng nhập sau.";
        else if (SelectedProxyId == null)
            ProxyHint.Text = "Chưa gán proxy: tài khoản sẽ đăng nhập và chạy bằng IP nhà bạn. Gán proxy ở đây (hoặc bỏ tích 'đăng nhập ngay' bên dưới, gán sau) nếu muốn dùng IP riêng.";
        else
            ProxyHint.Text = "Chromium của tài khoản này sẽ đi qua proxy đã chọn ngay từ lần đăng nhập đầu tiên.";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
