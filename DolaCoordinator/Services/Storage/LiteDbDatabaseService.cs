using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DolaCoordinator.Models;
using LiteDB;

namespace DolaCoordinator.Services.Storage;

public class LiteDbDatabaseService : IDatabaseService
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DolaSession> _sessions;
    private readonly ILiteCollection<AccountProfile> _profiles;
    private readonly ILiteCollection<RenderTask> _tasks;
    private readonly ILiteCollection<PromptItem> _prompts;
    private readonly ILiteCollection<AppSettings> _settings;
    private readonly object _lock = new();

    public LiteDbDatabaseService(string? customDbPath = null)
    {
        string dbPath;
        if (!string.IsNullOrWhiteSpace(customDbPath))
        {
            dbPath = customDbPath;
        }
        else
        {
            var appData = DolaCoordinator.Helpers.AppPaths.DataDir;
            dbPath = Path.Combine(appData, "coordinator.db");
        }

        var connectionString = new ConnectionString
        {
            Filename = dbPath,
            Connection = ConnectionType.Shared
        };

        _db = new LiteDatabase(connectionString);
        _sessions = _db.GetCollection<DolaSession>("sessions");
        // Di trú: collection cũ "chrome_profiles" được đổi tên thành "account_profiles" (giữ nguyên ghi chú/ngày tạo).
        if (_db.CollectionExists("chrome_profiles"))
        {
            if (!_db.CollectionExists("account_profiles"))
                _db.GetCollection<AccountProfile>("account_profiles").Upsert(_db.GetCollection<AccountProfile>("chrome_profiles").FindAll());
            _db.DropCollection("chrome_profiles");
        }
        _profiles = _db.GetCollection<AccountProfile>("account_profiles");
        _tasks = _db.GetCollection<RenderTask>("tasks");
        _prompts = _db.GetCollection<PromptItem>("prompts");
        _settings = _db.GetCollection<AppSettings>("settings");

        // Indexes
        _sessions.EnsureIndex(x => x.Status);
        _tasks.EnsureIndex(x => x.Status);
        _tasks.EnsureIndex(x => x.CreatedAt);
    }

    #region Sessions
    public List<DolaSession> GetAllSessions()
    {
        lock (_lock)
        {
            return _sessions.FindAll().OrderByDescending(s => s.CreatedAt).ToList();
        }
    }

    public DolaSession? GetSessionById(string id)
    {
        lock (_lock)
        {
            return _sessions.FindById(id);
        }
    }

    public void UpsertSession(DolaSession session)
    {
        lock (_lock)
        {
            _sessions.Upsert(session);
        }
    }

    public void UpsertSessions(IEnumerable<DolaSession> sessions)
    {
        lock (_lock)
        {
            _sessions.Upsert(sessions);
        }
    }

    public bool DeleteSession(string id)
    {
        lock (_lock)
        {
            return _sessions.Delete(id);
        }
    }

    public void DeleteAllSessions()
    {
        lock (_lock)
        {
            _sessions.DeleteAll();
        }
    }
    #endregion

    #region Account profiles
    public List<AccountProfile> GetAllProfiles()
    {
        lock (_lock)
        {
            return _profiles.FindAll().OrderBy(p => p.CreatedAt).ToList();
        }
    }

    public AccountProfile? GetProfileById(string id)
    {
        lock (_lock)
        {
            return _profiles.FindById(id);
        }
    }

    public void UpsertProfile(AccountProfile profile)
    {
        lock (_lock)
        {
            _profiles.Upsert(profile);
        }
    }

    public bool DeleteProfile(string id)
    {
        lock (_lock)
        {
            return _profiles.Delete(id);
        }
    }
    #endregion

    #region Prompts
    public List<PromptItem> GetAllPrompts()
    {
        lock (_lock)
        {
            return _prompts.FindAll().OrderByDescending(p => p.UpdatedAt).ToList();
        }
    }

    public void UpsertPrompt(PromptItem prompt)
    {
        lock (_lock)
        {
            _prompts.Upsert(prompt);
        }
    }

    public void UpsertPrompts(IEnumerable<PromptItem> prompts)
    {
        lock (_lock)
        {
            _prompts.Upsert(prompts);
        }
    }

    public void DeletePrompts(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids) _prompts.Delete(id);
        }
    }
    #endregion

    #region Tasks
    public List<RenderTask> GetAllTasks()
    {
        lock (_lock)
        {
            return _tasks.FindAll().OrderBy(t => t.CreatedAt).ToList();
        }
    }

    public RenderTask? GetTaskById(string id)
    {
        lock (_lock)
        {
            return _tasks.FindById(id);
        }
    }

    public void UpsertTask(RenderTask task)
    {
        lock (_lock)
        {
            _tasks.Upsert(task);
        }
    }

    public void UpsertTasks(IEnumerable<RenderTask> tasks)
    {
        lock (_lock)
        {
            _tasks.Upsert(tasks);
        }
    }

    public bool DeleteTask(string id)
    {
        lock (_lock)
        {
            return _tasks.Delete(id);
        }
    }

    public void DeleteTasks(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                _tasks.Delete(id);
            }
        }
    }

    public void DeleteCompletedTasks()
    {
        lock (_lock)
        {
            _tasks.DeleteMany(t => t.Status == RenderTaskStatus.Completed || t.Status == RenderTaskStatus.Cancelled);
        }
    }

    public void DeleteFailedTasks()
    {
        lock (_lock)
        {
            _tasks.DeleteMany(t => t.Status == RenderTaskStatus.Failed || t.Status == RenderTaskStatus.Cancelled);
        }
    }

    public void ClearAllTasks()
    {
        lock (_lock)
        {
            _tasks.DeleteAll();
        }
    }
    #endregion

    #region Settings
    public AppSettings GetSettings()
    {
        lock (_lock)
        {
            var s = _settings.FindById(1);
            if (s == null)
            {
                s = new AppSettings { Id = 1 };
                _settings.Insert(s);
            }

            // Di trú bảo mật: các bản cũ mặc định tự cập nhật từ một kho GitHub không thuộc quyền kiểm soát của người dùng.
            // Gói tải về được giải nén đè lên thư mục chạy nên URL đó phải do chính người dùng/công ty đặt lại.
            if (s.UpdateCheckUrl?.Contains("coll3879xx-cyber", StringComparison.OrdinalIgnoreCase) == true)
            {
                s.UpdateCheckUrl = string.Empty;
                _settings.Upsert(s);
            }
            return s;
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_lock)
        {
            settings.Id = 1;
            _settings.Upsert(settings);
        }
    }
    #endregion

    public void Dispose()
    {
        _db?.Dispose();
    }
}
