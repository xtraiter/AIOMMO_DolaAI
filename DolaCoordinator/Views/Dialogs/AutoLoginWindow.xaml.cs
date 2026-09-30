using System.Windows;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Hộp thoại đăng nhập tự động cho một tài khoản đã có (đăng nhập lại khi phiên hết hạn).</summary>
public partial class AutoLoginWindow : Window
{
    public LoginOptions Options { get; }

    public AutoLoginWindow(string accountName, LoginOptions initial, string? note = null)
    {
        Options = initial;

        InitializeComponent();
        DarkTitleBar.Attach(this);

        if (!string.IsNullOrWhiteSpace(note))
        {
            NoteText.Text = note;
            NoteBox.Visibility = Visibility.Visible;
        }
        Title = $"Đăng nhập tự động — {accountName}";
        TitleText.Text = $"Đăng nhập tự động — {accountName}";
        DataContext = this;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var error = Options.Validate();
        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
