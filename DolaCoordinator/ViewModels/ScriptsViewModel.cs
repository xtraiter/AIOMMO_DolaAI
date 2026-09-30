using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Queue;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Services.Video;
using DolaCoordinator.Views.Dialogs;

namespace DolaCoordinator.ViewModels;

/// <summary>
/// Trang "Kịch bản lớn": một kịch bản dài tách thành nhiều phần (mỗi phần một video), chạy NỐI TIẾP: video phần N xong thì lấy
/// khung hình cuối làm ảnh mở đầu cho phần N+1, rồi ghép tất cả thành một video. Người dùng xem, sắp xếp, sửa, làm lại từng phần.
/// </summary>
public partial class ScriptsViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private readonly ITaskDispatcher _dispatcher;
    private readonly QueueViewModel _queue;
    private readonly IVideoTools _tools;

    /// <summary>Kịch bản đang chạy nối tiếp (phần sau tự chạy khi phần trước xong).</summary>
    private readonly HashSet<string> _chains = new();

    public ObservableCollection<ScriptProject> Projects { get; } = new();

    /// <summary>Các phần của kịch bản đang chọn (theo thứ tự hiển thị / ghép).</summary>
    public ObservableCollection<ScriptPart> Parts { get; } = new();

    [ObservableProperty] private ScriptProject? _selectedProject;
    [ObservableProperty] private string _statusText = "Tạo một kịch bản lớn để bắt đầu: dán kịch bản dài, tách thành các phần rồi chạy nối tiếp.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunProject))]
    private bool _isChainRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    private bool _isMerging;

    public bool CanRunProject => !IsChainRunning;
    public bool CanMerge => !IsMerging;
    [ObservableProperty] private string _progressText = string.Empty;

    public int TotalCount => Projects.Count;
    public bool HasProject => SelectedProject != null;
    public bool HasNoProject => SelectedProject == null;
    public bool HasFfmpeg => _tools.IsAvailable;
    public string FfmpegWarning => _tools.IsAvailable ? string.Empty
        : "Chưa có ffmpeg: không lấy được khung hình cuối và không ghép được video. Bản đóng gói có sẵn ffmpeg trong thư mục 'tools'.";

    public ScriptsViewModel(IDatabaseService db, ITaskDispatcher dispatcher, QueueViewModel queue, IVideoTools tools)
    {
        _db = db;
        _dispatcher = dispatcher;
        _queue = queue;
        _tools = tools;

        foreach (var p in _db.GetAllProjects()) Projects.Add(p);
        Projects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TotalCount));
        SelectedProject = Projects.FirstOrDefault();
        _dispatcher.TaskUpdated += OnTaskUpdated;
        ReconcileWithQueue();
    }

    partial void OnSelectedProjectChanged(ScriptProject? value)
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasNoProject));
        LoadParts();
        IsChainRunning = value != null && _chains.Contains(value.Id);
    }

    private void LoadParts()
    {
        Parts.Clear();
        if (SelectedProject == null) return;
        Renumber(SelectedProject);
        foreach (var part in SelectedProject.Parts) Parts.Add(part);
        UpdateProgress();
    }

    private static void Renumber(ScriptProject p)
    {
        for (var i = 0; i < p.Parts.Count; i++) p.Parts[i].Number = i + 1;
    }

    /// <summary>
    /// Sau khi mở lại app: phần đang "chờ/đang chạy" mà tác vụ đã xong (hoặc không còn) được cập nhật theo hàng đợi thật,
    /// để không bị treo ở trạng thái cũ.
    /// </summary>
    private void ReconcileWithQueue()
    {
        var tasks = _db.GetAllTasks().Where(t => t.ProjectId != null).ToDictionary(t => t.Id);
        foreach (var project in Projects)
        {
            foreach (var part in project.Parts.Where(x => x.Status is ScriptPartStatus.Waiting or ScriptPartStatus.Running))
            {
                if (part.TaskId != null && tasks.TryGetValue(part.TaskId, out var t))
                {
                    if (t.Status == RenderTaskStatus.Completed && !string.IsNullOrEmpty(t.LocalFilePath))
                    {
                        part.Status = ScriptPartStatus.Done;
                        part.VideoPath = t.LocalFilePath;
                        part.ActualSeconds = t.ActualDurationSeconds;
                    }
                    else if (t.Status is RenderTaskStatus.Failed or RenderTaskStatus.Cancelled)
                    {
                        part.Status = t.Status == RenderTaskStatus.Failed ? ScriptPartStatus.Failed : ScriptPartStatus.NotStarted;
                        part.Error = t.ErrorMessage;
                    }
                    else if (t.Status is RenderTaskStatus.Pending or RenderTaskStatus.Queued or RenderTaskStatus.Processing or RenderTaskStatus.Downloading)
                    {
                        continue; // còn trong hàng đợi: theo dõi tiếp (chạy nối tiếp phải bấm lại "Chạy")
                    }
                }
                else
                {
                    part.Status = ScriptPartStatus.NotStarted;
                }
                _db.UpsertProject(project);
            }
        }
    }

    // ------------------------------------------------------------------ kịch bản: thêm / sửa / xóa

    [RelayCommand]
    private void NewProject()
    {
        var dlg = new ScriptProjectEditorWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var project = dlg.Result;
        Renumber(project);
        _db.UpsertProject(project);
        Projects.Insert(0, project);
        SelectedProject = project;
        StatusText = $"Đã tạo kịch bản '{project.Title}' gồm {project.Parts.Count} phần. Bấm 'Chạy kịch bản' để làm video lần lượt.";
    }

    [RelayCommand]
    private void EditProject()
    {
        var p = SelectedProject;
        if (p == null) return;
        if (_chains.Contains(p.Id))
        {
            MessageBox.Show("Kịch bản đang chạy. Bấm 'Dừng' trước khi sửa.", "Sửa kịch bản", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new ScriptProjectEditorWindow(p) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        Renumber(p);
        p.UpdatedAt = DateTime.UtcNow;
        _db.UpsertProject(p);
        LoadParts();
        p.NotifyChanged();
        StatusText = $"Đã lưu kịch bản '{p.Title}'.";
    }

    [RelayCommand]
    private void DeleteProject()
    {
        var p = SelectedProject;
        if (p == null) return;
        if (MessageBox.Show($"Xóa kịch bản '{p.Title}'?\n(Các video đã tạo vẫn nằm trong thư mục lưu video.)", "Xóa kịch bản",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StopChain(p, quiet: true);
        _db.DeleteProject(p.Id);
        var index = Projects.IndexOf(p);
        Projects.Remove(p);
        SelectedProject = Projects.Count == 0 ? null : Projects[Math.Clamp(index, 0, Projects.Count - 1)];
        StatusText = "Đã xóa kịch bản.";
    }

    // ------------------------------------------------------------------ các phần: sắp xếp / sửa / xóa

    [RelayCommand]
    private void MoveUp(ScriptPart? part) => Move(part, -1);

    [RelayCommand]
    private void MoveDown(ScriptPart? part) => Move(part, +1);

    private void Move(ScriptPart? part, int delta)
    {
        var p = SelectedProject;
        if (p == null || part == null) return;
        var i = p.Parts.IndexOf(part);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= p.Parts.Count) return;
        p.Parts.RemoveAt(i);
        p.Parts.Insert(j, part);
        Renumber(p);
        _db.UpsertProject(p);
        LoadParts();
        StatusText = $"Đã dời phần lên vị trí {j + 1}. Thứ tự này cũng là thứ tự khi ghép video.";
    }

    [RelayCommand]
    private void EditPart(ScriptPart? part)
    {
        var p = SelectedProject;
        if (p == null || part == null) return;
        var dlg = new PartEditorWindow($"Sửa phần {part.Number}", part.Text) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        part.Text = dlg.PartText;
        _db.UpsertProject(p);
        part.NotifyChanged();
        StatusText = $"Đã sửa phần {part.Number}. Bấm 'Làm lại phần này' để tạo lại video của phần đó.";
    }

    [RelayCommand]
    private void AddPart()
    {
        var p = SelectedProject;
        if (p == null) return;
        var dlg = new PartEditorWindow("Thêm phần mới", string.Empty) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        p.Parts.Add(new ScriptPart { Text = dlg.PartText });
        Renumber(p);
        _db.UpsertProject(p);
        LoadParts();
    }

    [RelayCommand]
    private void DeletePart(ScriptPart? part)
    {
        var p = SelectedProject;
        if (p == null || part == null) return;
        if (MessageBox.Show($"Xóa phần {part.Number} khỏi kịch bản?", "Xóa phần", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (part.TaskId != null && part.Status is ScriptPartStatus.Waiting or ScriptPartStatus.Running) _dispatcher.CancelTask(part.TaskId);
        p.Parts.Remove(part);
        Renumber(p);
        _db.UpsertProject(p);
        LoadParts();
    }

    [RelayCommand]
    private void PreviewPart(ScriptPart? part)
    {
        var p = SelectedProject;
        if (p == null || part == null) return;
        var idx = p.Parts.IndexOf(part);
        var frame = idx > 0 && p.UseLastFrame ? (p.Parts[idx - 1].LastFramePath ?? "(khung hình cuối của phần trước — lấy khi phần trước xong)") : null;
        var composed = BuildPrompt(p, idx, frame);
        var images = composed.Images.Count == 0
            ? "(không có ảnh tham chiếu)"
            : string.Join(Environment.NewLine, composed.Images.Select((x, i) => $"Ảnh {i + 1}: {(File.Exists(x) ? Path.GetFileName(x) : x)}"));
        new TextPreviewWindow($"Prompt sẽ gửi cho phần {part.Number}/{p.Parts.Count}",
            composed.Text + Environment.NewLine + Environment.NewLine + "—— Ảnh tham chiếu (theo thứ tự gửi) ——" + Environment.NewLine + images)
        { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private void OpenVideo(ScriptPart? part)
    {
        if (part == null || !part.HasVideo) { StatusText = "Phần này chưa có video."; return; }
        try { Process.Start(new ProcessStartInfo(part.VideoPath!) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText = "Không mở được video: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenLastFrame(ScriptPart? part)
    {
        if (part == null || !part.HasLastFrame) { StatusText = "Phần này chưa có ảnh khung hình cuối (có sau khi phần kế tiếp được chuẩn bị)."; return; }
        try { Process.Start(new ProcessStartInfo(part.LastFramePath!) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText = "Không mở được ảnh: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = _db.GetSettings().DownloadDirectory;
        var target = SelectedProject?.MergedPath is { } m && File.Exists(m) ? m : null;
        try
        {
            if (target != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            else if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText = "Không mở được thư mục: " + ex.Message; }
    }

    // ------------------------------------------------------------------ chạy nối tiếp

    /// <summary>Chạy kịch bản: làm lần lượt từ phần chưa xong đầu tiên (phần trước xong mới tới phần sau).</summary>
    [RelayCommand]
    private async Task RunProjectAsync()
    {
        var p = SelectedProject;
        if (p == null) return;
        if (p.Parts.Count == 0) { StatusText = "Kịch bản chưa có phần nào."; return; }
        var first = p.Parts.FindIndex(x => x.Status != ScriptPartStatus.Done);
        if (first < 0)
        {
            StatusText = "Mọi phần đã xong. Bấm 'Ghép video', hoặc 'Chạy từ phần này' ở một phần để làm lại.";
            return;
        }
        await StartChainAsync(p, first);
    }

    /// <summary>Chạy từ phần này trở đi (các phần sau được làm lại theo phần này).</summary>
    [RelayCommand]
    private async Task RunFromPartAsync(ScriptPart? part)
    {
        var p = SelectedProject;
        if (p == null || part == null) return;
        var idx = p.Parts.IndexOf(part);
        if (idx < 0) return;
        if (idx + 1 < p.Parts.Count && p.Parts.Skip(idx + 1).Any(x => x.Status == ScriptPartStatus.Done) &&
            MessageBox.Show("Các phần phía sau đã có video sẽ được làm lại nối tiếp theo phần này (tốn thêm lượt). Tiếp tục?", "Chạy từ phần này",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await StartChainAsync(p, idx);
    }

    /// <summary>Chỉ làm lại đúng phần này (không chạy tiếp các phần sau).</summary>
    [RelayCommand]
    private async Task RedoPartAsync(ScriptPart? part)
    {
        var p = SelectedProject;
        if (p == null || part == null) return;
        var idx = p.Parts.IndexOf(part);
        if (idx < 0) return;
        _chains.Remove(p.Id);
        IsChainRunning = false;
        await EnqueuePartAsync(p, idx);
        StatusText = $"Phần {part.Number} đã vào hàng đợi (chỉ làm lại phần này).";
    }

    private async Task StartChainAsync(ScriptProject p, int fromIndex)
    {
        for (var i = fromIndex; i < p.Parts.Count; i++)
        {
            var part = p.Parts[i];
            if (part.Status is ScriptPartStatus.Waiting or ScriptPartStatus.Running) continue;
            part.Status = ScriptPartStatus.NotStarted;
            part.Error = null;
        }
        _chains.Add(p.Id);
        if (SelectedProject?.Id == p.Id) IsChainRunning = true;
        await EnqueuePartAsync(p, fromIndex);
        StatusText = $"Đang chạy kịch bản '{p.Title}': phần {fromIndex + 1}/{p.Parts.Count} đã vào hàng đợi, các phần sau tự chạy khi phần trước xong.";
    }

    private static string Fill(string template, int n, int total)
        => template.Replace("{n}", n.ToString()).Replace("{total}", total.ToString());

    private ComposedPrompt BuildPrompt(ScriptProject p, int idx, string? frame)
    {
        var total = p.Parts.Count;
        string? header = null, footer = null;
        if (total > 1)
        {
            if (idx > 0) header = Fill(frame != null ? p.ContinueHeader : ScriptProject.ContinueHeaderWithoutFrame, idx + 1, total);
            if (idx < total - 1) footer = Fill(p.ContinueFooter, idx + 1, total);
        }
        return PromptComposer.Compose(p.Parts[idx].Text, null, p.Characters, p.SceneText, p.SceneImages,
            leadingImages: frame != null ? new[] { frame } : null, header: header, footer: footer);
    }

    private async Task EnqueuePartAsync(ScriptProject p, int idx)
    {
        var part = p.Parts[idx];
        string? frame = null;
        if (idx > 0 && p.UseLastFrame) frame = await EnsureLastFrameAsync(p, p.Parts[idx - 1]);

        var composed = BuildPrompt(p, idx, frame);
        if (composed.Images.Count > PromptComposer.MaxReferenceImages)
        {
            FailPart(p, part, $"Phần {idx + 1} có {composed.Images.Count} ảnh tham chiếu (nhân vật + bối cảnh + khung hình cuối), Dola chỉ nhận tối đa {PromptComposer.MaxReferenceImages}. Bớt ảnh ở kịch bản.");
            return;
        }

        var task = new RenderTask
        {
            Prompt = composed.Text,
            PromptTitle = $"{p.Title} — phần {idx + 1}/{p.Parts.Count}",
            Model = p.Model,
            Ratio = p.Ratio,
            Duration = p.Duration,
            ReferenceLocalPaths = composed.Images,
            ProjectId = p.Id,
            ProjectPartId = part.Id,
        };
        part.TaskId = task.Id;
        part.Status = ScriptPartStatus.Waiting;
        part.Error = null;
        part.StageText = string.Empty;
        _db.UpsertProject(p);
        part.NotifyChanged();
        UpdateProgress();
        await _queue.EnqueueAsync(new[] { task });
    }

    /// <summary>Khung hình cuối của video phần trước (PNG). Null nếu chưa có video / chưa có ffmpeg / lấy lỗi.</summary>
    private async Task<string?> EnsureLastFrameAsync(ScriptProject p, ScriptPart prev)
    {
        if (!prev.HasVideo) return null;
        if (prev.HasLastFrame) return prev.LastFramePath;
        if (!_tools.IsAvailable)
        {
            StatusText = "Không lấy được khung hình cuối vì chưa có ffmpeg — phần sau vẫn chạy nhưng không nối liền cảnh. " + FfmpegWarning;
            return null;
        }
        var path = Path.Combine(AppPaths.DataDir, "script_frames", p.Id, $"{prev.Id}.png");
        var (ok, error) = await _tools.ExtractLastFrameAsync(prev.VideoPath!, path);
        if (!ok)
        {
            StatusText = $"Không lấy được khung hình cuối của phần {prev.Number}: {error}. Phần sau chạy không có ảnh nối.";
            return null;
        }
        prev.LastFramePath = path;
        _db.UpsertProject(p);
        prev.NotifyChanged();
        return path;
    }

    private void FailPart(ScriptProject p, ScriptPart part, string error)
    {
        part.Status = ScriptPartStatus.Failed;
        part.Error = error;
        _chains.Remove(p.Id);
        if (SelectedProject?.Id == p.Id) IsChainRunning = false;
        _db.UpsertProject(p);
        part.NotifyChanged();
        UpdateProgress();
        StatusText = error;
    }

    [RelayCommand]
    private void StopRun()
    {
        if (SelectedProject != null) StopChain(SelectedProject, quiet: false);
    }

    private void StopChain(ScriptProject p, bool quiet)
    {
        _chains.Remove(p.Id);
        if (SelectedProject?.Id == p.Id) IsChainRunning = false;
        foreach (var part in p.Parts.Where(x => x.Status is ScriptPartStatus.Waiting or ScriptPartStatus.Running))
            if (part.TaskId != null) _dispatcher.CancelTask(part.TaskId);
        if (!quiet) StatusText = "Đã dừng kịch bản: các phần đang chờ / đang chạy bị hủy, phần đã xong được giữ lại.";
    }

    // ------------------------------------------------------------------ theo dõi hàng đợi

    private void OnTaskUpdated(RenderTask task)
    {
        if (string.IsNullOrEmpty(task.ProjectId)) return;
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try { await HandleTaskUpdateAsync(task); }
            catch (Exception ex) { StatusText = "Lỗi khi cập nhật kịch bản: " + ex.Message; }
        });
    }

    private async Task HandleTaskUpdateAsync(RenderTask task)
    {
        var project = Projects.FirstOrDefault(p => p.Id == task.ProjectId);
        var part = project?.Parts.FirstOrDefault(x => x.Id == task.ProjectPartId && x.TaskId == task.Id);
        if (project == null || part == null) return; // tác vụ cũ của một lần chạy trước đó

        switch (task.Status)
        {
            case RenderTaskStatus.Pending:
            case RenderTaskStatus.Queued:
                part.Status = ScriptPartStatus.Waiting;
                break;

            case RenderTaskStatus.Processing:
            case RenderTaskStatus.Downloading:
                part.Status = ScriptPartStatus.Running;
                part.StageText = task.StageText ?? string.Empty;
                break;

            case RenderTaskStatus.Completed:
                if (part.Status == ScriptPartStatus.Done) break;
                part.Status = ScriptPartStatus.Done;
                part.VideoPath = task.LocalFilePath;
                part.ActualSeconds = task.ActualDurationSeconds;
                part.Note = task.DolaNote;
                part.Error = null;
                part.LastFramePath = null; // video mới → khung hình cuối cũ không còn đúng
                _db.UpsertProject(project);
                part.NotifyChanged();
                UpdateProgress();
                await AfterPartDoneAsync(project, part);
                return;

            case RenderTaskStatus.Failed:
            case RenderTaskStatus.Cancelled:
                if (part.Status is ScriptPartStatus.Failed or ScriptPartStatus.NotStarted) break;
                part.Status = task.Status == RenderTaskStatus.Failed ? ScriptPartStatus.Failed : ScriptPartStatus.NotStarted;
                part.Error = task.ErrorMessage;
                if (_chains.Remove(project.Id) && SelectedProject?.Id == project.Id) IsChainRunning = false;
                if (task.Status == RenderTaskStatus.Failed)
                    StatusText = $"Phần {part.Number} bị lỗi nên kịch bản dừng: {task.ErrorMessage}. Sửa rồi bấm 'Làm lại phần này' hoặc 'Chạy từ phần này'.";
                _db.UpsertProject(project);
                break;
        }

        part.NotifyChanged();
        UpdateProgress();
    }

    private async Task AfterPartDoneAsync(ScriptProject project, ScriptPart part)
    {
        if (!_chains.Contains(project.Id)) return;
        var idx = project.Parts.IndexOf(part);
        if (idx < 0) return;

        if (idx + 1 < project.Parts.Count)
        {
            StatusText = $"Phần {idx + 1} xong, đang chuẩn bị phần {idx + 2}/{project.Parts.Count}...";
            await EnqueuePartAsync(project, idx + 1);
            StatusText = $"Phần {idx + 1} xong. Phần {idx + 2}/{project.Parts.Count} đã vào hàng đợi.";
            return;
        }

        _chains.Remove(project.Id);
        if (SelectedProject?.Id == project.Id) IsChainRunning = false;
        StatusText = $"Kịch bản '{project.Title}' đã xong {project.Parts.Count} phần.";
        if (project.AutoMerge) await MergeProjectAsync(project);
        else StatusText += " Bấm 'Ghép video' để ghép thành một video.";
    }

    private void UpdateProgress()
    {
        var p = SelectedProject;
        if (p == null || p.Parts.Count == 0) { ProgressText = string.Empty; return; }
        var done = p.Parts.Count(x => x.Status == ScriptPartStatus.Done);
        var running = p.Parts.Count(x => x.Status is ScriptPartStatus.Waiting or ScriptPartStatus.Running);
        var failed = p.Parts.Count(x => x.Status == ScriptPartStatus.Failed);
        ProgressText = $"{done}/{p.Parts.Count} phần xong" + (running > 0 ? $" · {running} đang chạy/chờ" : string.Empty) + (failed > 0 ? $" · {failed} lỗi" : string.Empty);
        p.NotifyChanged();
    }

    // ------------------------------------------------------------------ ghép video

    [RelayCommand]
    private async Task MergeAsync()
    {
        var p = SelectedProject;
        if (p == null) return;
        var ready = p.Parts.Count(x => x.HasVideo);
        if (ready == 0) { StatusText = "Chưa có video nào để ghép."; return; }
        if (ready < p.Parts.Count &&
            MessageBox.Show($"Mới có video của {ready}/{p.Parts.Count} phần. Vẫn ghép các phần đã có (theo đúng thứ tự)?", "Ghép video",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await MergeProjectAsync(p);
    }

    private async Task MergeProjectAsync(ScriptProject p)
    {
        if (IsMerging) return;
        var videos = p.Parts.Where(x => x.HasVideo).Select(x => x.VideoPath!).ToList();
        if (videos.Count == 0) return;
        if (!_tools.IsAvailable) { StatusText = FfmpegWarning; return; }

        IsMerging = true;
        StatusText = $"Đang ghép {videos.Count} video...";
        try
        {
            var dir = _db.GetSettings().DownloadDirectory;
            if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var name = string.Concat(p.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
            if (name.Length == 0) name = "kich_ban";
            var output = Path.Combine(dir, $"{name}_ghep_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            var (ok, error) = await _tools.MergeAsync(videos, output);
            if (!ok) { StatusText = "Ghép video lỗi: " + error; return; }

            p.MergedPath = output;
            _db.UpsertProject(p);
            StatusText = $"Đã ghép {videos.Count} video thành: {output}";
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true }); }
            catch { /* không mở được Explorer: không sao */ }
        }
        finally
        {
            IsMerging = false;
        }
    }
}
