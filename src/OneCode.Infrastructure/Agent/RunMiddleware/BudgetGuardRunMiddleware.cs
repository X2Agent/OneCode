using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Cost;
using System.Runtime.CompilerServices;

namespace OneCode.Infrastructure.Agent.RunMiddleware;

/// <summary>
/// Agent Run 级预算守卫中间件 — 在 MAF Agent Run 层执行 <b>pre-execution</b> 预算检查，
/// 当 <see cref="ICostTracker"/> 累计成本已达到或超过 <c>MaxBudgetUsd</c> 时短路返回错误响应，
/// 不发起 LLM 调用，从而防止超支后继续消费。
///
/// <para>
/// <b>三层中间件定位</b>（MAF 1.13 官方设计）：
/// <list type="bullet">
///   <item>Agent Run（本中间件）：pre-execution 预算门控 — 位于 <see cref="UsageTrackingRunMiddleware"/> 外层</item>
///   <item>Function Calling：工具调用层（权限/Hook/验证等）</item>
///   <item>IChatClient：模型推理层</item>
/// </list>
/// 本中间件注册为 Agent Run 最外层，确保在任何 LLM 调用前进行预算检查。
/// </para>
///
/// <para>
/// <b>短路行为</b>：当预算超支时：
/// <list type="bullet">
///   <item>非流式：返回包含预算超支提示文本的 <see cref="AgentResponse"/>，<c>Usage</c> 为 null</item>
///   <item>流式：yield 单个 <see cref="AgentResponseUpdate"/>（文本内容为预算超支提示），然后结束</item>
/// </list>
/// 调用方（如 <c>MainAgentRunner</c>）通过 <c>response.Text</c> 或 update 文本感知预算耗尽。
/// </para>
///
/// <para>
/// <b>与 UsageTrackingRunMiddleware 的协作</b>：
/// <list type="bullet">
///   <item>BudgetGuard（外层）：pre-execution 检查 → 短路或放行</item>
///   <item>UsageTracking（内层）：post-execution 记录 → 写入 ICostTracker</item>
/// </list>
/// 当 BudgetGuard 放行后，UsageTracking 记录本次 run 的实际 usage；
/// 下一次 run 时 BudgetGuard 读取更新后的 <see cref="ICostTracker.GetTotalCost"/> 进行检查。
/// </para>
/// </summary>
public static class BudgetGuardRunMiddleware
{
    /// <summary>
    /// 创建 Agent Run 级预算守卫中间件的 (runFunc, runStreamingFunc) 委托对。
    /// 传给 <see cref="AIAgentBuilder.Use(System.Func{Microsoft.Agents.AI.AIAgent, Microsoft.Agents.AI.AIAgent})"/> 的 Run 中间件重载。
    /// </summary>
    /// <param name="costTracker">ICostTracker 实例（null 时不执行预算检查）。</param>
    /// <param name="maxBudgetUsd">预算上限（USD）。null 时不执行预算检查。</param>
    /// <param name="logger">日志器（可选）。</param>
    /// <param name="modelId">当前模型 ID（可选，用于检测未定价模型）。</param>
    /// <returns>(runFunc, runStreamingFunc) 委托对。</returns>
    public static (
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>>,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>>
        ) Create(ICostTracker? costTracker, decimal? maxBudgetUsd, ILogger? logger, string? modelId = null)
    {
        // 无 ICostTracker 或无预算上限 → 不执行预算检查（测试/无预算场景）
        if (costTracker is null || maxBudgetUsd is null)
        {
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

        var budgetLimit = maxBudgetUsd.Value;
        var unpricedWarned = false;
        return (RunCore, RunStreamingCore);

        async Task<AgentResponse> RunCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, CancellationToken ct)
        {
            var currentCost = costTracker.GetTotalCost();
            if (currentCost >= budgetLimit)
            {
                var message = FormatBudgetExceededMessage(currentCost, budgetLimit);
                logger?.LogWarning(
                    "BudgetGuard: pre-execution budget exceeded — ${Current:F4} >= ${Limit:F4}, short-circuiting agent run",
                    currentCost, budgetLimit);
                return CreateBudgetExceededResponse(message);
            }

            WarnIfUnpriced(costTracker, modelId, budgetLimit, logger, ref unpricedWarned);

            return await agent.RunAsync(messages, session, options, ct).ConfigureAwait(false);
        }

        async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, [EnumeratorCancellation] CancellationToken ct)
        {
            var currentCost = costTracker.GetTotalCost();
            if (currentCost >= budgetLimit)
            {
                var message = FormatBudgetExceededMessage(currentCost, budgetLimit);
                logger?.LogWarning(
                    "BudgetGuard: pre-execution budget exceeded (streaming) — ${Current:F4} >= ${Limit:F4}, short-circuiting agent run",
                    currentCost, budgetLimit);
                yield return CreateBudgetExceededUpdate(message);
                yield break;
            }

            WarnIfUnpriced(costTracker, modelId, budgetLimit, logger, ref unpricedWarned);

            await foreach (var update in agent.RunStreamingAsync(messages, session, options, ct).ConfigureAwait(false))
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// 构造预算超支提示消息。
    /// </summary>
    internal static string FormatBudgetExceededMessage(decimal currentCost, decimal budgetLimit)
        => $"[Budget Exceeded] Cumulative cost ${currentCost:F4} has reached the budget limit ${budgetLimit:F4}. "
           + "Agent run was not executed to prevent overspending. "
           + "Increase --max-budget-usd or reset the session to continue.";

    /// <summary>
    /// 当模型未配置定价时发出一次性警告。
    /// 未定价模型的费用始终为零，BudgetGuard 的预算熔断对该模型静默失效，
    /// 需提醒用户通过 settings.json 或 ModelCatalog 配置定价。
    /// </summary>
    private static void WarnIfUnpriced(
        ICostTracker costTracker, string? modelId, decimal budgetLimit,
        ILogger? logger, ref bool warned)
    {
        if (warned || string.IsNullOrEmpty(modelId) || logger is null)
            return;

        if (!costTracker.HasPricing(modelId))
        {
            warned = true;
            logger.LogWarning(
                "BudgetGuard: model {ModelId} has no pricing configured — cost will be recorded as zero, " +
                "budget limit ${Limit:F4} cannot be enforced for this model. " +
                "Configure pricing via settings.json or ensure the model exists in ModelCatalog.",
                modelId, budgetLimit);
        }
    }

    /// <summary>
    /// 创建预算超支的短路 <see cref="AgentResponse"/>。
    /// 不设置 <see cref="AgentResponse.Usage"/>（无 LLM 调用，无 token 消耗）。
    /// </summary>
    internal static AgentResponse CreateBudgetExceededResponse(string message)
    {
        var response = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, message)],
        };
        return response;
    }

    /// <summary>
    /// 创建预算超支的短路 <see cref="AgentResponseUpdate"/>。
    /// </summary>
    internal static AgentResponseUpdate CreateBudgetExceededUpdate(string message)
    {
        var update = new AgentResponseUpdate();
        update.Contents.Add(new TextContent(message));
        return update;
    }
}
