using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Cost;
using OneCode.Core.Domain;
using System.Runtime.CompilerServices;

namespace OneCode.Infrastructure.Agent.RunMiddleware;

/// <summary>
/// Agent Run 级 Usage 追踪中间件 — 在 MAF Agent Run 层统一拦截 LLM 返回的
/// <see cref="UsageDetails"/>，写入 <see cref="ICostTracker"/>。
///
/// <para>
/// <b>三层中间件定位</b>（MAF 1.13 官方设计）：
/// <list type="bullet">
///   <item>Agent Run（本中间件）：包裹整个 agent run，拦截最终 response/streaming updates</item>
///   <item>Function Calling：包裹工具调用（权限/Hook/验证等）</item>
///   <item>IChatClient：包裹模型推理</item>
/// </list>
/// 本中间件位于 Agent Run 最外层，确保所有 agent run（无论是否触发工具调用）
/// 的 usage 都被记录。
/// </para>
///
/// <para>
/// <b>Token 维度</b>（<see cref="UsageDetails"/> 契约，两个 provider 一致）：
/// <list type="bullet">
///   <item><c>InputTokenCount</c> → InputTokens（完整输入，<b>已含</b>缓存命中部分。MEAI 契约：
///     "Cached input tokens should be counted as part of InputTokenCount"）</item>
///   <item><c>OutputTokenCount</c> → OutputTokens（含 ReasoningTokens，见 MEAI 契约）</item>
///   <item><c>CachedInputTokenCount</c> → CacheReadTokens（其中缓存命中的子集，非额外部分）</item>
///   <item><c>AdditionalCounts["cache_creation_input_tokens"]</c> → CacheWriteTokens（Anthropic 创生）</item>
///   <item><c>ReasoningTokenCount</c> → ReasoningTokens（思考 token，是 OutputTokens 的子集）</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Streaming 去重策略</b>：MAF 可能在 turn 边界重放 <see cref="UsageContent"/>
/// （与 <see cref="FunctionCallContent"/> 行为一致）。本中间件取最后一个有效的
/// <see cref="UsageContent"/>（与 <c>ChatService.TryExtractUsage</c> 行为一致），
/// 避免重复累加。MAF 在 streaming 中的 <see cref="UsageContent"/> 通常包含
/// 当前 agent run 的累积 usage。
/// </para>
///
/// <para>
/// <b>异常路径 Usage 保留</b>：模型已消耗 token 但流式传输因异常
/// （网络中断、超时、5xx 等）未正常结束时，已收到的 <see cref="UsageContent"/>
/// 仍需写入 <see cref="ICostTracker"/>，否则 <c>--max-budget-usd</c> 熔断会因
/// 累计成本偏低而失效。流式路径使用 <c>try/finally</c> 确保异常时也执行
/// <see cref="RecordUsage"/>；非流式路径因 <c>agent.RunAsync</c> 抛异常时
/// <see cref="AgentResponse"/> 对象不可得（usage 封装在 response 内），无法
/// 在本层提取——该场景需下沉到 <c>IChatClient</c> 层从 HTTP 响应头提取 usage。
/// </para>
/// </summary>
public static class UsageTrackingRunMiddleware
{
    /// <summary>
    /// 创建 Agent Run 级中间件的 (runFunc, runStreamingFunc) 委托对。
    /// 传给 <see cref="AIAgentBuilder.Use"/> 的 Run 中间件重载。
    /// </summary>
    /// <param name="costTracker">ICostTracker 实例（null 时不拦截 usage）。</param>
    /// <param name="modelId">当前模型 ID（用于定价查找）。</param>
    /// <param name="logger">日志器（可选）。</param>
    /// <returns>(runFunc, runStreamingFunc) 委托对。</returns>
    public static (
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>>,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>>
        ) Create(ICostTracker? costTracker, string? modelId, ILogger? logger, SessionId? sessionId = null)
    {
        if (costTracker is null)
        {
            // No ICostTracker — pass through without interception (测试/无预算场景)
            return (PassThroughRun, PassThroughRunStreaming);

            static Task<AgentResponse> PassThroughRun(
                IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
                AIAgent agent, CancellationToken ct)
                => agent.RunAsync(messages, session, options, ct);

            static IAsyncEnumerable<AgentResponseUpdate> PassThroughRunStreaming(
                IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
                AIAgent agent, CancellationToken ct)
                => agent.RunStreamingAsync(messages, session, options, ct);
        }

        return (RunCore, RunStreamingCore);

        // 非流式路径：RunAsync 抛异常时 AgentResponse 对象不可得（usage 封装在
        // response 内），本层无法提取已消耗的 token。该场景需下沉到 IChatClient
        // 层从 HTTP 响应头提取 usage。此处仅在正常返回后记录。
        async Task<AgentResponse> RunCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, CancellationToken ct)
        {
            var response = await agent.RunAsync(messages, session, options, ct).ConfigureAwait(false);
            RecordUsage(response.Usage, costTracker, modelId, logger, sessionId);
            return response;
        }

        async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, [EnumeratorCancellation] CancellationToken ct)
        {
            // 取最后一个有效的 UsageContent（与 ChatService.TryExtractUsage 行为一致）。
            // MAF streaming 中的 UsageContent 通常包含当前 agent run 的累积 usage。
            UsageDetails? lastUsage = null;

            // try/finally 确保流式传输因异常中断时，已收到的 usage 仍被记录。
            // 否则 BudgetGuard 的 pre-execution 检查会因累计成本偏低而无法及时熔断。
            try
            {
                await foreach (var update in agent.RunStreamingAsync(messages, session, options, ct).ConfigureAwait(false))
                {
                    var usageContent = update.Contents?.OfType<UsageContent>().FirstOrDefault();
                    if (usageContent?.Details is { } details && HasValidTokens(details))
                        lastUsage = details;

                    yield return update;
                }
            }
            finally
            {
                // 无论正常结束还是异常，只要收到过有效 usage 就记录。
                // 异常会在此 finally 执行后继续向上传播（yield return 语义保证）。
                if (lastUsage is not null)
                    RecordUsage(lastUsage, costTracker, modelId, logger, sessionId);
            }
        }
    }

    /// <summary>
    /// 将 <see cref="UsageDetails"/> 写入 <see cref="ICostTracker"/>。
    /// 提取完整 token 维度：Input, Output, CacheRead, CacheWrite, Reasoning。
    /// </summary>
    internal static void RecordUsage(UsageDetails? details, ICostTracker costTracker, string? modelId, ILogger? logger, SessionId? sessionId = null)
    {
        if (details is null) return;
        if (!HasValidTokens(details)) return;

        var record = BuildUsageRecord(details, modelId);
        try
        {
            if (sessionId is { } sid)
                costTracker.RecordUsage(sid, record);
            else
                costTracker.RecordUsage(record);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to record usage to ICostTracker for model {ModelId}", modelId);
        }
    }

    private static bool HasValidTokens(UsageDetails details)
    {
        var input = SafeInt(details.InputTokenCount);
        var output = SafeInt(details.OutputTokenCount);
        return input > 0 || output > 0;
    }

    /// <summary>
    /// 从 <see cref="UsageDetails"/> 构建 <see cref="UsageRecord"/>。
    /// CacheWrite 从 AdditionalCounts 提取（Anthropic 的 cache_creation_input_tokens）。
    /// </summary>
    internal static UsageRecord BuildUsageRecord(UsageDetails details, string? modelId)
    {
        var input = SafeInt(details.InputTokenCount);
        var output = SafeInt(details.OutputTokenCount);
        var cacheRead = SafeInt(details.CachedInputTokenCount);
        var cacheWrite = ExtractAdditionalCount(details,
            "cache_creation_input_tokens",
            "cache_creation",
            "cacheWriteInputTokens",
            "cache_write_input_tokens");
        var reasoning = SafeInt(details.ReasoningTokenCount);

        return new UsageRecord(
            ModelId: modelId ?? "unknown",
            InputTokens: input,
            OutputTokens: output,
            CacheReadTokens: cacheRead,
            CacheWriteTokens: cacheWrite,
            ReasoningTokens: reasoning,
            // ContextTokens = 完整输入 token 数（InputTokens 已含 CacheReadTokens 子集，
            // 按 MEAI 契约不能再相加，否则会重复计算 cache_read 部分）
            ContextTokens: input);
    }

    /// <summary>
    /// 从 <see cref="UsageDetails.AdditionalCounts"/> 中提取厂商特定的 token 计数。
    /// </summary>
    private static int ExtractAdditionalCount(UsageDetails details, params string[] keys)
    {
        if (details.AdditionalCounts is null || keys.Length == 0)
            return 0;

        foreach (var key in keys)
        {
            if (details.AdditionalCounts.TryGetValue(key, out var value))
                return SafeInt(value);
        }

        return 0;
    }

    private static int SafeInt(long? value) =>
        value is null or 0 ? 0 : value > int.MaxValue ? int.MaxValue : (int)value.Value;
}
