using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Sessions;

namespace DolaCoordinator.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDolaGatewayClient _gatewayClient;
    private readonly IQuotaTracker _quotaTracker;
    private readonly IGatewayHost _gatewayHost;
    private readonly IDatabaseService _db;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    private QueueViewModel _queueVm;

    [ObservableProperty]
    private ProfilesViewModel _profilesVm;

    [ObservableProperty]
    private SettingsViewModel _settingsVm;

    [ObservableProperty]
    private PromptsViewModel _promptsVm;

    [ObservableProperty]
    private ScriptsViewModel _scriptsVm;

    // 0 = Vận hành, 1 = Quản lý tài khoản (profile), 2 = Cài đặt, 3 = Quản lý prompt, 4 = Kịch bản lớn
    [ObservableProperty]
    private int _selectedTabIndex = 0;

    [ObservableProperty]
    private string _pageTitle = PageTitles[0].Title;

    [ObservableProperty]
    private string _pageSubtitle = PageTitles[0].Subtitle;

    private static readonly (string Title, string Subtitle)[] PageTitles =
    {
        ("Vận hành", "Điều khiển tiến trình: chạy, tạm dừng, ưu tiên, quản lý hàng đợi và theo dõi từng video"),
        ("Quản lý tài khoản", "Mỗi dòng là một tài khoản Dola: tích chọn rồi dùng các nút ở hàng trên (mở, đăng nhập tự động, sửa, kiểm tra, xóa...)"),
        ("Cài đặt", "Gateway, trình duyệt, thư mục lưu và cập nhật"),
        ("Quản lý prompt", "Thư viện prompt: soạn, nhập/xuất rồi thêm vào hàng đợi để làm video"),
        ("Kịch bản lớn", "Kịch bản dài tách thành nhiều phần, chạy nối tiếp bằng khung hình cuối của video trước, rồi ghép thành một video"),
    };

    partial void OnSelectedTabIndexChanged(int value)
    {
        var page = PageTitles[Math.Clamp(value, 0, PageTitles.Length - 1)];
        PageTitle = page.Title;
        PageSubtitle = page.Subtitle;
    }

    [ObservableProperty]
    private string _gatewayStatusBadge = "Đang kiểm tra...";

    [ObservableProperty]
    private bool _isGatewayOnline = false;

    [ObservableProperty]
    private string _nextResetTimeString = string.Empty;

    public MainViewModel(
        QueueViewModel queueVm,
        ProfilesViewModel profilesVm,
        SettingsViewModel settingsVm,
        PromptsViewModel promptsVm,
        ScriptsViewModel scriptsVm,
        IDolaGatewayClient gatewayClient,
        IQuotaTracker quotaTracker,
        IGatewayHost gatewayHost,
        IDatabaseService db)
    {
        _queueVm = queueVm;
        _profilesVm = profilesVm;
        _settingsVm = settingsVm;
        _promptsVm = promptsVm;
        _scriptsVm = scriptsVm;
        WeakReferenceMessenger.Default.Register<MainViewModel, NavigateMessage>(this, static (vm, m) => vm.SelectedTabIndex = m.TabIndex);
        _gatewayClient = gatewayClient;
        _quotaTracker = quotaTracker;
        _gatewayHost = gatewayHost;
        _db = db;
        WeakReferenceMessenger.Default.Register<MainViewModel, BrowserStateChangedMessage>(this, static (vm, _) => vm.RefreshEnvironment());
        RefreshEnvironment();

        NextResetTimeString = _quotaTracker.GetNextResetTime().ToString("dd/MM/yyyy 00:00");
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        _ = RefreshLoopAsync(_cts.Token);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        await CheckGatewayStatusAsync();
        try
        {
            while (!ct.IsCancellationRequested && await _timer.WaitForNextTickAsync(ct))
            {
                await CheckGatewayStatusAsync();
                if (!IsInstallingEnvironment) RefreshEnvironment();
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ thẻ "cần cài môi trường" ở thanh bên

    [ObservableProperty]
    private bool _showSetupCard;

    [ObservableProperty]
    private string _setupTitle = string.Empty;

    [ObservableProperty]
    private string _setupDetail = string.Empty;

    [ObservableProperty]
    private string _setupButtonText = string.Empty;

    [ObservableProperty]
    private string _setupProgressText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSetupCommand))]
    private bool _isInstallingEnvironment;

    private bool _gatewayMissing;

    /// <summary>Kiểm tra môi trường chạy: có gateway không, đã cài trình duyệt Chromium chưa. Thiếu thì hiện thẻ cảnh báo.</summary>
    public void RefreshEnvironment()
    {
        var gatewayDir = GatewayLocator.FindDir(_db.GetSettings().GatewayDir);
        _gatewayMissing = gatewayDir == null;

        if (_gatewayMissing)
        {
            SetupTitle = "Chưa tìm thấy gateway";
            SetupDetail = "Cần thư mục 'gateway' (dola-gateway.exe) cạnh app hoặc chọn thư mục trong Cài đặt.";
            SetupButtonText = "Mở Cài đặt";
            ShowSetupCard = true;
        }
        else if (!_gatewayHost.IsBrowserInstalled)
        {
            SetupTitle = "Chưa cài môi trường chạy";
            SetupDetail = "Cần tải trình duyệt Chromium (~150 MB) để đăng nhập tài khoản và render video.";
            SetupButtonText = "⬇ Cài đặt ngay";
            ShowSetupCard = true;
        }
        else
        {
            ShowSetupCard = IsInstallingEnvironment; // đang cài dở thì giữ thẻ đến khi xong
        }
    }

    private bool CanRunSetup() => !IsInstallingEnvironment;

    [RelayCommand(CanExecute = nameof(CanRunSetup))]
    private async Task RunSetupAsync()
    {
        if (_gatewayMissing)
        {
            SelectedTabIndex = 2; // Cài đặt: chọn thư mục gateway
            return;
        }

        IsInstallingEnvironment = true;
        SetupProgressText = "Đang bắt đầu...";
        try
        {
            var result = await _gatewayHost.InstallBrowserAsync(new Progress<string>(line => SetupProgressText = line));
            SetupProgressText = result.Ok ? "Đã cài xong." : (result.Error ?? "Cài đặt thất bại.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsInstallingEnvironment = false;
            WeakReferenceMessenger.Default.Send(new BrowserStateChangedMessage());
        }
    }

    public async Task CheckGatewayStatusAsync()
    {
        try
        {
            var health = await _gatewayClient.CheckHealthAsync();
            if (health != null && health.Ok)
            {
                IsGatewayOnline = true;
                GatewayStatusBadge = "Gateway: Trực Tuyến";
            }
            else
            {
                IsGatewayOnline = false;
                GatewayStatusBadge = "Gateway: Ngoại Tuyến (127.0.0.1:8000)";
            }
        }
        catch
        {
            IsGatewayOnline = false;
            GatewayStatusBadge = "Gateway: Không phản hồi";
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }
}
