using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DolaCoordinator.Views;

public partial class ProfilesView : UserControl
{
    // Ô "Dùng để chạy": binding hai chiều đã đổi giá trị trong phiên; lưu xuống DB
    private void UseForRender_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DolaCoordinator.Models.AccountProfile p } && DataContext is DolaCoordinator.ViewModels.ProfilesViewModel vm)
            vm.PersistUseForRender(p);
    }

    public ProfilesView()
    {
        InitializeComponent();
    }

    private void IoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var menu = (ContextMenu)FindResource("IoMenu");
        menu.DataContext = DataContext; // ContextMenu nằm ngoài visual tree nên phải gán thủ công
        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Nhật ký luôn cuộn xuống dòng mới nhất
    private void Log_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.ScrollToEnd();
    }
}
