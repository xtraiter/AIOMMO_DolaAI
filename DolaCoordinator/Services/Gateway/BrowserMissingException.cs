using System;

namespace DolaCoordinator.Services.Gateway;

/// <summary>Trình duyệt Chromium của gateway chưa có và không tự cài được (thường do mạng). Giao diện có thể mời cài lại.</summary>
public sealed class BrowserMissingException : InvalidOperationException
{
    public BrowserMissingException(string message) : base(message) { }
}
