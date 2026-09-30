using System.Windows.Controls;

namespace DolaCoordinator.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    // Nhật ký luôn cuộn xuống dòng mới nhất
    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.ScrollToEnd();
    }
}
