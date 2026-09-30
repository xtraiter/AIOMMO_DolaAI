using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Proxy;

public sealed record ProxyTestResult(bool Ok, string? Ip, string? Country, string? City, int LatencyMs, string? Error);

/// <summary>
/// Quản lý proxy và việc gán proxy cho tài khoản. Tài khoản có proxy thì app ghi <c>accounts/&lt;tên&gt;/proxy.txt</c>
/// (một dòng <c>scheme://user:pass@host:port</c>); gateway đọc file này mỗi lần mở Chromium cho tài khoản đó.
/// </summary>
public interface IProxyService
{
    List<ProxyItem> GetAll();
    ProxyItem? Get(string? id);

    /// <summary>Lưu proxy. <paramref name="plainPassword"/> = null giữ mật khẩu cũ, "" xóa mật khẩu, chuỗi khác thì mã hóa và lưu.</summary>
    void Save(ProxyItem proxy, string? plainPassword);

    /// <summary>URL đầy đủ để đưa cho trình duyệt: scheme://user:pass@host:port.</summary>
    string BuildUrl(ProxyItem proxy);

    /// <summary>Xóa proxy và gỡ nó khỏi mọi tài khoản đang dùng; trả về số tài khoản bị gỡ.</summary>
    int Delete(string proxyId);

    /// <summary>Gán (hoặc gỡ khi proxyId = null) proxy cho một tài khoản: lưu vào tài khoản và ghi / xóa proxy.txt.</summary>
    void Assign(AccountProfile account, string? proxyId);

    /// <summary>Ghi lại / xóa proxy.txt của các tài khoản cho khớp với proxy đang gán (sau khi sửa proxy hoặc đổi thư mục gateway).</summary>
    void SyncFiles(IEnumerable<AccountProfile> accounts);

    /// <summary>Kiểm tra proxy: đi qua nó tới dịch vụ hỏi IP, lấy IP thoát, quốc gia và độ trễ; lưu kết quả vào proxy.</summary>
    Task<ProxyTestResult> TestAsync(ProxyItem proxy, CancellationToken ct = default);
}
