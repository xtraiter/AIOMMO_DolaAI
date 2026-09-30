using DolaCoordinator.Services.Downloader;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Notification;
using DolaCoordinator.Services.Profiles;
using DolaCoordinator.Services.Proxy;
using DolaCoordinator.Services.Queue;
using DolaCoordinator.Services.Security;
using DolaCoordinator.Services.Sessions;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Services.Update;
using DolaCoordinator.Services.Video;
using DolaCoordinator.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DolaCoordinator;

/// <summary>
/// Điểm đăng ký duy nhất của module Dola. Ứng dụng độc lập gọi nó trong App.xaml.cs; khi gộp vào ứng dụng khác
/// chỉ cần gọi <c>services.AddDolaModule()</c> rồi lấy QueueView / ProfilesView / SettingsView từ DI hoặc tự tạo.
/// </summary>
public static class DolaModule
{
    public static IServiceCollection AddDolaModule(this IServiceCollection services)
    {
        // Lưu trữ & bảo mật
        services.AddSingleton<ISecurityService, DpapiSecurityService>();
        services.AddSingleton<IDatabaseService, LiteDbDatabaseService>();

        // Gateway
        services.AddHttpClient();
        services.AddSingleton<IGatewayHost, GatewayHost>();
        services.AddSingleton<IDolaGatewayClient, DolaGatewayClient>();

        // Tài khoản Dola (mỗi tài khoản = một profile của gateway)
        services.AddSingleton<IAccountProfileService, AccountProfileService>();
        services.AddSingleton<IProxyService, ProxyService>();

        // Phiên & hạn ngạch
        services.AddSingleton<IQuotaTracker, QuotaTracker>();
        services.AddSingleton<ISessionValidator, SessionValidator>();

        // Hàng đợi, tải video, thông báo, cập nhật
        services.AddSingleton<IAssetDownloader, AssetDownloader>();
        services.AddSingleton<IVideoTools, FfmpegVideoTools>();
        services.AddSingleton<ITaskDispatcher, TaskDispatcher>();
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<IUpdateService, AutoUpdateService>();

        // ViewModels
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PromptsViewModel>();
        services.AddSingleton<ScriptsViewModel>();
        services.AddSingleton<ProxiesViewModel>();
        services.AddSingleton<MainViewModel>();

        return services;
    }
}
