using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DolaCoordinator.Models;
using DolaCoordinator.ViewModels;

namespace DolaCoordinator.Views;

public partial class PromptsView : UserControl
{
    public PromptsView()
    {
        InitializeComponent();
        // mỗi lần mở trang: đối chiếu số "đang làm" của prompt với hàng đợi thật
        Loaded += (_, _) => (DataContext as PromptsViewModel)?.RefreshStatusesCommand.Execute(null);
    }

    // Nút "Dọn dẹp ▾" mở menu xóa nhanh theo trạng thái
    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    // Bấm đúp vào một dòng = sửa prompt đó
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PromptsViewModel vm) return;
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is DataGridRow { Item: PromptItem item })
            vm.Edit(item);
    }
}
