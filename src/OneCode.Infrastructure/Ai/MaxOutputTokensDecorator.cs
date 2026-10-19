using Microsoft.Extensions.AI;
using OneCode.Core.Models;

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
public sealed class MaxOutputTokensDecorator(IChatClient inner, int? contextWindow = null) : DelegatingChatClient(inner)
{
    private const int RecoveryLimit = 3;

    /// <summary>
    /// 默认与升级后的输出预算上限。推理模型可能把 4K/8K 级默认输出预算全部耗在 reasoning 上
    /// （finishReason=length 且零文本零工具调用），65536 是产出可见内容所需的量级；
    /// 当已知模型上下文窗口小于该值时自动按窗口上限钳制，防止把输出上限拉到超过模型总窗口。
    /// </summary>
    public const int EscalatedMaxTokens = 65_536;
    private const string RecoveryPrompt =
        """
        Output token limit hit. Resume directly — no apology, no recap. Continue from exactly where you left off.
        """;

    private int ResolveEscalationCap() =>
        contextWindow is > 0
            ? Math.Min(EscalatedMaxTokens, Math.Max(1, contextWindow.Value - 1))
            : EscalatedMaxTokens;

    /// <summary>
    /// 首轮默认输出上限：已知有限上下文窗口（如本地 Ollama <c>num_ctx</c>）时按窗口 1/4（上限 8192）保守起步，
    /// 避免首轮就把 <c>num_predict</c> 顶到 <c>contextWindow - 1</c> 挤压输入 KV cache 并使升级分支失效；
    /// 未知窗口（云端默认）保持 <see cref="EscalatedMaxTokens"/>。
    /// </summary>
    private int ResolveInitialDefaultCap() =>
        contextWindow is > 0
            ? Math.Min(ResolveEscalationCap(), ModelContextDefaults.ResolveOutputReservation(contextWindow.Value, 8_192))
            : EscalatedMaxTokens;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var cap = ResolveEscalationCap();
        var initialCap = ResolveInitialDefaultCap();
        var messageList = messages.ToList();
        var workingOptions = WithDefaultMaxOutputTokens(options, initialCap, cap);
        var escalated = workingOptions.MaxOutputTokens >= cap;
        var recoveryCount = 0;

        while (true)
        {
            var result = await base.GetResponseAsync(messageList, workingOptions, cancellationToken)
                .ConfigureAwait(false);

            if (result.FinishReason != ChatFinishReason.Length)
                return result;

            if (!escalated)
            {
                workingOptions.MaxOutputTokens = cap;
                escalated = true;
                continue;
            }

            if (recoveryCount >= RecoveryLimit)
                return result;

            var partialText = result.Text;
            if (!string.IsNullOrWhiteSpace(partialText))
                messageList.Add(new ChatMessage(ChatRole.Assistant, partialText));
            messageList.Add(new ChatMessage(ChatRole.User, RecoveryPrompt));
            if (workingOptions.MaxOutputTokens is null || workingOptions.MaxOutputTokens < cap)
                workingOptions.MaxOutputTokens = cap;
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
        return base.GetStreamingResponseAsync(
            messages,
            WithDefaultMaxOutputTokens(options, ResolveInitialDefaultCap(), ResolveEscalationCap()),
            cancellationToken);
    }

    /// <summary>
    /// 克隆调用方 <see cref="ChatOptions"/>：在未指定上限且未开启显式 Reasoning 时填入 <paramref name="defaultCap"/>；
    /// 若调用方显式指定的 <see cref="ChatOptions.MaxOutputTokens"/> 超过 <paramref name="maxCap"/>（如零输出重试硬编码 65536
    /// 落到小窗口模型），则钳制到 <paramref name="maxCap"/>。绝不原地修改调用方传入的实例。
    /// </summary>
    internal static ChatOptions WithDefaultMaxOutputTokens(
        ChatOptions? options,
        int defaultCap = EscalatedMaxTokens,
        int? maxCap = null)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        if (clone.MaxOutputTokens is null && clone.Reasoning?.Effort is null)
            clone.MaxOutputTokens = defaultCap;
        else if (maxCap is { } clamp && clone.MaxOutputTokens > clamp)
            clone.MaxOutputTokens = clamp;
        return clone;
    }
}
