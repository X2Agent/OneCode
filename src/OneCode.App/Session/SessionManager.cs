using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.Observability;
using OneCode.App.Tools;

namespace OneCode.App.Session;

/// <summary>
/// Manages conversations — creation, resume, persistence, and listing.
/// </summary>
public sealed class SessionManager : ISessionManager
{
    private readonly ISessionStore _store;
    private readonly ILogger<SessionManager> _logger;
    private readonly IHookExecutionService _hookExecutionService;
    private readonly IShellExecutorCleanup _shellExecutorCleanup;
    private readonly ITokenUsageTracker _tokenUsageTracker;
    private readonly SessionIdHolder _sessionIdHolder;
    private readonly ISessionToolSetManager _sessionToolSetManager;
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _transcriptLocks = new();
    private string _workingDirectory;

    private readonly List<BackgroundSession> _backgroundSessions = [];

    /// <summary>
    /// Foreground conversation shown in the TUI.
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

    /// <summary>Background sessions kept alive while the user works in another conversation.</summary>
    public IReadOnlyList<BackgroundSession> BackgroundSessions => _backgroundSessions;

    /// <summary>
    /// The working directory for this session. Updated via <see cref="ChangeWorkingDirectoryAsync"/>.
    /// </summary>
    public string WorkingDirectory => _workingDirectory;

    public SessionManager(
        ISessionStore store,
        ILogger<SessionManager> logger,
        string workingDirectory,
        IHookExecutionService hookExecutionService,
        IShellExecutorCleanup shellExecutorCleanup,
        ITokenUsageTracker tokenUsageTracker,
        SessionIdHolder sessionIdHolder,
        ISessionToolSetManager sessionToolSetManager)
    {
        _store = store;
        _logger = logger;
        _workingDirectory = workingDirectory;
        _hookExecutionService = hookExecutionService;
        _shellExecutorCleanup = shellExecutorCleanup;
        _tokenUsageTracker = tokenUsageTracker;
        _sessionIdHolder = sessionIdHolder;
        _sessionToolSetManager = sessionToolSetManager;
    }

    /// <summary>
    /// Change the session's working directory and persist the foreground conversation.
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
    /// Create a new conversation. Persistence is deferred: nothing is written to
    /// disk until the first message lands (see <see cref="PersistAsync"/>), so
    /// abandoned sessions leave no empty files behind.
    /// </summary>
    public async Task<Conversation> CreateAsync(
        ConversationOptions options,
        CancellationToken ct = default)
    {
        var conversation = new Conversation
        {
            Name = options.Name ?? $"conversation-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
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
            "Created conversation {Id} '{Name}' with model {Model}",
            conversation.Id, conversation.Name, conversation.Model);

        await FireHookAsync(HookEvent.SessionStart, "startup", conversation.Id, ct);

        return conversation;
    }

    /// <summary>
    /// Stamp the foreground conversation's working mode (build/plan/team/goal).
    /// In-memory only — the value persists with the next transcript save.
    /// </summary>
    public void SetForegroundMode(string mode)
    {
        if (ForegroundConversation is not null)
            ForegroundConversation.Metadata["mode"] = mode;
    }

    /// <summary>
    /// Persist a conversation unless it is still empty — header-only session
    /// files are never written proactively.
    /// </summary>
    private Task PersistAsync(Conversation conversation, CancellationToken ct) =>
        conversation.Messages.Count == 0
            ? Task.CompletedTask
            : _store.SaveAsync(conversation, ct);

    /// <summary>
    /// Returns the foreground conversation, creating one when missing.
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

    /// <summary>Append a user turn to the foreground conversation and persist.</summary>
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

    /// <summary>Append an assistant turn to the foreground conversation and persist.</summary>
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

        var contentBlocks = new List<ContentBlock>();
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
                conversation.TotalUsage.CacheWriteTokens + usage.CacheWriteTokens,
                conversation.TotalUsage.TotalCostUsd);
        }

        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(conversation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically appends sealed call/result batches and saves the conversation once.
    /// Incomplete batches are rejected instead of producing provider-invalid history.
    /// </summary>
    public async Task AppendCompletedToolBatchesAsync(
        SessionId conversationId,
        IReadOnlyList<CompletedToolBatch> batches,
        CancellationToken ct = default)
    {
        if (batches.Count == 0)
            return;

        foreach (var batch in batches)
        {
            if (!batch.IsComplete)
                throw new InvalidOperationException($"Tool batch '{batch.BatchId}' is incomplete.");
        }

        var transcriptLock = _transcriptLocks.GetOrAdd(conversationId, static _ => new SemaphoreSlim(1, 1));
        await transcriptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conversation = GetConversation(conversationId);
            if (conversation is null)
                return;

            var appended = new List<Message>();
            foreach (var batch in batches)
            {
                var batchMessageId = ToolBatchMessageId(batch.BatchId);
                if (conversation.Messages.Any(message =>
                        string.Equals(message.Id, batchMessageId, StringComparison.Ordinal)))
                {
                    continue;
                }

                var assistantMessage = new AssistantMessage(
                    Id: batchMessageId,
                    Content: batch.Calls.OrderBy(call => call.Order)
                        .Select(call => (ContentBlock)new ToolUseBlock(
                            call.CallId,
                            call.ToolName,
                            call.ArgumentsJson))
                        .ToArray(),
                    Timestamp: batch.CompletedAt);
                conversation.Messages.Add(assistantMessage);
                appended.Add(assistantMessage);

                foreach (var result in batch.Results.OrderBy(result => result.Order))
                {
                    var resultMessage = new ToolResultMessage(
                        Id: ToolBatchResultMessageId(batch.BatchId, result.CallId),
                        ToolUseId: result.CallId,
                        ToolName: result.ToolName,
                        Content: result.ResultJson,
                        IsError: result.IsError,
                        Timestamp: batch.CompletedAt);
                    conversation.Messages.Add(resultMessage);
                    appended.Add(resultMessage);
                }
            }

            if (appended.Count == 0)
                return;

            var previousLastActivityAt = conversation.LastActivityAt;
            conversation.LastActivityAt = DateTimeOffset.UtcNow;
            try
            {
                await PersistAsync(conversation, ct).ConfigureAwait(false);
            }
            catch
            {
                foreach (var message in appended)
                    conversation.Messages.Remove(message);
                conversation.LastActivityAt = previousLastActivityAt;
                throw;
            }
        }
        finally
        {
            transcriptLock.Release();
        }
    }

    private static string ToolBatchMessageId(string batchId) => $"tool-batch:{batchId}";

    private static string ToolBatchResultMessageId(string batchId, string callId)
        => $"tool-batch:{batchId}:result:{callId}";

    /// <summary>Build chat history for the foreground conversation (excluding system prompt).</summary>
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
    /// Resume an existing conversation by ID.
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
        if (conversation == null)
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

        if (conversation.Metadata.TryGetValue("mode", out var modeObj) && modeObj is string sessionMode)
        {
            if (sessionMode == "coordinator")
                _logger.LogInformation("Resumed session was in coordinator mode; continuing in normal mode.");
        }

        await PersistAsync(conversation, ct);

        _logger.LogInformation(
            "Resumed conversation {Id} with {Count} messages",
            conversation.Id, conversation.Messages.Count);

        await FireHookAsync(HookEvent.SessionStart, "resume", conversation.Id, ct);

        return conversation;
    }

    /// <summary>
    /// List all available conversations, sorted by last activity.
    /// </summary>
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default)
    {
        var summaries = await _store.ListAsync(ct);
        return summaries
            .OrderByDescending(s => s.LastActivityAt)
            .ToList();
    }

    /// <summary>
    /// Save the current conversation state. No-op while the conversation is
    /// still empty (deferred persistence).
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (ForegroundConversation == null)
            return;

        ForegroundConversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(ForegroundConversation, ct);
    }

    /// <summary>
    /// Close the current conversation (async — fires SessionEnd hook and clears session cache).
    /// Also invoked by <see cref="DisposeAsync"/> at shutdown (idempotent: no-op when no foreground conversation).
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (ForegroundConversation == null)
            return;

        var sessionId = ForegroundConversation.Id;
        ForegroundConversation.Status = ConversationStatus.Completed;
        ForegroundConversation.LastActivityAt = DateTimeOffset.UtcNow;

        await PersistAsync(ForegroundConversation, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Closed conversation {Id} - total usage: {Usage}",
            ForegroundConversation.Id, ForegroundConversation.TotalUsage);

        await FireHookAsync(HookEvent.SessionEnd, "close", sessionId, ct);

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
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionManager.DisposeAsync failed");
        }
    }

    /// <summary>
    /// Continue an existing conversation — appends new messages to the existing session.
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
    /// Replay a conversation transcript without re-executing side effects.
    /// </summary>
    public async Task<IReadOnlyList<Message>> ReplayAsync(
        string conversationId,
        CancellationToken ct = default)
    {
        var id = SessionId.TryParse(conversationId)
            ?? throw new ArgumentException($"Invalid conversation ID: {conversationId}");

        var conversation = await _store.LoadAsync(id, ct);
        if (conversation == null)
            throw new InvalidOperationException($"Conversation not found: {conversationId}");

        _logger.LogInformation(
            "Replay conversation {Id} with {Count} messages (read-only, no side effects)",
            conversation.Id, conversation.Messages.Count);

        return conversation.Messages.ToList();
    }

    /// <summary>
    /// Move the foreground conversation to the background without cancelling an active query.
    /// </summary>
    public async Task SuspendForegroundToBackgroundAsync(CancellationToken ct = default)
    {
        if (ForegroundConversation is null)
            return;

        await SaveAsync(ct).ConfigureAwait(false);

        var existing = _backgroundSessions.FirstOrDefault(b => b.Conversation.Id == ForegroundConversation.Id);
        if (existing is null)
            _backgroundSessions.Add(new BackgroundSession(ForegroundConversation));

        ForegroundConversation = null;
        _sessionIdHolder.SetCurrent(null);
    }

    /// <summary>
    /// Switch foreground to an existing conversation (from disk or background list).
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
            await PersistAsync(ForegroundConversation, ct).ConfigureAwait(false);
            await FireHookAsync(HookEvent.SessionStart, "switch", ForegroundConversation.Id, ct);
            return ForegroundConversation;
        }

        return await ResumeAsync(conversationId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Background the current conversation and create a new foreground session.
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
    /// Close a background session and cancel any running query.
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
    /// Save current session state and write transcript to persistent storage.
    /// </summary>
    public async Task PersistTranscriptAsync(CancellationToken ct = default)
    {
        if (ForegroundConversation == null) return;

        ForegroundConversation.LastActivityAt = DateTimeOffset.UtcNow;
        await PersistAsync(ForegroundConversation, ct);

        _logger.LogDebug(
            "Persisted transcript for conversation {Id} ({Count} messages)",
            ForegroundConversation.Id, ForegroundConversation.Messages.Count);
    }

    private async Task FireHookAsync(HookEvent @event, string source, SessionId sessionId, CancellationToken ct = default)
    {
        var payload = new HookPayload
        {
            Event = @event,
            SessionId = sessionId,
            Cwd = _workingDirectory,
        };

        await _hookExecutionService.FireAsync(payload, actualMatcherValue: source, ct: ct);
    }
}
