using System.Windows;
using DolaCoordinator.Helpers;
using DolaCoordinator.ViewModels;

namespace DolaCoordinator;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = viewModel;
    }
}
