using System;
using System.Collections.Generic;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Storage;

public interface IDatabaseService : IDisposable
{
    // Sessions
    List<DolaSession> GetAllSessions();
    DolaSession? GetSessionById(string id);
    void UpsertSession(DolaSession session);
    void UpsertSessions(IEnumerable<DolaSession> sessions);
    bool DeleteSession(string id);
    void DeleteAllSessions();

    // Thư viện prompt
    List<PromptItem> GetAllPrompts();
    void UpsertPrompt(PromptItem prompt);
    void UpsertPrompts(IEnumerable<PromptItem> prompts);
    void DeletePrompts(IEnumerable<string> ids);

    // Tài khoản Dola (profile của gateway)
    List<AccountProfile> GetAllProfiles();
    AccountProfile? GetProfileById(string id);
    void UpsertProfile(AccountProfile profile);
    bool DeleteProfile(string id);

    // Tasks
    List<RenderTask> GetAllTasks();
    RenderTask? GetTaskById(string id);
    void UpsertTask(RenderTask task);
    void UpsertTasks(IEnumerable<RenderTask> tasks);
    bool DeleteTask(string id);
    void DeleteTasks(IEnumerable<string> ids);
    void DeleteCompletedTasks();
    void DeleteFailedTasks();
    void ClearAllTasks();

    // Settings
    AppSettings GetSettings();
    void SaveSettings(AppSettings settings);
}
