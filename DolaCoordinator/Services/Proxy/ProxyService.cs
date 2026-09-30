using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Profiles;
using DolaCoordinator.Services.Security;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Proxy;

public sealed class ProxyService : IProxyService
{
    public const string FileName = "proxy.txt";

    private readonly IDatabaseService _db;
    private readonly ISecurityService _security;
    private readonly IAccountProfileService _profiles;

    public ProxyService(IDatabaseService db, ISecurityService security, IAccountProfileService profiles)
    {
        _db = db;
        _security = security;
        _profiles = profiles;
    }

    public List<ProxyItem> GetAll() => _db.GetAllProxies();

    public ProxyItem? Get(string? id) => string.IsNullOrEmpty(id) ? null : _db.GetAllProxies().FirstOrDefault(p => p.Id == id);

    public void Save(ProxyItem proxy, string? plainPassword)
    {
        if (plainPassword != null)
            proxy.EncryptedPassword = plainPassword.Length == 0 ? null : _security.Encrypt(plainPassword);
        if (string.IsNullOrWhiteSpace(proxy.Username))
        {
            proxy.Username = null;
            proxy.EncryptedPassword = null;
        }
        // địa chỉ đã đổi thì kết quả kiểm tra cũ không còn đúng
        var old = Get(proxy.Id);
        if (old != null && (old.Host != proxy.Host || old.Port != proxy.Port || old.Scheme != proxy.Scheme || old.Username != proxy.Username))
        {
            proxy.LastOk = null; proxy.LastIp = null; proxy.LastCountry = null; proxy.LastCity = null;
            proxy.LastLatencyMs = null; proxy.LastError = null; proxy.LastTestAt = null;
        }
        _db.UpsertProxy(proxy);

        // proxy đã sửa: các tài khoản đang dùng nó phải có proxy.txt mới
        var users = _db.GetAllProfiles().Where(a => a.ProxyId == proxy.Id).ToList();
        if (users.Count > 0) SyncFiles(users);
        WeakReferenceMessenger.Default.Send(new ProxiesChangedMessage());
    }

    public string BuildUrl(ProxyItem proxy)
    {
        var cred = string.Empty;
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            var pass = string.IsNullOrEmpty(proxy.EncryptedPassword) ? string.Empty : _security.Decrypt(proxy.EncryptedPassword);
            cred = $"{Uri.EscapeDataString(proxy.Username)}:{Uri.EscapeDataString(pass)}@";
        }
        return $"{proxy.Scheme}://{cred}{proxy.Host}:{proxy.Port}";
    }

    public int Delete(string proxyId)
    {
        var users = _db.GetAllProfiles().Where(a => a.ProxyId == proxyId).ToList();
        foreach (var a in users)
        {
            a.ProxyId = null;
            _db.UpsertProfile(a);
            _profiles.ResolvePaths(a);
            DeleteFile(a);
        }
        _db.DeleteProxy(proxyId);
        WeakReferenceMessenger.Default.Send(new ProxiesChangedMessage());
        return users.Count;
    }

    public void Assign(AccountProfile account, string? proxyId)
    {
        account.ProxyId = string.IsNullOrEmpty(proxyId) ? null : proxyId;
        _db.UpsertProfile(account);
        if (string.IsNullOrEmpty(account.FolderPath)) _profiles.ResolvePaths(account);
        WriteOrDelete(account, Get(account.ProxyId));
    }

    public void SyncFiles(IEnumerable<AccountProfile> accounts)
    {
        var all = _db.GetAllProxies().ToDictionary(p => p.Id);
        foreach (var a in accounts)
        {
            if (string.IsNullOrEmpty(a.FolderPath)) _profiles.ResolvePaths(a);
            all.TryGetValue(a.ProxyId ?? string.Empty, out var proxy);
            WriteOrDelete(a, proxy);
        }
    }

    private void WriteOrDelete(AccountProfile account, ProxyItem? proxy)
    {
        if (string.IsNullOrEmpty(account.FolderPath)) return;
        try
        {
            var path = Path.Combine(account.FolderPath, FileName);
            if (proxy == null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(account.FolderPath);
            var url = BuildUrl(proxy);
            if (File.Exists(path) && File.ReadAllText(path).Trim() == url) return;
            File.WriteAllText(path, url, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // thư mục đang bị chương trình khác giữ: lần đồng bộ sau sẽ ghi lại
        }
    }

    private static void DeleteFile(AccountProfile account)
    {
        try
        {
            var path = Path.Combine(account.FolderPath, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task<ProxyTestResult> TestAsync(ProxyItem proxy, CancellationToken ct = default)
    {
        ProxyTestResult result;
        try
        {
            var web = new WebProxy(new Uri($"{proxy.Scheme}://{proxy.Host}:{proxy.Port}"));
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                var pass = string.IsNullOrEmpty(proxy.EncryptedPassword) ? string.Empty : _security.Decrypt(proxy.EncryptedPassword);
                web.Credentials = new NetworkCredential(proxy.Username, pass);
            }
            using var handler = new SocketsHttpHandler
            {
                Proxy = web,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(12),
            };
            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AIOMMO-DolaAI/1.0");

            var sw = Stopwatch.StartNew();
            string body;
            try
            {
                body = await http.GetStringAsync("https://ipinfo.io/json", ct);
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
                body = await http.GetStringAsync("https://api.ipify.org?format=json", ct); // dự phòng: chỉ có IP
            }
            sw.Stop();

            using var doc = JsonDocument.Parse(body);
            string? Str(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var ip = Str("ip");
            result = string.IsNullOrEmpty(ip)
                ? new ProxyTestResult(false, null, null, null, 0, "Không đọc được IP thoát từ dịch vụ kiểm tra.")
                : new ProxyTestResult(true, ip, Str("country"), Str("city"), (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or UriFormatException or InvalidOperationException)
        {
            result = new ProxyTestResult(false, null, null, null, 0, Short(ex));
        }

        proxy.LastTestAt = DateTime.UtcNow;
        proxy.LastOk = result.Ok;
        proxy.LastIp = result.Ip;
        proxy.LastCountry = result.Country;
        proxy.LastCity = result.City;
        proxy.LastLatencyMs = result.Ok ? result.LatencyMs : null;
        proxy.LastError = result.Error;
        _db.UpsertProxy(proxy);
        return result;
    }

    private static string Short(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        var msg = ex is TaskCanceledException ? "Quá thời gian chờ (proxy không phản hồi)." : inner.Message;
        return msg.Length > 160 ? msg[..157] + "..." : msg;
    }
}
