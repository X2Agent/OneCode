using OneCode.App.Session;

namespace OneCode.App.Query;

/// <summary>Persists the assistant transcript and completed tool batches for a finished run.</summary>
internal sealed class TranscriptPersistence
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger _logger;

    public TranscriptPersistence(ISessionManager sessionManager, ILogger logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task PersistAsync(
        QueryStreamRequest request,
        StreamingSession session,
        string finalText,
        TokenUsage finalUsage,
        CancellationToken ct)
    {
        try
        {
            if (request.ConversationId is { } completedConversationId)
            {
                if (session.ToolBatchCollector.CompletedBatches.Count > 0)
                {
                    await _sessionManager.AppendCompletedToolBatchesAsync(
                            completedConversationId,
                            session.ToolBatchCollector.CompletedBatches,
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
