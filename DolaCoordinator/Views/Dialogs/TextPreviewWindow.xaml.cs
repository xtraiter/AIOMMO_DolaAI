using System;
using System.Windows;
using DolaCoordinator.Helpers;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Cửa sổ xem một đoạn văn bản dài (prompt sẽ gửi, nội dung ghép...) có nút chép.</summary>
public partial class TextPreviewWindow : Window
{
    public TextPreviewWindow(string heading, string text)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Title = heading;
        HeadingText.Text = heading;
        Body.Text = text;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Body.Text);
            StatusText.Text = "Đã chép.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không chép được: " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
