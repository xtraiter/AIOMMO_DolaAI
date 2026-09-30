using System.Linq;
using System.Windows;
using DolaCoordinator.Helpers;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Chọn cách đưa các prompt đã tích vào hàng đợi: tỷ lệ/thời lượng (hoặc theo từng prompt), số video, độ ưu tiên.</summary>
public partial class AddToQueueWindow : Window
{
    private const string PerPrompt = "Theo từng prompt";

    /// <summary>null = dùng tỷ lệ mặc định của từng prompt.</summary>
    public string? RatioOverride { get; private set; }

    /// <summary>null = dùng thời lượng mặc định của từng prompt.</summary>
    public int? DurationOverride { get; private set; }

    public int Copies { get; private set; } = 1;

    /// <summary>0 thường, 1 cao, 2 khẩn.</summary>
    public int Priority { get; private set; }

    public bool GoToQueue { get; private set; } = true;

    public AddToQueueWindow(int promptCount)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Title = "Thêm vào hàng đợi";
        SummaryText.Text = $"{promptCount} prompt đã chọn. Mỗi prompt được gửi nguyên văn (giữ xuống dòng) cho Dola.";
        RatioBox.ItemsSource = new[] { PerPrompt }.Concat(PromptFileParser.Ratios).ToList();
        DurationBox.ItemsSource = new[] { PerPrompt }.Concat(PromptFileParser.Durations.Select(d => d.ToString())).ToList();
        RatioBox.SelectedIndex = 0;
        DurationBox.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CopiesBox.Text.Trim(), out var copies) || copies < 1 || copies > 20)
        {
            ErrorText.Text = "Số video mỗi prompt phải từ 1 đến 20.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Copies = copies;
        RatioOverride = RatioBox.SelectedItem is string r && r != PerPrompt ? r : null;
        DurationOverride = DurationBox.SelectedItem is string d && int.TryParse(d, out var seconds) ? seconds : null;
        Priority = PriorityBox.SelectedIndex;
        GoToQueue = GoBox.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
