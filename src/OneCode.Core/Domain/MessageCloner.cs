namespace OneCode.Core.Domain;

public static class MessageCloner
{
    public static Message CloneMessage(Message message) => message switch
    {
        UserMessage user => user with
        {
            Attachments = user.Attachments?.Select(a => a with { }).ToList(),
        },
        AssistantMessage assistant => assistant with
        {
            Content = assistant.Content.Select(CloneContentBlock).ToList(),
        },
        SystemMessage system => system with { },
        ToolResultMessage tool => tool with { },
        AttachmentMessage attachment => attachment with { },
        TombstoneMessage tombstone => tombstone with { },
        _ => throw new NotSupportedException($"Unsupported message type: {message.GetType().Name}"),
    };

    public static ContentBlock CloneContentBlock(ContentBlock block) => block switch
    {
        TextBlock text => text with { },
        ToolUseBlock toolUse => toolUse with { },
        ThinkingBlock thinking => thinking with { },
        RedactedThinkingBlock redacted => redacted with { },
        _ => throw new NotSupportedException($"Unsupported content block type: {block.GetType().Name}"),
    };
}
