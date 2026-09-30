using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Helpers;
using DolaCoordinator.Services;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>
/// Bảng quy tắc đặt prompt lấy từ README của repo đang dùng. Mở ra là hiện ngay bản đã lưu rồi tự tải bản mới từ GitHub;
/// repo có bản mới thì quy tắc tự cập nhật.
/// </summary>
public partial class PromptRulesWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<PromptRule> _rules = Array.Empty<PromptRule>();
    private bool _loading;

    public PromptRulesWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        var repo = RepoRulesService.CurrentRepo;
        foreach (var r in PromptRules.KnownRepos.Concat(new[] { repo }).Distinct()) RepoBox.Items.Add(r);
        RepoBox.Text = repo;
        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) => await LoadAsync(repo);
    }

    private string SelectedRepo => (RepoBox.Text ?? string.Empty).Trim();

    private async System.Threading.Tasks.Task LoadAsync(string repo)
    {
        if (_loading) return;
        if (!RepoRulesService.IsValidRepo(repo))
        {
            StatusText.Text = "Tên repo không hợp lệ (dạng chủ-sở-hữu/tên-repo, ví dụ Roins-hub/dola-pool).";
            return;
        }
        _loading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            Show(RepoRulesService.LoadCached(repo));
            StatusText.Text = "Đang kiểm tra repo trên GitHub…";
            var result = await RepoRulesService.RefreshAsync(repo, ct: _cts.Token);
            Show(result);
            if (result.Online) RepoRulesService.CurrentRepo = repo;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Lỗi khi cập nhật: " + ex.Message;
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void Show(RepoRulesResult r)
    {
        _rules = r.Rules;
        RulesList.ItemsSource = r.Rules;
        EmptyText.Visibility = r.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyAllButton.IsEnabled = r.Rules.Count > 0;
        RepoInfo.Text = $"Repo: github.com/{r.Repo}" +
                        (r.Commit != null ? $" · commit mới nhất {r.Commit}" + (r.CommitDate is { } d ? $" ({d:dd/MM/yyyy HH:mm})" : "") : "") +
                        (r.FetchedAt is { } f ? $" · cập nhật lúc {f:dd/MM/yyyy HH:mm}" : "") +
                        $" · {r.Rules.Count} mục";
        StatusText.Text = r.Message;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(SelectedRepo);

    private async void RepoBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || RepoBox.SelectedItem is not string repo) return;
        await LoadAsync(repo);
    }

    private void CopyRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PromptRule rule }) Copy(rule.AsText(), string.IsNullOrWhiteSpace(rule.Title) ? "Đã chép." : $"Đã chép: {rule.Title}");
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) =>
        Copy(string.Join("\n\n", _rules.Select(r => r.AsText())), $"Đã chép {_rules.Count} mục.");

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = done;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không chép được (clipboard đang bị chương trình khác giữ): " + ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
