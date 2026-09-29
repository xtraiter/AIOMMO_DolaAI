using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using DolaCoordinator.Helpers;
using DolaCoordinator.Services.Profiles;
using DolaCoordinator.Services.Downloader;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Notification;
using DolaCoordinator.Services.Queue;
using DolaCoordinator.Services.Security;
using DolaCoordinator.Services.Sessions;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DolaCoordinator;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global Exception Handlers to prevent unexpected crashes
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            System.Diagnostics.Debug.WriteLine($"AppDomain Unhandled Exception: {ex?.Message}");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"Đã xảy ra lỗi không mong muốn: {args.Exception.Message}", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // --no-gateway: mở app mà không bật gateway (dùng khi gateway đã chạy sẵn hoặc khi thử giao diện)
        var skipGateway = e.Args.Contains("--no-gateway", StringComparer.OrdinalIgnoreCase);
        if (!skipGateway && _serviceProvider.GetRequiredService<IDatabaseService>().GetSettings().AutoStartGateway)
            _ = StartBackgroundGatewayAsync();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    /// <summary>
    /// Bật ngầm dola-render-gateway ngay khi mở app (tùy chọn ở Cài đặt). Dù tùy chọn này tắt, gateway vẫn tự bật ngầm
    /// khi bạn dùng chức năng cần đến nó (điều phối, kiểm tra phiên, kiểm tra kết nối) — xem GatewayHost.
    /// </summary>
    private async Task StartBackgroundGatewayAsync()
    {
        try
        {
            await _serviceProvider!.GetRequiredService<IGatewayHost>().EnsureRunningAsync();
        }
        catch (OperationCanceledException) { }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddDolaModule();
        services.AddSingleton<MainWindow>(); // vỏ ứng dụng độc lập; khi gộp vào app khác thì bỏ đi
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider != null)
        {
            _serviceProvider.GetService<ITaskDispatcher>()?.StopAsync().GetAwaiter().GetResult();
            _serviceProvider.GetService<IGatewayHost>()?.Stop(); // tắt gateway do app bật (cả Chromium con)
            _serviceProvider.Dispose();
        }

        base.OnExit(e);
    }
}
