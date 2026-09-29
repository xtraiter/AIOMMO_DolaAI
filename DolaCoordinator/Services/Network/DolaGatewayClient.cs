using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Network;

public class DolaGatewayClient : IDolaGatewayClient
{
    private readonly HttpClient _httpClient;
    private readonly IDatabaseService _databaseService;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IGatewayHost _gatewayHost;

    public DolaGatewayClient(HttpClient httpClient, IDatabaseService databaseService, IGatewayHost gatewayHost)
    {
        _httpClient = httpClient;
        _databaseService = databaseService;
        _gatewayHost = gatewayHost;
    }

    private string GetGatewayBaseUrl()
    {
        var settings = _databaseService.GetSettings();
        var url = settings.GatewayUrl?.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://127.0.0.1:8000";
        return url.TrimEnd('/');
    }

    /// <summary>API khách (/v1/*): Bearer token.</summary>
    private void ApplyAuthHeader(HttpRequestMessage request, string? clientApiKey)
    {
        var settings = _databaseService.GetSettings();
        var key = !string.IsNullOrWhiteSpace(clientApiKey) ? clientApiKey : settings.ClientApiKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        }
    }

    /// <summary>API quản trị (/api/admin/*): header X-Admin-Key (DOLA_ADMIN_KEY của gateway), không phải Bearer.</summary>
    private void ApplyAdminHeader(HttpRequestMessage request)
    {
        var key = _databaseService.GetSettings().AdminKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.TryAddWithoutValidation("X-Admin-Key", key.Trim());
        }
    }

    public async Task<GatewayHealthResponse?> CheckHealthAsync(string? gatewayUrl = null, CancellationToken ct = default)
    {
        try
        {
            var baseUrl = !string.IsNullOrWhiteSpace(gatewayUrl) ? gatewayUrl.TrimEnd('/') : GetGatewayBaseUrl();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var response = await _httpClient.GetAsync($"{baseUrl}/health", cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            return JsonSerializer.Deserialize<GatewayHealthResponse>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TaskApiResponse?> CreateVideoTaskAsync(VideoGenApiRequest request, string? clientApiKey = null, CancellationToken ct = default)
    {
        var baseUrl = GetGatewayBaseUrl();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/videos/generations");
        ApplyAuthHeader(httpRequest, clientApiKey);

        var payload = JsonSerializer.Serialize(request, JsonOptions);
        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(httpRequest, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gateway error ({(int)response.StatusCode}): {json}");
        }

        return JsonSerializer.Deserialize<TaskApiResponse>(json, JsonOptions);
    }

    public async Task<TaskApiResponse?> GetTaskStatusAsync(string taskId, string? clientApiKey = null, CancellationToken ct = default)
    {
        var baseUrl = GetGatewayBaseUrl();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/videos/{taskId}");
        ApplyAuthHeader(httpRequest, clientApiKey);

        var response = await _httpClient.SendAsync(httpRequest, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Status query failed: {response.StatusCode} - {json}");
        }

        return JsonSerializer.Deserialize<TaskApiResponse>(json, JsonOptions);
    }

    public async Task<List<GatewayAccountDto>?> GetAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{GetGatewayBaseUrl()}/api/admin/accounts");
            ApplyAdminHeader(req);

            using var response = await _httpClient.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode) return null; // 401: sai Admin Key

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            return JsonSerializer.Deserialize<GatewayAccountsResponse>(json, JsonOptions)?.Accounts;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // gateway chưa chạy / quá thời gian
        }
    }

    public async Task<(bool Ok, bool? LoginOk, string? Error)> VerifyAccountAsync(string name, CancellationToken ct = default)
    {
        var host = await _gatewayHost.EnsureRunningAsync(ct); // người dùng bấm "Kiểm tra phiên" → bật gateway ngầm nếu cần
        if (!host.Ok) return (false, null, $"Không bật được gateway: {host.Error}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{GetGatewayBaseUrl()}/api/admin/accounts/{Uri.EscapeDataString(name)}/verify");
            ApplyAdminHeader(req);

            // Gateway mở Chromium headless và vào dola.com/chat nên có thể mất vài chục giây
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            using var response = await _httpClient.SendAsync(req, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body);
                bool? loginOk = doc.RootElement.ValueKind == JsonValueKind.Object
                                && doc.RootElement.TryGetProperty("login_ok", out var p)
                                && p.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? p.GetBoolean()
                    : null;
                return (true, loginOk, null);
            }

            return (false, null, response.StatusCode switch
            {
                HttpStatusCode.Conflict => "Tài khoản đang render hoặc profile đang mở — đóng profile / chờ render xong rồi kiểm tra lại.",
                HttpStatusCode.NotFound => $"Gateway không thấy tài khoản '{name}' (thiếu thư mục accounts/{name}).",
                HttpStatusCode.Unauthorized => "Sai Admin Key — nhập đúng DOLA_ADMIN_KEY của gateway trong Cài đặt.",
                _ => $"Gateway phản hồi {(int)response.StatusCode}: {body}",
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, "Gateway chưa chạy hoặc không phản hồi (kiểm tra tab Cài đặt).");
        }
    }

    public async Task<(bool Ok, bool Unreachable, string? Error)> DeleteAccountAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete,
                $"{GetGatewayBaseUrl()}/api/admin/accounts/{Uri.EscapeDataString(name)}");
            ApplyAdminHeader(req);

            using var response = await _httpClient.SendAsync(req, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return (true, false, null);

            var body = await response.Content.ReadAsStringAsync(ct);
            return (false, false, response.StatusCode switch
            {
                HttpStatusCode.Conflict => "Tài khoản đang render, không thể xóa.",
                HttpStatusCode.Unauthorized => "Sai Admin Key — nhập đúng DOLA_ADMIN_KEY của gateway trong Cài đặt.",
                _ => $"Gateway phản hồi {(int)response.StatusCode}: {body}",
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, true, "Gateway chưa chạy.");
        }
    }

    public async Task<(bool Success, string? ConvertedCookie, string? ErrorMessage)> ImportCookieWithResultAsync(
        string name, string cookieToken, CancellationToken ct = default)
    {
        var host = await _gatewayHost.EnsureRunningAsync(ct);
        if (!host.Ok) return (false, null, $"Không bật được gateway: {host.Error}");
        try
        {
            var baseUrl = GetGatewayBaseUrl();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/admin/accounts/import_cookie");
            ApplyAdminHeader(req);

            var payload = JsonSerializer.Serialize(new { name, cookie = cookieToken }, JsonOptions);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(req, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"Gateway phản hồi ({response.StatusCode}): {json}");
            }

            using var doc = JsonDocument.Parse(json);
            string? converted = null;
            if (doc.RootElement.TryGetProperty("cookie", out var cookieProp) && cookieProp.ValueKind == JsonValueKind.String)
            {
                converted = cookieProp.GetString();
            }

            return (true, converted, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<Stream> OpenVideoStreamAsync(string videoUrl, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }
}
