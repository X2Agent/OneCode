using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Agent;

/// <summary>
/// <see cref="MainAgentRunner"/> 的 ChatOptions 与消息装配：
/// 输出上限、思考档位、工具列表与用户消息的统一构建入口。
/// </summary>
public partial class MainAgentRunner
{
    internal static ChatOptions BuildChatOptions(MainAgentRunOptions options)
    {
        // 开启扩展思考时不能再用低默认上限：Anthropic 要求 budget_tokens < max_tokens，
        // 显式设了 MaxOutputTokens 时适配器只能把 budget 压到 max_tokens - 1，输出预算会被吃光。
        // 留空则由适配器按 budget 自行抬高 max_tokens。
        var reasoningEffort = ResolveReasoningEffort(options);

        // 默认输出上限与 Length 升级恢复统一由底层客户端管道（MaxOutputTokensDecorator）处理；
        // 业务层仅透传调用方显式指定的 MaxOutputTokens，避免与扩展思考预算冲突或各处散落硬编码。
        var chatOptions = new ChatOptions
        {
            ModelId = options.ModelId,
            MaxOutputTokens = options.MaxOutputTokens,
            // 主体正文走 MAF 的 agent-instructions 入口，而不是自己拼一条 system 消息：
            // HarnessAgent 会把 harness 片段拼在它前面（片段缺失时用 MAF 默认文案）。
            Instructions = string.IsNullOrEmpty(options.SystemPrompt) ? null : options.SystemPrompt,
        };

        // 思考意图走 MEAI 标准 ChatOptions.Reasoning，由各 provider 适配器翻译成自家参数。
        // 不要往 AdditionalProperties 里塞 provider 私有 wire key：那不是任何 SDK 的契约。
        if (reasoningEffort is { } effort)
            chatOptions.Reasoning = new ReasoningOptions { Effort = effort };

        // Tool approval wrapping is centralized in AgentPipelineBuilder from
        // ToolMetadataRegistry. Keeping it there prevents Main/Worker policy drift.
        if (options.Tools is { Count: > 0 })
        {
            chatOptions.Tools = options.Tools.ToList();
            chatOptions.ToolMode = ChatToolMode.Auto;
        }

        return chatOptions;
    }

    /// <summary>
    /// 解析本轮的思考档位。未开启思考返回 <c>null</c>（不设置 <see cref="ChatOptions.Reasoning"/>，
    /// 让 provider 使用自身默认值）；显式 effort 字符串优先于 budget 推导。
    /// </summary>
    private static ReasoningEffort? ResolveReasoningEffort(MainAgentRunOptions options)
    {
        if (!options.EnableThinking)
            return null;

        if (!string.IsNullOrEmpty(options.ThinkingEffort))
            return ParseReasoningEffort(options.ThinkingEffort);

        if (options.ThinkingBudgetTokens > 0)
            return EffortThinking.ToReasoningEffort(options.ThinkingBudgetTokens);

        return null;
    }

    private static ReasoningEffort? ParseReasoningEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "none" or "disabled" or "off" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "max" or "extrahigh" => ReasoningEffort.ExtraHigh,
        _ => null,
    };

    internal static List<ChatMessage> BuildMessages(MainAgentRunOptions options)
    {
        List<ChatMessage> messages = [];

        // 正文已由 BuildChatOptions 交给 ChatOptions.Instructions，这里不能再加一条 system
        // 消息，否则同一段正文会既作指令又作对话消息重复送达模型。
        if (options.Messages is { Count: > 0 })
            messages.AddRange(options.Messages);

        // Prefer pre-built UserMessage (may contain multimodal content) over plain text.
        if (options.UserMessage is not null)
            messages.Add(options.UserMessage);
        else if (!string.IsNullOrEmpty(options.UserPrompt))
            messages.Add(new ChatMessage(ChatRole.User, options.UserPrompt));

        return messages;
    }
}
