using OneCode.Core.Domain;

namespace OneCode.Core.Session;

/// <summary>从会话事件派生领域消息视图。</summary>
public static class SessionEventProjection
{
    /// <summary>按事件顺序派生可供 LLM 和 UI 使用的领域消息。</summary>
    public static IReadOnlyList<Message> DeriveMessages(IEnumerable<SessionEvent> events)
    {
        List<Message> messages = [];
        foreach (var sessionEvent in events.OrderBy(item => item.Sequence).ThenBy(item => item.OccurredAt))
        {
            switch (sessionEvent)
            {
                case MessagesReplacedEvent replaced:
                    messages.Clear();
                    messages.AddRange(replaced.Messages);
                    break;
                case UserMessageEvent user:
                    messages.Add(user.Message);
                    break;
                case SystemMessageEvent system:
                    messages.Add(system.Message);
                    break;
                case AssistantMessageEvent assistant:
                    messages.Add(assistant.Message);
                    break;
                case ToolResultEvent toolResult:
                    messages.Add(toolResult.Message);
                    break;
                case AttachmentMessageEvent attachment:
                    messages.Add(attachment.Message);
                    break;
                case TombstoneMessageEvent tombstone:
                    messages.Add(tombstone.Message);
                    break;
            }
        }

        return messages;
    }

    /// <summary>从事件序列取得最新的会话元数据快照。</summary>
    public static SessionStartedEvent FindSessionSnapshot(IEnumerable<SessionEvent> events) =>
        FindLatestSessionSnapshot(events)
            ?? throw new InvalidOperationException("Session event log is missing its creation snapshot.");
    public static SessionStartedEvent? FindLatestSessionSnapshot(IEnumerable<SessionEvent> events)
    {
        var latestStarted = events.OfType<SessionStartedEvent>().MaxBy(item => item.Sequence);
        var latestSnapshot = events.OfType<SessionSnapshotEvent>().MaxBy(item => item.Sequence);

        return latestStarted is null || latestSnapshot is not null && latestSnapshot.Sequence > latestStarted.Sequence
            ? latestSnapshot is null
                ? latestStarted
                : new SessionStartedEvent(
                    latestSnapshot.SessionId,
                    latestSnapshot.Sequence,
                    latestSnapshot.OccurredAt,
                    latestSnapshot.Name,
                    latestSnapshot.WorkingDirectory,
                    latestSnapshot.Model,
                    latestSnapshot.Status,
                    latestSnapshot.TotalUsage,
                    latestSnapshot.CreatedAt,
                    latestSnapshot.LastActivityAt,
                    latestSnapshot.Branch,
                    latestSnapshot.Metadata)
            : latestStarted;
    }
}