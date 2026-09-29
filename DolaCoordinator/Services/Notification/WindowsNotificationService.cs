using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DolaCoordinator.Services.Notification;

public class WindowsNotificationService : INotificationService
{
    public void ShowToast(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to show toast notification: {ex.Message}");
        }
    }
}
