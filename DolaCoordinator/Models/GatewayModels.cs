using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DolaCoordinator.Models;

public class VideoGenApiRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "seedance-2.0";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("ratio")]
    public string? Ratio { get; set; }

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("reference_images")]
    public List<string> ReferenceImages { get; set; } = new();

    /// <summary>Đường dẫn tuyệt đối của ảnh tham chiếu trên máy này (gateway chỉ nhận từ loopback).</summary>
    [JsonPropertyName("reference_local_paths")]
    public List<string> ReferenceLocalPaths { get; set; } = new();

    [JsonPropertyName("account")]
    public string? Account { get; set; }

    [JsonPropertyName("cookie")]
    public string? Cookie { get; set; }

    /// <summary>Chạy với cửa sổ Chromium nằm ngoài màn hình (ẩn).</summary>
    [JsonPropertyName("hide_window")]
    public bool HideWindow { get; set; }
}

public class TaskApiResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Lời Dola viết kèm video (gateway: note).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Mã lỗi máy đọc được khi failed: account_limited | credit | risk_control | login_required | unhealthy | timeout | 429 | no_account | error.</summary>
    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; set; }

    [JsonPropertyName("account")]
    public string? Account { get; set; }

    /// <summary>Giai đoạn: warmup → new_chat → submitting → generating → done.</summary>
    [JsonPropertyName("stage")]
    public string? Stage { get; set; }
}

public class GatewayHealthResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("pending_tasks")]
    public int PendingTasks { get; set; }

    [JsonPropertyName("max_pending_tasks")]
    public int MaxPendingTasks { get; set; }
}

/// <summary>Một tài khoản trong pool của gateway (browser_pool.list_accounts).</summary>
public class GatewayAccountDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("scheduling")]
    public bool Scheduling { get; set; } = true;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Kết quả verify gần nhất: true/false, null = chưa kiểm tra.</summary>
    [JsonPropertyName("login_ok")]
    public bool? LoginOk { get; set; }

    [JsonPropertyName("credit_balance")]
    public int? CreditBalance { get; set; }

    [JsonPropertyName("used_today")]
    public int UsedToday { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }

    [JsonPropertyName("busy")]
    public bool Busy { get; set; }

    [JsonPropertyName("cooling")]
    public bool Cooling { get; set; }

    [JsonPropertyName("rate_limited")]
    public bool RateLimited { get; set; }

    [JsonPropertyName("limit_reason")]
    public string? LimitReason { get; set; }

    [JsonPropertyName("quota_blocked")]
    public bool QuotaBlocked { get; set; }

    [JsonPropertyName("quota_reason")]
    public string? QuotaReason { get; set; }
}

public class GatewayAccountsResponse
{
    [JsonPropertyName("accounts")]
    public List<GatewayAccountDto> Accounts { get; set; } = new();
}
