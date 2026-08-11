using Microsoft.Extensions.AI;

namespace OneCode.App.Session;

/// <summary>
/// Maps domain transcript messages to <see cref="ChatMessage"/> for agent replay.
/// </summary>
internal static class ConversationMessageMapper
{
    public static IReadOnlyList<ChatMessage> ToChatHistory(IEnumerable<Message> messages, ILogger? logger = null)
    {
        List<ChatMessage> result = [];
        foreach (var message in messages)
        {
            var mapped = TryMap(message, logger);
            if (mapped is not null)
                result.Add(mapped);
        }

        return result;
    }

    public static ChatMessage? TryMap(Message message, ILogger? logger = null) => message switch
    {
        UserMessage um when !um.IsMeta && !string.IsNullOrWhiteSpace(um.Content)
            => new ChatMessage(ChatRole.User, um.Content),
        AssistantMessage am => MapAssistantMessage(am, logger),
        SystemMessage sm when !string.IsNullOrWhiteSpace(sm.Content)
            => new ChatMessage(ChatRole.System, sm.Content),
        ToolResultMessage trm when !string.IsNullOrWhiteSpace(trm.Content)
            => new ChatMessage(
                ChatRole.User,
                [new FunctionResultContent(trm.ToolUseId, trm.Content)]),
        AttachmentMessage attachment when !string.IsNullOrWhiteSpace(attachment.Content)
            => new ChatMessage(ChatRole.User, attachment.Content),
        _ => null,
    };

    private static ChatMessage MapAssistantMessage(AssistantMessage message, ILogger? logger)
    {
        List<AIContent> contents = [];
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when !string.IsNullOrEmpty(text.Text):
                    contents.Add(new TextContent(text.Text));
                    break;
                case ThinkingBlock thinking when !string.IsNullOrEmpty(thinking.Thinking):
                    contents.Add(new TextReasoningContent(thinking.Thinking));
                    break;
                case ToolUseBlock toolUse:
                    contents.Add(new FunctionCallContent(
                        toolUse.Id,
                        toolUse.Name,
                        ParseToolArguments(toolUse.Input, toolUse.Name, logger)));
                    break;
            }
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static IDictionary<string, object?>? ParseToolArguments(string input, string toolName, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(input);
        }
        catch (JsonException ex)
        {
            // 工具参数 JSON 损坏 → FunctionCallContent 将无参数回放，agent 会看到
            // "工具被调用但不知道传了什么"。必须留日志，不能静默吞掉（AGENTS.md §5.1）。
            logger?.LogWarning(ex,
                "Tool argument deserialization failed for '{ToolName}' — arguments will be empty in replayed history",
                toolName);
            return null;
        }
    }
}
