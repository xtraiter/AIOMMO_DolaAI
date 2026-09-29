using System;
using System.IO;

namespace DolaCoordinator.Helpers;

public static class AppPaths
{
    /// <summary>
    /// Thư mục dữ liệu của app (coordinator.db). Mặc định %LocalAppData%\DolaCoordinator;
    /// đặt biến môi trường DOLA_DATA_DIR để đổi (dùng khi thử nghiệm hoặc chạy bản portable).
    /// </summary>
    public static string DataDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("DOLA_DATA_DIR");
            var dir = string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DolaCoordinator")
                : overrideDir;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
