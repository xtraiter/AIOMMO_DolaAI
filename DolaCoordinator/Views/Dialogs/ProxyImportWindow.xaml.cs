using System.Linq;
using System.Windows;
using DolaCoordinator.Helpers;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Dán nhiều proxy (mỗi dòng một proxy) để nhập một lượt.</summary>
public partial class ProxyImportWindow : Window
{
    public string Text => Body.Text;

    public ProxyImportWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Loaded += (_, _) => Body.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var (proxies, errors) = ProxyParser.ParseLines(Body.Text);
        if (proxies.Count == 0)
        {
            ErrorText.Text = errors.Count > 0 ? errors[0] : "Chưa có proxy nào.";
            return;
        }
        if (errors.Count > 0 &&
            MessageBox.Show($"Có {errors.Count} dòng không đọc được (ví dụ: {errors.First()}). Vẫn nhập {proxies.Count} proxy hợp lệ?",
                "Nhập proxy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
