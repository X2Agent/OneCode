using Microsoft.Extensions.AI;

namespace OneCode.App.Session;

/// <summary>
/// Maps domain transcript messages to <see cref="ChatMessage"/> for agent replay.
/// </summary>
/// <remarks>
/// 工具结果必须映射为 <see cref="ChatRole.Tool"/>（M.E.AI 标准约定，运行内
/// FunctionInvokingChatClient 亦然）：OpenAI 适配器对 User 角色消息里的
/// <see cref="FunctionResultContent"/> 静默丢弃（只有 Tool 角色才序列化为
/// <c>tool</c> wire 消息），按 User 角色回放会让恢复后的历史出现"assistant 带
/// tool_calls 但没有任何 tool 结果"的畸形结构；Anthropic / Ollama 适配器对
/// Tool 角色的结果均正确转换。空消息一律跳过，不进回放历史。
/// </remarks>
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

        CloseUnresolvedToolCalls(result, logger);
        return result;
    }

    /// <summary>
    /// 闭合回放历史中缺失 <see cref="FunctionResultContent"/> 的未闭合工具调用（例如上次运行中途中断、
    /// 或空 <see cref="ToolResultMessage"/> 被跳过）：为缺失的 <c>CallId</c> 补插合成 <c>[TOOL ERROR]</c>
    /// 结果消息，防止 MAF <c>ChatMessageGroups</c> 原子组无限延伸吞噬后续轮次，同时避免上游 API
    /// 因孤儿 <c>tool_calls</c> 返回 HTTP 400。
    /// </summary>
    private static void CloseUnresolvedToolCalls(List<ChatMessage> history, ILogger? logger)
    {
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role != ChatRole.Assistant)
                continue;

            var calls = history[i].Contents.OfType<FunctionCallContent>()
                .Where(static c => !string.IsNullOrEmpty(c.CallId))
                .ToList();
            if (calls.Count == 0)
                continue;

            var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
            var j = i + 1;
            while (j < history.Count && history[j].Role == ChatRole.Tool)
            {
                foreach (var res in history[j].Contents.OfType<FunctionResultContent>())
                {
                    if (!string.IsNullOrEmpty(res.CallId))
                        resolvedIds.Add(res.CallId);
                }
                j++;
            }

            foreach (var call in calls)
            {
                if (resolvedIds.Contains(call.CallId))
                    continue;

                logger?.LogWarning(
                    "Synthesizing error FunctionResultContent for unclosed tool call '{ToolName}' (callId={CallId}) in replayed history",
                    call.Name,
                    call.CallId);
                history.Insert(
                    j,
                    new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(
                            call.CallId,
                            $"[TOOL ERROR] Tool '{call.Name}' did not complete or its result was missing from transcript.")]));
                j++;
            }
        }
    }

    public static ChatMessage? TryMap(Message message, ILogger? logger = null) => message switch
    {
        UserMessage um when !um.IsMeta && !string.IsNullOrWhiteSpace(um.Content)
            => new ChatMessage(ChatRole.User, um.Content),
        AssistantMessage am when am.Content.Count > 0 => MapAssistantMessage(am, logger),
        SystemMessage sm when !string.IsNullOrWhiteSpace(sm.Content)
            => new ChatMessage(ChatRole.System, sm.Content),
        ToolResultMessage trm when !string.IsNullOrWhiteSpace(trm.Content)
            => new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(trm.ToolUseId, trm.Content)]),
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
            // "工具被调用但不知道传了什么"。必须留日志，不能静默吞掉。
            logger?.LogWarning(ex,
                "Tool argument deserialization failed for '{ToolName}' — arguments will be empty in replayed history",
                toolName);
            return null;
        }
    }
}
