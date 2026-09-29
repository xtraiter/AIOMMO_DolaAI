using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Sessions;

namespace DolaCoordinator.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDolaGatewayClient _gatewayClient;
    private readonly IQuotaTracker _quotaTracker;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    private QueueViewModel _queueVm;

    [ObservableProperty]
    private ProfilesViewModel _profilesVm;

    [ObservableProperty]
    private SettingsViewModel _settingsVm;

    // 0 = Vận hành, 1 = Dola Super (tài khoản/profile), 2 = Cài đặt
    [ObservableProperty]
    private int _selectedTabIndex = 0;

    [ObservableProperty]
    private string _pageTitle = PageTitles[0].Title;

    [ObservableProperty]
    private string _pageSubtitle = PageTitles[0].Subtitle;

    private static readonly (string Title, string Subtitle)[] PageTitles =
    {
        ("Vận hành", "Hàng đợi render video và tiến độ tải về"),
        ("Dola Super", "Mỗi profile là một tài khoản Dola của gateway: đăng nhập (thủ công/Google/Facebook), lưu phiên, theo dõi hạn ngạch"),
        ("Cài đặt", "Gateway, thư mục lưu và cập nhật"),
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
        IDolaGatewayClient gatewayClient,
        IQuotaTracker quotaTracker)
    {
        _queueVm = queueVm;
        _profilesVm = profilesVm;
        _settingsVm = settingsVm;
        _gatewayClient = gatewayClient;
        _quotaTracker = quotaTracker;

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
            }
        }
        catch (OperationCanceledException) { }
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
