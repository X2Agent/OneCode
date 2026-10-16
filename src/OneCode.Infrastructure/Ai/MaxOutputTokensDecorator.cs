using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：在客户端管道层统一管理默认输出上限（<see cref="EscalatedMaxTokens"/>）
/// 以及收到 <c>stop_reason=max_tokens</c>（<see cref="ChatFinishReason.Length"/>）时的升级与多轮续跑恢复。
/// </summary>
/// <remarks>
/// <para>
/// 默认上限规则：当调用方未显式指定 <see cref="ChatOptions.MaxOutputTokens"/> 且未开启
/// <see cref="ChatOptions.Reasoning"/> 时，统一注入 <see cref="EscalatedMaxTokens"/>，
/// 避免推理模型（如 step-5-preview / gpt-5 / o3）在隐性思维链上耗尽服务端默认的 4K/8K 输出配额。
/// 当开启 <see cref="ChatOptions.Reasoning"/> 且未显式指定上限时，保持 <c>null</c>，
/// 交由 Anthropic 等适配器按 <c>budget_tokens</c> 自动抬高 <c>max_tokens</c>，避免预算被反向压扁。
/// </para>
/// <para>
/// 恢复机制依赖 M.E.AI 标准 <see cref="ChatFinishReason.Length"/>，与具体提供商无关，
/// 在 <see cref="ChatClientFactory.CreateWithDecorators"/> 中统一挂载，使主 Agent、子代理、
/// GoalDecomposer 与 GoalSubGoalJudge 共享同一保护，业务层无需硬编码输出上限。
/// </para>
/// </remarks>
public sealed class MaxOutputTokensDecorator(IChatClient inner) : DelegatingChatClient(inner)
{
    private const int RecoveryLimit = 3;

    /// <summary>
    /// 默认与升级后的输出预算上限。推理模型可能把 4K/8K 级默认输出预算全部耗在 reasoning 上
    /// （finishReason=length 且零文本零工具调用），65536 是产出可见内容所需的量级。
    /// </summary>
    public const int EscalatedMaxTokens = 65_536;
    private const string RecoveryPrompt =
        """
        Output token limit hit. Resume directly — no apology, no recap. Continue from exactly where you left off.
        """;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var workingOptions = WithDefaultMaxOutputTokens(options);
        var escalated = workingOptions.MaxOutputTokens >= EscalatedMaxTokens;
        var recoveryCount = 0;

        while (true)
        {
            var result = await base.GetResponseAsync(messageList, workingOptions, cancellationToken)
                .ConfigureAwait(false);

            if (result.FinishReason != ChatFinishReason.Length)
                return result;

            if (!escalated)
            {
                workingOptions.MaxOutputTokens = EscalatedMaxTokens;
                escalated = true;
                continue;
            }

            if (recoveryCount >= RecoveryLimit)
                return result;

            var partialText = result.Text;
            if (!string.IsNullOrWhiteSpace(partialText))
                messageList.Add(new ChatMessage(ChatRole.Assistant, partialText));
            messageList.Add(new ChatMessage(ChatRole.User, RecoveryPrompt));
            workingOptions.MaxOutputTokens = EscalatedMaxTokens;
            recoveryCount++;
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 流式路径同样注入默认输出上限，但不在此处做中途 continuation（流式响应消费完毕后才暴露 FinishReason，
        // 零输出 Length 重试由 MainAgentRunner 在流结束后处理）。
        return base.GetStreamingResponseAsync(messages, WithDefaultMaxOutputTokens(options), cancellationToken);
    }

    /// <summary>
    /// 克隆调用方 <see cref="ChatOptions"/> 并在未指定上限且未开启显式 Reasoning 时填入默认输出上限，
    /// 绝不原地修改调用方传入的实例。
    /// </summary>
    internal static ChatOptions WithDefaultMaxOutputTokens(ChatOptions? options)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        if (clone.MaxOutputTokens is null && clone.Reasoning?.Effort is null)
            clone.MaxOutputTokens = EscalatedMaxTokens;
        return clone;
    }
}
