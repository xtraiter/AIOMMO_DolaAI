using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Helpers;

namespace DolaCoordinator.Services;

public sealed class RepoRulesResult
{
    public string Repo { get; init; } = string.Empty;
    public List<PromptRule> Rules { get; init; } = new();
    /// <summary>true = đã tải được bản mới từ GitHub; false = đang dùng bản lưu sẵn (offline / GitHub lỗi).</summary>
    public bool Online { get; init; }
    /// <summary>Nội dung tài liệu của repo khác với lần trước (repo có bản mới).</summary>
    public bool Changed { get; init; }
    public string? Commit { get; init; }
    public DateTime? CommitDate { get; init; }
    public DateTime? FetchedAt { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Lấy quy tắc đặt prompt từ README của repo đang dùng (tải từ GitHub mỗi lần mở bảng, lưu bản sao để dùng khi offline).
/// Repo có commit / nội dung mới thì lần mở sau tự cập nhật, không cần phát hành lại app.
/// </summary>
public static class RepoRulesService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DolaCoordinator");
        return c;
    }

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DolaCoordinator", "repo-docs");

    private static string SourceFile => Path.Combine(Root, "source.json");

    /// <summary>Repo đang chọn (lưu lại giữa các lần mở app).</summary>
    public static string CurrentRepo
    {
        get
        {
            try
            {
                if (File.Exists(SourceFile))
                {
                    var repo = JsonDocument.Parse(File.ReadAllText(SourceFile)).RootElement.GetProperty("repo").GetString();
                    if (IsValidRepo(repo)) return repo!;
                }
            }
            catch { /* file hỏng → dùng mặc định */ }
            return PromptRules.DefaultRepo;
        }
        set
        {
            if (!IsValidRepo(value)) return;
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(SourceFile, JsonSerializer.Serialize(new { repo = value }));
            }
            catch { /* không lưu được thì lần sau dùng mặc định */ }
        }
    }

    public static bool IsValidRepo(string? repo) =>
        !string.IsNullOrWhiteSpace(repo) && System.Text.RegularExpressions.Regex.IsMatch(repo.Trim(), @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");

    /// <summary>Đọc bản lưu sẵn, không cần mạng (dùng để hiện ngay trong lúc chờ tải).</summary>
    public static RepoRulesResult LoadCached(string repo)
    {
        var dir = RepoDir(repo);
        var rules = new List<PromptRule>();
        DateTime? when = null;
        foreach (var doc in PromptRules.Docs)
        {
            var path = Path.Combine(dir, Safe(doc));
            if (!File.Exists(path)) continue;
            rules.AddRange(RepoRuleParser.Extract(File.ReadAllText(path), $"{repo}/{doc}"));
            var t = File.GetLastWriteTime(path);
            if (when == null || t > when) when = t;
        }
        var (commit, date) = ReadMeta(dir);
        return new RepoRulesResult
        {
            Repo = repo, Rules = rules, Online = false, Commit = commit, CommitDate = date, FetchedAt = when,
            Message = rules.Count == 0 ? "Chưa có bản lưu của repo này (cần mạng để tải lần đầu)." : "Đang hiện bản đã lưu…",
        };
    }

    /// <summary>Tải README của repo từ GitHub; lỗi mạng thì trả về bản lưu sẵn.</summary>
    public static async Task<RepoRulesResult> RefreshAsync(string repo, string branch = PromptRules.DefaultBranch, CancellationToken ct = default)
    {
        var dir = RepoDir(repo);
        Directory.CreateDirectory(dir);
        var changed = false;
        var fetched = 0;
        foreach (var doc in PromptRules.Docs)
        {
            try
            {
                using var resp = await Http.GetAsync($"https://raw.githubusercontent.com/{repo}/{branch}/{doc}", ct);
                if (!resp.IsSuccessStatusCode) continue; // repo không có file này
                var text = await resp.Content.ReadAsStringAsync(ct);
                var path = Path.Combine(dir, Safe(doc));
                if (File.Exists(path) && Hash(File.ReadAllText(path)) != Hash(text)) changed = true;
                File.WriteAllText(path, text);
                fetched++;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* offline hoặc GitHub chặn: dùng bản lưu */ }
        }

        string? commit = null;
        DateTime? commitDate = null;
        if (fetched > 0)
        {
            try
            {
                using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/commits/{branch}", ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    commit = json.RootElement.GetProperty("sha").GetString()?[..7];
                    commitDate = json.RootElement.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTime().ToLocalTime();
                    File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new { commit, date = commitDate }));
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* commit chỉ để hiển thị */ }
        }

        var cached = LoadCached(repo);
        var (c2, d2) = (commit ?? cached.Commit, commitDate ?? cached.CommitDate);
        return new RepoRulesResult
        {
            Repo = repo, Rules = cached.Rules, Online = fetched > 0, Changed = changed, Commit = c2, CommitDate = d2,
            FetchedAt = fetched > 0 ? DateTime.Now : cached.FetchedAt,
            Message = fetched == 0
                ? (cached.Rules.Count == 0 ? "Không tải được repo (kiểm tra mạng) và chưa có bản lưu." : "Không tải được repo, đang dùng bản đã lưu.")
                : changed ? "Repo có bản mới: đã cập nhật quy tắc." : "Đã kiểm tra: quy tắc đang là bản mới nhất của repo.",
        };
    }

    private static (string? commit, DateTime? date) ReadMeta(string dir)
    {
        try
        {
            var p = Path.Combine(dir, "meta.json");
            if (!File.Exists(p)) return (null, null);
            var root = JsonDocument.Parse(File.ReadAllText(p)).RootElement;
            return (root.GetProperty("commit").GetString(), root.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetDateTime() : null);
        }
        catch { return (null, null); }
    }

    private static string RepoDir(string repo) => Path.Combine(Root, repo.Replace('/', '_'));
    private static string Safe(string doc) => doc.Replace('/', '_');
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
