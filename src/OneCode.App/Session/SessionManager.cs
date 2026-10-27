using OneCode.Core.Session;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.Observability;
using OneCode.App.Tools;

namespace OneCode.App.Session;

/// <summary>
/// 会话管理器：负责会话创建、恢复、持久化与列表查询。
/// </summary>
public sealed class SessionManager : ISessionManager
{
    private readonly ISessionStore _store;
    private readonly ILogger<SessionManager> _logger;
    private readonly IShellExecutorCleanup _shellExecutorCleanup;
    private readonly ITokenUsageTracker _tokenUsageTracker;
    private readonly SessionIdHolder _sessionIdHolder;
    private readonly ISessionToolSetManager _sessionToolSetManager;
    private readonly ToolBatchTranscriptAppender _toolBatchAppender;
    private string _workingDirectory;

    private readonly List<BackgroundSession> _backgroundSessions = [];

    /// <summary>
    /// 前台会话（界面当前展示的会话）。
    /// </summary>
    public Conversation? ForegroundConversation { get; private set; }

    /// <inheritdoc />
    public SessionId? CurrentSessionId => ForegroundConversation?.Id;

    public Conversation? GetConversation(SessionId conversationId)
    {
        if (ForegroundConversation?.Id == conversationId)
            return ForegroundConversation;

        return _backgroundSessions
            .FirstOrDefault(session => session.Conversation.Id == conversationId)
            ?.Conversation;
    }

    /// <summary>用户在其他会话中工作时保持存活的后台会话。</summary>
    public IReadOnlyList<BackgroundSession> BackgroundSessions => _backgroundSessions;

    /// <summary>
    /// 当前会话的工作目录，经 <see cref="ChangeWorkingDirectoryAsync"/> 更新。
    /// </summary>
    public string WorkingDirectory => _workingDirectory;

    public SessionManager(
        ISessionStore store,
        ILogger<SessionManager> logger,
        string workingDirectory,
        IShellExecutorCleanup shellExecutorCleanup,
        ITokenUsageTracker tokenUsageTracker,
        SessionIdHolder sessionIdHolder,
        ISessionToolSetManager sessionToolSetManager)
    {
        _store = store;
        _logger = logger;
        _workingDirectory = workingDirectory;
        _shellExecutorCleanup = shellExecutorCleanup;
        _tokenUsageTracker = tokenUsageTracker;
        _sessionIdHolder = sessionIdHolder;
        _sessionToolSetManager = sessionToolSetManager;
        _toolBatchAppender = new ToolBatchTranscriptAppender(this, PersistAsync);
    }

    /// <summary>
    /// 切换会话工作目录并持久化前台会话。
    /// </summary>
    public async Task ChangeWorkingDirectoryAsync(string newCwd, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newCwd)) return;
        if (string.Equals(_workingDirectory, newCwd, StringComparison.OrdinalIgnoreCase)) return;

        _workingDirectory = newCwd;
        if (ForegroundConversation is not null)
        {
            ForegroundConversation.WorkingDirectory = newCwd;
            try { await PersistAsync(ForegroundConversation, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "SessionManager.ChangeWorkingDirectoryAsync save failed"); }
        }
    }

    /// <summary>
    /// 新建会话。持久化延后：磁盘写入推迟到首条消息落盘时（见 <see cref="PersistAsync"/>），
    /// 避免被放弃的空会话留下空文件。
    /// </summary>
    public async Task<Conversation> CreateAsync(
        ConversationOptions options,
        CancellationToken ct = default)
    {
        var conversation = new Conversation
        {
            // 未命名会话的 Name 保持为空：显示标题由 Conversation.DisplayTitle
            // 按首条用户消息推导，不再落时间戳占位名。
            Name = options.Name ?? "",
            WorkingDirectory = options.WorkingDirectory,
            Model = options.Model ?? "",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };

        ForegroundConversation = conversation;
        _sessionIdHolder.SetCurrent(conversation.Id);

        _tokenUsageTracker.Reset();

        _logger.LogInformation(
            "Created conversation {Id} with model {Model}",
            conversation.Id, conversation.Model);

        return conversation;
    }

    /// <summary>
    /// 标记前台会话的工作模式（build/plan/goal）。仅改内存，随下一次转录落盘写入磁盘。
    /// </summary>
    public void SetForegroundMode(string mode)
    {
        if (ForegroundConversation is not null)
            ForegroundConversation.Metadata["mode"] = mode;
    }

    /// <summary>
    /// 持久化会话，但空会话不落盘——只有头部信息的会话文件永不被主动创建。
    /// </summary>
    private Task PersistAsync(Conversation conversation, CancellationToken ct) =>
        conversation.Messages.Count == 0
            ? Task.CompletedTask
            : _store.SaveAsync(conversation, ct);

    /// <summary>
    /// 返回前台会话，不存在时新建一个。
    /// </summary>
    public async Task<Conversation> EnsureActiveSessionAsync(
        ConversationOptions options,
        CancellationToken ct = default)
    {
        if (ForegroundConversation is not null)
        {
            if (!string.IsNullOrWhiteSpace(options.Model))
                ForegroundConversation.Model = options.Model!;
            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
                ForegroundConversation.WorkingDirectory = options.WorkingDirectory;
            return ForegroundConversation;
        }

        return await CreateAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>向前台会话追加一轮用户消息并持久化。</summary>
    public async Task AppendUserMessageAsync(string content, CancellationToken ct = default)
    {
        if (ForegroundConversation is null)
            return;

        await AppendUserMessageAsync(ForegroundConversation.Id, content, ct)
            .ConfigureAwait(false);
    }

    public async Task AppendUserMessageAsync(
        SessionId conversationId,
        string content,
        CancellationToken ct = default)
    {
        var conversation = GetConversation(conversationId);
        if (conversation is null || string.IsNullOrWhiteSpace(content))
            return;

        conversation.Messages.Add(new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: content,
            Timestamp: DateTimeOffset.UtcNow));

        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(conversation, ct).ConfigureAwait(false);
    }

    /// <summary>向前台会话追加一轮助手消息并持久化。</summary>
    public async Task AppendAssistantMessageAsync(
        string content,
        TokenUsage? usage = null,
        CancellationToken ct = default)
    {
        if (ForegroundConversation is null)
            return;

        await AppendAssistantMessageAsync(ForegroundConversation.Id, content, usage, null, ct)
            .ConfigureAwait(false);
    }

    public async Task AppendAssistantMessageAsync(
        SessionId conversationId,
        string content,
        TokenUsage? usage = null,
        CancellationToken ct = default)
    {
        await AppendAssistantMessageAsync(conversationId, content, usage, null, ct)
            .ConfigureAwait(false);
    }

    public async Task AppendAssistantMessageAsync(
        SessionId conversationId,
        string content,
        TokenUsage? usage = null,
        IReadOnlyList<ToolUseBlock>? toolCalls = null,
        CancellationToken ct = default)
    {
        var conversation = GetConversation(conversationId);
        if (conversation is null)
            return;

        List<ContentBlock> contentBlocks = [];
        if (toolCalls is { Count: > 0 })
            contentBlocks.AddRange(toolCalls);
        if (!string.IsNullOrWhiteSpace(content))
            contentBlocks.Add(new TextBlock(content));

        if (contentBlocks.Count > 0)
        {
            conversation.Messages.Add(new AssistantMessage(
                Id: Guid.NewGuid().ToString("N"),
                Content: contentBlocks,
                Timestamp: DateTimeOffset.UtcNow,
                TokenUsage: usage));
        }

        if (usage is not null)
        {
            conversation.TotalUsage = new TokenUsage(
                conversation.TotalUsage.InputTokens + usage.InputTokens,
                conversation.TotalUsage.OutputTokens + usage.OutputTokens,
                conversation.TotalUsage.CacheReadTokens + usage.CacheReadTokens,
                conversation.TotalUsage.CacheWriteTokens + usage.CacheWriteTokens);
        }

        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(conversation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 原子追加已闭合的工具调用/结果批次，并只保存一次会话。
    /// 未闭合的批次直接拒绝，避免写出 provider 不接受的非法历史。
    /// </summary>
    public Task AppendCompletedToolBatchesAsync(
        SessionId conversationId,
        IReadOnlyList<CompletedToolBatch> batches,
        CancellationToken ct = default)
        => _toolBatchAppender.AppendAsync(conversationId, batches, ct);

    /// <summary>构建前台会话的聊天历史（不含系统提示词）。</summary>
    public IReadOnlyList<ChatMessage> GetForegroundChatHistory()
    {
        if (ForegroundConversation is null)
            return [];

        return GetChatHistory(ForegroundConversation.Id);
    }

    public IReadOnlyList<ChatMessage> GetChatHistory(SessionId conversationId)
    {
        var conversation = GetConversation(conversationId);
        return conversation is null
            ? []
            : ConversationMessageMapper.ToChatHistory(conversation.Messages, _logger);
    }

    /// <summary>
    /// 按 ID 恢复已有会话。
    /// </summary>
    public async Task<Conversation?> ResumeAsync(
        string conversationId,
        CancellationToken ct = default)
    {
        var id = SessionId.TryParse(conversationId);
        if (!id.HasValue)
        {
            _logger.LogWarning("Invalid conversation ID: {ConversationId}", conversationId);
            return null;
        }

        var conversation = await _store.LoadAsync(id.Value, ct);
        if (conversation is null)
        {
            _logger.LogWarning("Conversation not found: {ConversationId}", conversationId);
            return null;
        }

        conversation.Status = ConversationStatus.Active;
        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        RemoveBackgroundSession(conversation.Id);
        ForegroundConversation = conversation;
        _sessionIdHolder.SetCurrent(conversation.Id);

        _tokenUsageTracker.Reset();

        // 切换只改内存状态：下一次真实追加时的 SaveAsync 会因快照不一致把新状态落盘，
        // 避免为了改 Status / LastActivityAt 重写整份事件日志。
        _logger.LogInformation(
            "Resumed conversation {Id} with {Count} messages",
            conversation.Id, conversation.Messages.Count);

        return conversation;
    }

    /// <summary>
    /// 列出全部会话，按最后活动时间倒序。
    /// </summary>
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default)
    {
        var summaries = await _store.ListAsync(ct);
        return summaries
            .OrderByDescending(s => s.LastActivityAt)
            .ToList();
    }

    /// <summary>
    /// 保存当前会话状态；会话仍为空时不落盘（持久化延后）。
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (ForegroundConversation is null)
            return;

        ForegroundConversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(ForegroundConversation, ct);
    }

    /// <summary>
    /// 关闭当前会话（异步，清理会话缓存）。
    /// 进程退出时也由 <see cref="DisposeAsync"/> 调用（幂等：无前台会话时为空操作）。
    /// </summary>
    public Task CloseAsync(CancellationToken ct = default) => CloseAsync(SessionEndReason.Close, ct);

    /// <summary>关闭前台会话；<paramref name="reason"/> 仅供日志与产品状态使用。</summary>
    public async Task CloseAsync(string reason, CancellationToken ct = default)
    {
        if (ForegroundConversation is null)
            return;

        var sessionId = ForegroundConversation.Id;
        ForegroundConversation.Status = ConversationStatus.Completed;
        ForegroundConversation.LastActivityAt = DateTimeOffset.UtcNow;

        await PersistAsync(ForegroundConversation, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Closed conversation {Id} - total usage: {Usage}",
            ForegroundConversation.Id, ForegroundConversation.TotalUsage);

        RemoveBackgroundSession(ForegroundConversation.Id);
        await _shellExecutorCleanup.ReleaseAsync(ForegroundConversation.Id).ConfigureAwait(false);
        _sessionToolSetManager.Remove(sessionId.Value);
        ForegroundConversation = null;
        _sessionIdHolder.SetCurrent(null);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // 兜底 reason=other：宿主停止路径由 HostStopSessionCloseService 先行关闭（幂等），此处覆盖未启停宿主的释放路径
            await CloseAsync(SessionEndReason.Other, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionManager.DisposeAsync failed");
        }
    }

    /// <summary>
    /// 继续已有会话——新消息追加到原有会话。
    /// </summary>
    public async Task<Conversation> ContinueAsync(
        string conversationId,
        string userMessage,
        CancellationToken ct = default)
    {
        var conversation = await ResumeAsync(conversationId, ct)
            ?? throw new InvalidOperationException($"Conversation not found: {conversationId}");

        conversation.Messages.Add(new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: userMessage,
            Timestamp: DateTimeOffset.UtcNow));

        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(conversation, ct);

        _logger.LogInformation("Continue conversation {Id} with message", conversation.Id);
        return conversation;
    }

    /// <summary>
    /// 把前台会话转入后台，不打断正在进行的查询。
    /// </summary>
    /// <remarks>
    /// 转入后台只改内存归属：消息在每次追加时已落盘，切换本身不产生新内容，
    /// 不需要重写整份事件日志。
    /// </remarks>
    public Task SuspendForegroundToBackgroundAsync(CancellationToken ct = default)
    {
        if (ForegroundConversation is null)
            return Task.CompletedTask;

        var existing = _backgroundSessions.FirstOrDefault(b => b.Conversation.Id == ForegroundConversation.Id);
        if (existing is null)
            _backgroundSessions.Add(new BackgroundSession(ForegroundConversation));

        ForegroundConversation = null;
        _sessionIdHolder.SetCurrent(null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把前台切换为已有会话（来自磁盘或后台列表）。
    /// </summary>
    public async Task<Conversation?> SwitchToSessionAsync(string conversationId, CancellationToken ct = default)
    {
        var id = SessionId.TryParse(conversationId);
        if (!id.HasValue)
        {
            _logger.LogWarning("Invalid conversation ID: {ConversationId}", conversationId);
            return null;
        }

        if (ForegroundConversation?.Id == id.Value)
            return ForegroundConversation;

        if (ForegroundConversation is not null)
            await SuspendForegroundToBackgroundAsync(ct).ConfigureAwait(false);

        var background = _backgroundSessions.FirstOrDefault(b => b.Conversation.Id == id.Value);
        if (background is not null)
        {
            ForegroundConversation = background.Conversation;
            _sessionIdHolder.SetCurrent(background.Conversation.Id);
            _backgroundSessions.Remove(background);
            ForegroundConversation.Status = ConversationStatus.Active;
            ForegroundConversation.LastActivityAt = DateTimeOffset.UtcNow;
            _tokenUsageTracker.Reset();
            return ForegroundConversation;
        }

        return await ResumeAsync(conversationId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把当前会话转入后台并新建前台会话。
    /// </summary>
    public async Task<Conversation> BackgroundCurrentAndCreateNewAsync(
        ConversationOptions options,
        CancellationToken ct = default)
    {
        if (ForegroundConversation is not null)
            await SuspendForegroundToBackgroundAsync(ct).ConfigureAwait(false);

        return await CreateAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 关闭后台会话并取消其正在运行的查询。
    /// </summary>
    public async Task<bool> CloseBackgroundSessionAsync(string conversationId, CancellationToken ct = default)
    {
        var id = SessionId.TryParse(conversationId);
        if (!id.HasValue)
            return false;

        var background = _backgroundSessions.FirstOrDefault(b => b.Conversation.Id == id.Value);
        if (background is null)
            return false;

        if (background.QueryCancellation is not null)
        {
            try { await background.QueryCancellation.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Background query cancel failed for {ConversationId}", id); }
        }

        background.Conversation.Status = ConversationStatus.Completed;
        background.Conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(background.Conversation, ct).ConfigureAwait(false);
        await _shellExecutorCleanup.ReleaseAsync(background.Conversation.Id).ConfigureAwait(false);
        _sessionToolSetManager.Remove(background.Conversation.Id.Value);
        _backgroundSessions.Remove(background);
        return true;
    }

    public BackgroundSession? FindBackgroundSession(SessionId conversationId) =>
        _backgroundSessions.FirstOrDefault(b => b.Conversation.Id == conversationId);

    public int BackgroundSessionCount => _backgroundSessions.Count;

    private void RemoveBackgroundSession(SessionId conversationId)
    {
        _backgroundSessions.RemoveAll(b => b.Conversation.Id == conversationId);
    }

    /// <summary>
    /// 保存当前会话状态并把转录写入持久化存储。
    /// </summary>
    public async Task PersistTranscriptAsync(CancellationToken ct = default)
    {
        if (ForegroundConversation is null) return;

        ForegroundConversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(ForegroundConversation, ct);

        _logger.LogDebug(
            "Persisted transcript for conversation {Id} ({Count} messages)",
            ForegroundConversation.Id, ForegroundConversation.Messages.Count);
    }
}
