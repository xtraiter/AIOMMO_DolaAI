using System.Windows;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Thêm / sửa một proxy.</summary>
public partial class ProxyEditorWindow : Window
{
    private readonly ProxyItem? _editing;

    /// <summary>Proxy sau khi lưu (khi sửa: chính đối tượng truyền vào).</summary>
    public ProxyItem Result { get; private set; } = new();

    /// <summary>null = giữ mật khẩu cũ (khi sửa mà không gõ mật khẩu mới); "" = không có mật khẩu.</summary>
    public string? PlainPassword { get; private set; }

    public ProxyEditorWindow(ProxyItem? editing = null)
    {
        _editing = editing;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        SchemeBox.ItemsSource = ProxyItem.Schemes;

        var title = editing == null ? "Thêm proxy" : "Sửa proxy";
        Title = title;
        TitleText.Text = title;
        OkButton.Content = editing == null ? "Lưu proxy" : "Lưu thay đổi";

        if (editing != null)
        {
            NameBox.Text = editing.Name;
            SchemeBox.SelectedItem = editing.Scheme;
            HostBox.Text = editing.Host;
            PortBox.Text = editing.Port.ToString();
            UserBox.Text = editing.Username ?? string.Empty;
            NoteBox.Text = editing.Note ?? string.Empty;
            if (editing.HasAuth)
                HintText.Text = "Để trống ô mật khẩu = giữ mật khẩu đã lưu. Chromium không hỗ trợ socks5 có tài khoản/mật khẩu: dùng http/https nếu proxy cần đăng nhập.";
        }
        else
        {
            SchemeBox.SelectedIndex = 0;
            PortBox.Text = "8080";
            HintText.Text = "Chromium không hỗ trợ socks5 có tài khoản/mật khẩu: dùng http/https nếu proxy cần đăng nhập.";
        }
        Loaded += (_, _) => HostBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { Fail("Nhập host hoặc IP của proxy."); return; }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) { Fail("Port phải là số từ 1 đến 65535."); return; }
        var scheme = (SchemeBox.SelectedItem as string) ?? "http";
        var user = UserBox.Text.Trim();
        var pass = PasswordBox.Password;
        if (scheme == "socks5" && user.Length > 0)
        {
            Fail("Chromium không hỗ trợ proxy socks5 có tài khoản/mật khẩu. Đổi sang http/https hoặc dùng proxy không cần đăng nhập.");
            return;
        }

        var p = _editing ?? new ProxyItem();
        p.Name = NameBox.Text.Trim();
        p.Scheme = scheme;
        p.Host = host;
        p.Port = port;
        p.Username = user.Length == 0 ? null : user;
        p.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        if (p.Name.Length == 0) p.Name = $"{host}:{port}";

        // sửa mà không gõ mật khẩu mới = giữ mật khẩu cũ; bỏ tài khoản thì mật khẩu cũng bỏ
        PlainPassword = user.Length == 0 ? string.Empty : (pass.Length > 0 ? pass : (_editing != null ? null : string.Empty));
        Result = p;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
