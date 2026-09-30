using System;
using System.Windows;
using DolaCoordinator.Helpers;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Hiện MỘT đoạn prompt quy tắc (soạn theo repo gateway đang dùng) và nút chép để dán cho AI viết prompt.</summary>
public partial class PromptRulesWindow : Window
{
    public PromptRulesWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        RulesBox.Text = PromptRules.RulesPrompt;
        SourceText.Text = $"Soạn theo repo github.com/{PromptRules.SourceRepo}. Bấm nút để chép, dán cho AI (ChatGPT, Claude…), rồi gõ ý tưởng video ở cuối.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PromptRules.RulesPrompt);
            StatusText.Text = "Đã chép.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không chép được (clipboard đang bị chương trình khác giữ): " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
