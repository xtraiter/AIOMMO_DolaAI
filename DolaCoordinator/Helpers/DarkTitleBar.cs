using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DolaCoordinator.Helpers;

/// <summary>Bật thanh tiêu đề tối (Windows 10 1809+ / 11) để cửa sổ đồng bộ với giao diện teal.</summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var on = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        };
    }
}
