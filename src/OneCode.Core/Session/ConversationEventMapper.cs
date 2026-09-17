using OneCode.Core.Domain;

namespace OneCode.Core.Session;

/// <summary>将当前 Conversation 状态映射为一次性事件批次。</summary>
public static class ConversationEventMapper
{
    /// <summary>生成创建快照和所有领域消息事件，顺序由 EventStore 重新分配。</summary>
    public static IReadOnlyList<SessionEvent> ToEvents(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var events = new List<SessionEvent>
        {
            new SessionStartedEvent(
                conversation.Id,
                0,
                conversation.CreatedAt,
                conversation.Name,
                conversation.WorkingDirectory,
                conversation.Model,
                conversation.Status,
                conversation.TotalUsage,
                conversation.CreatedAt,
                conversation.LastActivityAt,
                conversation.Branch,
                conversation.Metadata),
        };

        events.AddRange(conversation.Messages.Select<Message, SessionEvent>(message => message switch
        {
            UserMessage user => new UserMessageEvent(conversation.Id, 0, user.Timestamp, user),
            SystemMessage system => new SystemMessageEvent(conversation.Id, 0, system.Timestamp, system),
            AssistantMessage assistant => new AssistantMessageEvent(conversation.Id, 0, assistant.Timestamp, assistant),
            ToolResultMessage toolResult => new ToolResultEvent(conversation.Id, 0, toolResult.Timestamp, toolResult),
            AttachmentMessage attachment => new AttachmentMessageEvent(conversation.Id, 0, attachment.Timestamp, attachment),
            TombstoneMessage tombstone => new TombstoneMessageEvent(conversation.Id, 0, tombstone.Timestamp, tombstone),
            _ => throw new InvalidOperationException($"Unsupported conversation message: {message.GetType().Name}"),
        }));

        return events;
    }
}