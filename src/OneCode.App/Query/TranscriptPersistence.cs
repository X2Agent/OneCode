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
}
