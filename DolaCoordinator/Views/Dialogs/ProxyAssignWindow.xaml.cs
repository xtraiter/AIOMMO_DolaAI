using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

public enum ProxyAssignMode
{
    /// <summary>Một proxy cho mọi tài khoản đã tích.</summary>
    One,

    /// <summary>Chia vòng tròn các proxy trong danh sách.</summary>
    RoundRobin,

    /// <summary>Bỏ proxy.</summary>
    None,
}

/// <summary>Chọn cách gán proxy cho các tài khoản đã tích.</summary>
public partial class ProxyAssignWindow : Window
{
    private readonly IReadOnlyList<ProxyItem> _proxies;

    public ProxyAssignMode Mode { get; private set; } = ProxyAssignMode.One;

    /// <summary>Proxy được chọn khi Mode = One.</summary>
    public ProxyItem? Proxy { get; private set; }

    public ProxyAssignWindow(IReadOnlyList<ProxyItem> proxies, int accountCount)
    {
        _proxies = proxies;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Title = "Gán proxy";
        TitleText.Text = $"Gán proxy cho {accountCount} tài khoản";
        SubText.Text = "Proxy được áp dụng từ lần mở Chromium kế tiếp của tài khoản (đang mở thì đóng rồi mở lại).";
        ProxyBox.ItemsSource = proxies;
        if (proxies.Count > 0) ProxyBox.SelectedIndex = 0;
        RoundHint.Text = proxies.Count == 0
            ? "Chưa có proxy nào: thêm ở mục Quản lý proxy."
            : $"{proxies.Count} proxy cho {accountCount} tài khoản" + (proxies.Count < accountCount ? " — sẽ có tài khoản dùng chung proxy." : ".");
        OneRadio.IsEnabled = RoundRadio.IsEnabled = proxies.Count > 0;
        if (proxies.Count == 0) NoneRadio.IsChecked = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (NoneRadio.IsChecked == true)
        {
            Mode = ProxyAssignMode.None;
        }
        else if (RoundRadio.IsChecked == true)
        {
            Mode = ProxyAssignMode.RoundRobin;
        }
        else
        {
            Mode = ProxyAssignMode.One;
            Proxy = ProxyBox.SelectedItem as ProxyItem;
            if (Proxy == null)
            {
                ErrorText.Text = "Chọn một proxy trong danh sách.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
