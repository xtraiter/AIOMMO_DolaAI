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
