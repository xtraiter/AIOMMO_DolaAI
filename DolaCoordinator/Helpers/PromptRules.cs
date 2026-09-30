using System.Collections.Generic;

namespace DolaCoordinator.Helpers;

/// <summary>Một quy tắc đặt prompt lấy từ README của repo (tiêu đề mục, nội dung nguyên văn, nguồn = file trong repo).</summary>
public sealed record PromptRule(string Title, string Detail, string Source)
{
    public string AsText() => string.IsNullOrWhiteSpace(Title) ? Detail : $"{Title}\n{Detail}";
}

/// <summary>Repo mặc định, các repo đã biết và các file tài liệu được đọc cho cửa sổ quy tắc.</summary>
public static class PromptRules
{
    /// <summary>Repo mặc định + các repo đã biết; chọn repo nào thì hiện quy tắc của repo đó.</summary>
    public static readonly string[] KnownRepos =
    {
        "coll3879xx-cyber/dola-render-gateway",
        "Roins-hub/dola-pool",
    };

    public const string DefaultRepo = "coll3879xx-cyber/dola-render-gateway";
    public const string DefaultBranch = "main";

    /// <summary>File tài liệu của repo được đọc để tìm quy tắc (file nào repo không có thì bỏ qua).</summary>
    public static readonly string[] Docs = { "README.md", "extensions/dola30/README.md", "API.md" };
}
