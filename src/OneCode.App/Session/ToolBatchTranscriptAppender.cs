namespace OneCode.App.Session;

/// <summary>
/// 工具批次转录写入器：把已封口的工具调用/结果批次原子地追加到会话转录——
/// 按批次消息 ID 幂等去重（重复投递只落一次），同一会话的并发写入经独立门闩串行化，
/// 全部追加完成后一次性持久化；持久化失败回滚内存追加并向上传播异常。
/// 不完整的批次直接拒绝，避免产生 provider 无效的历史。
/// </summary>
internal sealed class ToolBatchTranscriptAppender(
    ISessionConversationAccess conversations,
    Func<Conversation, CancellationToken, Task> persistAsync)
{
    // 每会话一把的转录写入锁：同一会话的批次追加串行化，跨会话互不阻塞。
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _transcriptLocks = new();

    /// <summary>追加批次并一次性持久化；重复批次幂等去重，持久化失败回滚内存追加后重新抛出。</summary>
    public async Task AppendAsync(
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
            var conversation = conversations.GetConversation(conversationId);
            if (conversation is null)
                return;

            List<Message> appended = [];
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
                await persistAsync(conversation, ct).ConfigureAwait(false);
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
}
