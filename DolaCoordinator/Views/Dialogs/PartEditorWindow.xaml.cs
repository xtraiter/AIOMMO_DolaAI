using System.Windows;
using DolaCoordinator.Helpers;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Sửa nội dung của một phần trong kịch bản lớn.</summary>
public partial class PartEditorWindow : Window
{
    public string PartText => Body.Text.Trim('\r', '\n');

    public PartEditorWindow(string heading, string text)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Title = heading;
        HeadingText.Text = heading;
        Body.Text = text;
        Loaded += (_, _) => Body.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Body.Text))
        {
            ErrorText.Text = "Nội dung đang trống.";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
