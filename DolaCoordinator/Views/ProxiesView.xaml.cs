using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DolaCoordinator.Models;
using DolaCoordinator.ViewModels;

namespace DolaCoordinator.Views;

public partial class ProxiesView : UserControl
{
    public ProxiesView()
    {
        InitializeComponent();
        // mỗi lần mở trang: đọc lại số tài khoản đang dùng từng proxy
        Loaded += (_, _) => (DataContext as ProxiesViewModel)?.Load();
    }

    // Bấm đúp vào một dòng = sửa proxy đó
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ProxiesViewModel vm) return;
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is DataGridRow { Item: ProxyItem item })
            vm.EditProxyCommand.Execute(item);
    }
}
