using System.Collections.Generic;
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

    /// <summary>null = dùng model mặc định của từng prompt.</summary>
    public string? ModelOverride { get; private set; }

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
        ModelBox.ItemsSource = new[] { PerPrompt }.Concat(PromptFileParser.ModelLabels).ToList();
        ModelBox.SelectedIndex = 0;
        RatioBox.SelectedIndex = 0;
        RefreshDurations();
        ModelBox.SelectionChanged += (_, _) => RefreshDurations();
    }

    /// <summary>Thời lượng theo model đã chọn (2.0: 5/10/15, 2.5: 5/10/30); "Theo từng prompt" → hiện đủ 5/10/15/30.</summary>
    private void RefreshDurations()
    {
        var previous = DurationBox.SelectedItem as string;
        var model = ModelBox.SelectedItem as string;
        var seconds = model is null || model == PerPrompt ? PromptFileParser.Durations : PromptFileParser.DurationsFor(model);
        DurationBox.ItemsSource = new[] { PerPrompt }.Concat(seconds.Select(d => d.ToString())).ToList();
        DurationBox.SelectedItem = previous is not null && ((IEnumerable<string>)DurationBox.ItemsSource).Contains(previous) ? previous : PerPrompt;
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
        ModelOverride = ModelBox.SelectedItem is string m && m != PerPrompt ? PromptFileParser.NormalizeModel(m) : null;
        RatioOverride = RatioBox.SelectedItem is string r && r != PerPrompt ? r : null;
        DurationOverride = DurationBox.SelectedItem is string d && int.TryParse(d, out var seconds) ? seconds : null;
        Priority = PriorityBox.SelectedIndex;
        GoToQueue = GoBox.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
