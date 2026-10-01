using System;
using System.IO;

namespace DolaCoordinator.Helpers;

public static class AppPaths
{
    /// <summary>Tên thư mục dữ liệu của bản đã cài bằng bộ cài: %APPDATA%\AIOMMO DolaAI.</summary>
    public const string InstalledFolderName = "AIOMMO DolaAI";

    /// <summary>
    /// Bản đã cài bằng bộ cài (có tệp installed.marker cạnh file exe, do bộ cài tạo). Khi đó chương trình nằm ở thư mục ứng dụng
    /// (Program Files, chỉ đọc) và MỌI dữ liệu — cơ sở dữ liệu, cài đặt, log, hồ sơ trình duyệt của từng tài khoản, proxy — nằm ở
    /// %APPDATA%\AIOMMO DolaAI. Bản chạy thẳng từ thư mục (clone / giải nén) không có tệp này nên dữ liệu vẫn nằm cạnh như cũ.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            return !string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, "installed.marker"));
        }
    }

    /// <summary>Thư mục dữ liệu cố định của bản đã cài.</summary>
    public static string InstalledDataDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), InstalledFolderName);

    /// <summary>
    /// Thư mục dữ liệu của app (coordinator.db, gateway.log...). Bản đã cài: %APPDATA%\AIOMMO DolaAI. Bản chạy thẳng:
    /// %LocalAppData%\DolaCoordinator. Đặt biến môi trường DOLA_DATA_DIR để đổi (dùng khi thử nghiệm hoặc bản portable).
    /// </summary>
    public static string DataDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("DOLA_DATA_DIR");
            var dir = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : IsInstalled
                    ? InstalledDataDir
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DolaCoordinator");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Thư mục chứa dữ liệu của gateway (accounts/, tasks.db, downloads/...) trong bản đã cài.</summary>
    public static string InstalledGatewayDataDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "gateway");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
