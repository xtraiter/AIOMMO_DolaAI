using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Helpers;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Sessions;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Services.Update;
using Microsoft.Win32;

namespace DolaCoordinator.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDatabaseService _databaseService;
    private readonly IDolaGatewayClient _gatewayClient;
    private readonly IGatewayHost _gatewayHost;
    private readonly IUpdateService _updateService;
    private readonly IQuotaTracker _quotaTracker;

    [ObservableProperty]
    private string _gatewayUrl = "http://127.0.0.1:8000";

    [ObservableProperty]
    private string? _clientApiKey;

    [ObservableProperty]
    private int _defaultDailyQuota = 5;


    [ObservableProperty]
    private int _pollingIntervalSeconds = 5;

    [ObservableProperty]
    private bool _enableToastNotification = true;

    [ObservableProperty]
    private bool _autoRetryOnFailure = true;

    [ObservableProperty]
    private bool _autoStartGateway = true;

    [ObservableProperty]
    private string _gatewayDir = string.Empty;

    [ObservableProperty]
    private string _pythonCommand = "py -3";

    [ObservableProperty]
    private string? _adminKey;

    [ObservableProperty]
    private bool _autoAnswerAskBack = true;

    [ObservableProperty]
    private string _askBackReply = AppSettings.DefaultAskBackReply;

    [RelayCommand]
    private void ResetAskBackReply() => AskBackReply = AppSettings.DefaultAskBackReply;

    [ObservableProperty]
    private string _gatewayDirDetectedText = string.Empty;

    [ObservableProperty]
    private string _connectionStatusText = "Chưa kiểm tra";

    [ObservableProperty]
    private bool _isConnectionOk = false;

    // Auto-update properties
    [ObservableProperty]
    private string _currentVersion = "1.0.0";

    [ObservableProperty]
    private string _updateCheckUrl = string.Empty;

    [ObservableProperty]
    private string _updateStatusText = "Chưa kiểm tra phiên bản mới.";

    [ObservableProperty]
    private UpdateInfo? _availableUpdate;

    [ObservableProperty]
    private bool _hasAvailableUpdate = false;

    [ObservableProperty]
    private bool _isCheckingUpdate = false;

    [ObservableProperty]
    private bool _isDownloadingUpdate = false;

    [ObservableProperty]
    private int _updateDownloadProgress = 0;

    public SettingsViewModel(
        IDatabaseService databaseService,
        IDolaGatewayClient gatewayClient,
        IUpdateService updateService,
        IQuotaTracker quotaTracker,
        IGatewayHost gatewayHost)
    {
        _databaseService = databaseService;
        _gatewayClient = gatewayClient;
        _gatewayHost = gatewayHost;
        _updateService = updateService;
        _quotaTracker = quotaTracker;

        CurrentVersion = $"v{_updateService.CurrentVersionString}";
        WeakReferenceMessenger.Default.Register<SettingsViewModel, BrowserStateChangedMessage>(this, static (vm, _) => vm.RefreshBrowserStatus());
        LoadSettings();
    }

    public void LoadSettings()
    {
        var s = _databaseService.GetSettings();
        GatewayUrl = s.GatewayUrl;
        ClientApiKey = s.ClientApiKey;
        DefaultDailyQuota = s.DefaultDailyQuota;
        PollingIntervalSeconds = s.PollingIntervalSeconds;
        EnableToastNotification = s.EnableToastNotification;
        AutoRetryOnFailure = s.AutoRetryOnFailure;
        AutoStartGateway = s.AutoStartGateway;
        GatewayDir = s.GatewayDir ?? string.Empty;
        PythonCommand = string.IsNullOrWhiteSpace(s.PythonCommand) ? "py -3" : s.PythonCommand;
        AdminKey = s.AdminKey;
        AutoAnswerAskBack = s.AutoAnswerAskBack;
        AskBackReply = string.IsNullOrWhiteSpace(s.AskBackReply) ? AppSettings.DefaultAskBackReply : s.AskBackReply;
        UpdateCheckUrl = s.UpdateCheckUrl;
        RefreshGatewayDirDetected();
        RefreshBrowserStatus();
    }

    [ObservableProperty]
    private string _browserStatusText = string.Empty;

    [ObservableProperty]
    private string _browserProgressText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallBrowserCommand))]
    private bool _isInstallingBrowser;

    private void RefreshBrowserStatus()
        => BrowserStatusText = _gatewayHost.IsBrowserInstalled
            ? "✔ Đã cài trình duyệt Chromium cho gateway."
            : "✖ Chưa có trình duyệt Chromium. Bấm \"Cài đặt trình duyệt\" (cần mạng, ~150 MB).";

    private bool CanInstallBrowser() => !IsInstallingBrowser;

    [RelayCommand(CanExecute = nameof(CanInstallBrowser))]
    private async Task InstallBrowserAsync()
    {
        IsInstallingBrowser = true;
        BrowserProgressText = "Đang bắt đầu...";
        try
        {
            var result = await _gatewayHost.InstallBrowserAsync(new Progress<string>(line => BrowserProgressText = line));
            if (!result.Ok) BrowserProgressText = result.Error ?? "Cài đặt thất bại.";
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsInstallingBrowser = false;
            RefreshBrowserStatus();
            WeakReferenceMessenger.Default.Send(new BrowserStateChangedMessage());
        }
    }

    private void RefreshGatewayDirDetected()
    {
        var found = GatewayLocator.FindDir(string.IsNullOrWhiteSpace(GatewayDir) ? null : GatewayDir.Trim());
        GatewayDirDetectedText = found == null
            ? "Không tìm thấy gateway (cần dola-gateway.exe hoặc server.py). Hãy chọn thư mục thủ công."
            : $"Đang dùng: {found}  (profile nằm ở {GatewayLocator.AccountsDir(found)})";
    }

    partial void OnGatewayDirChanged(string value) => RefreshGatewayDirDetected();

    [RelayCommand]
    private void BrowseGatewayDir()
    {
        var dialog = new OpenFolderDialog { Title = "Chọn thư mục gateway (chứa dola-gateway.exe hoặc server.py)" };
        if (Directory.Exists(GatewayDir)) dialog.InitialDirectory = GatewayDir;
        if (dialog.ShowDialog() == true) GatewayDir = dialog.FolderName;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        ConnectionStatusText = "Đang kiểm tra kết nối tới gateway...";
        try
        {
            var host = await _gatewayHost.EnsureRunningAsync(); // chưa chạy thì bật ngầm rồi mới kiểm tra
            var health = await _gatewayClient.CheckHealthAsync(GatewayUrl);
            if (health != null && health.Ok)
            {
                IsConnectionOk = true;
                ConnectionStatusText = $"Kết nối Gateway thành công! (Accounts sẵn sàng: {health.Available}, Tasks chờ: {health.PendingTasks})";
            }
            else
            {
                IsConnectionOk = false;
                ConnectionStatusText = host.Ok
                    ? "Không nhận được phản hồi hợp lệ từ Dola Render Gateway."
                    : $"Không bật được gateway: {host.Error}";
            }
        }
        catch (Exception ex)
        {
            IsConnectionOk = false;
            ConnectionStatusText = $"Lỗi kết nối: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateStatusText = "Đang kiểm tra bản cập nhật từ server...";

        try
        {
            var update = await _updateService.CheckForUpdatesAsync(UpdateCheckUrl, CancellationToken.None);
            if (update != null)
            {
                AvailableUpdate = update;
                HasAvailableUpdate = true;
                UpdateStatusText = $"🎉 Đã có bản cập nhật mới v{update.Version}! (Ngày: {update.ReleaseDate})";
            }
            else
            {
                AvailableUpdate = null;
                HasAvailableUpdate = false;
                UpdateStatusText = "✅ Bạn đang sử dụng phiên bản mới nhất.";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Lỗi kiểm tra cập nhật: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndApplyUpdateAsync()
    {
        if (AvailableUpdate == null || IsDownloadingUpdate) return;

        var confirm = MessageBox.Show(
            $"Bản cập nhật v{AvailableUpdate.Version} đã sẵn sàng.\n\n" +
            $"Nội dung mới:\n{AvailableUpdate.Changelog}\n\n" +
            "Ứng dụng sẽ tự động tải file cập nhật, giải nén và khởi động lại. Bạn có muốn tiếp tục?",
            "Xác nhận Cập nhật",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        IsDownloadingUpdate = true;
        UpdateDownloadProgress = 0;
        UpdateStatusText = "Đang tải bản cập nhật...";

        var progress = new Progress<int>(pct => UpdateDownloadProgress = pct);

        try
        {
            var packagePath = await _updateService.DownloadUpdateAsync(AvailableUpdate, progress, CancellationToken.None);
            UpdateStatusText = "Đã tải xong! Đang khởi động trình cài đặt cập nhật...";
            await Task.Delay(1000);
            _updateService.ApplyUpdateAndRestart(packagePath);
        }
        catch (Exception ex)
        {
            IsDownloadingUpdate = false;
            UpdateStatusText = $"Lỗi khi tải hoặc áp dụng bản cập nhật: {ex.Message}";
            MessageBox.Show(UpdateStatusText, "Lỗi cập nhật", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            var s = _databaseService.GetSettings(); // giữ nguyên các trường không có trên form (vd. AdminKey)
            s.GatewayUrl = GatewayUrl?.Trim().TrimEnd('/') ?? "http://127.0.0.1:8000";
            s.ClientApiKey = ClientApiKey?.Trim();
            s.DefaultDailyQuota = Math.Clamp(DefaultDailyQuota, 1, 1000);
            s.PollingIntervalSeconds = Math.Clamp(PollingIntervalSeconds, 2, 60);
            s.EnableToastNotification = EnableToastNotification;
            s.AutoRetryOnFailure = AutoRetryOnFailure;
            s.AutoStartGateway = AutoStartGateway;
            s.GatewayDir = string.IsNullOrWhiteSpace(GatewayDir) ? null : GatewayDir.Trim();
            s.PythonCommand = string.IsNullOrWhiteSpace(PythonCommand) ? "py -3" : PythonCommand.Trim();
            s.AdminKey = string.IsNullOrWhiteSpace(AdminKey) ? null : AdminKey.Trim();
            s.AutoAnswerAskBack = AutoAnswerAskBack;
            s.AskBackReply = string.IsNullOrWhiteSpace(AskBackReply) ? AppSettings.DefaultAskBackReply : AskBackReply.Trim();
            s.UpdateCheckUrl = UpdateCheckUrl?.Trim() ?? string.Empty;

            _databaseService.SaveSettings(s);

            // Dola có thể đổi chính sách: giới hạn mới áp dụng ngay cho mọi tài khoản đang có, không chỉ tài khoản tạo sau
            _quotaTracker.ApplyDailyLimit(s.DefaultDailyQuota);
            DefaultDailyQuota = s.DefaultDailyQuota;
            WeakReferenceMessenger.Default.Send(new SessionsChangedMessage());
            MessageBox.Show("Đã lưu cấu hình hệ thống thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi lưu cấu hình: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
