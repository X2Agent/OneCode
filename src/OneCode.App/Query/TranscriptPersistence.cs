using OneCode.App.Session;

namespace OneCode.App.Query;

/// <summary>Persists the assistant transcript and completed tool batches for a finished run.</summary>
internal sealed class TranscriptPersistence(ISessionManager sessionManager, ILogger logger)
{
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly ILogger _logger = logger;

    public async Task PersistAsync(
        QueryStreamRequest request,
        StreamingSession session,
        string finalText,
        TokenUsage finalUsage,
        CancellationToken ct,
        int alreadyPersistedBatchCount = 0)
    {
        try
        {
            if (request.ConversationId is { } completedConversationId)
            {
                // 逐批落盘的批次已入账（appender 按 batchId 幂等去重），这里只补剩余增量。
                var pendingBatches = session.ToolBatchCollector.CompletedBatches
                    .Skip(alreadyPersistedBatchCount)
                    .ToList();
                if (pendingBatches.Count > 0)
                {
                    await _sessionManager.AppendCompletedToolBatchesAsync(
                            completedConversationId,
                            pendingBatches,
                            ct)
                        .ConfigureAwait(false);
                }

                if (session.ToolBatchCollector.HasOpenBatch)
                {
                    _logger.LogWarning(
                        "Dropping incomplete tool batch for conversation {SessionId}, run {RunId}",
                        completedConversationId,
                        request.AgentRunId);
                }

                await _sessionManager.AppendAssistantMessageAsync(
                        completedConversationId,
                        finalText,
                        finalUsage,
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist assistant transcript for session {SessionId}", request.SessionId);
        }
    }

    /// <summary>
    /// 把新封口的工具批次立即持久化（逐批落盘）：长 run 中途被杀只丢尚未封口的当前批次，
    /// 而不是整个 run 的工具历史。appender 按 batchId 幂等去重，与 run 收尾的整批投递不冲突。
    /// 持久化失败不中断 run：收尾的 <see cref="PersistAsync"/> 会重试剩余批次。
    /// </summary>
    /// <returns>更新后的已持久化批次数。</returns>
    public async Task<int> PersistSealedBatchesAsync(
        QueryStreamRequest request,
        StreamingSession session,
        int persistedBatchCount,
        CancellationToken ct)
    {
        var batches = session.ToolBatchCollector.CompletedBatches;
        if (request.ConversationId is not { } conversationId
            || ct.IsCancellationRequested
            || batches.Count <= persistedBatchCount)
            return persistedBatchCount;

        try
        {
            await _sessionManager.AppendCompletedToolBatchesAsync(
                    conversationId,
                    batches.Skip(persistedBatchCount).ToList(),
                    ct)
                .ConfigureAwait(false);
            return batches.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist sealed tool batches for run {RunId}", request.AgentRunId);
            return persistedBatchCount;
        }
    }
}
