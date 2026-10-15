using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Goals;
using OneCode.Core.Permissions;
using OneCode.Core.Tokens;
using System.Runtime.CompilerServices;

namespace OneCode.Infrastructure.Agent.RunMiddleware;

/// <summary>
/// Agent Run 级预算守卫中间件 — 在 MAF Agent Run 层执行 <b>pre-execution</b> 预算检查，
/// 当 <see cref="ITokenLedger"/> 累计可计费 token（<see cref="UsageMath.BillableTokens(long, long, long, long)"/> 口径）
/// 已达到或超过 <c>MaxBudgetTokens</c> 时短路返回错误响应，不发起 LLM 调用，从而防止失控循环继续消耗。
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
/// <b>分级策略</b>：70%/90% 时经 <c>onBudgetWarning</c> 回调发预警（按级别去重递升）；
/// 100% 时若提供 <c>onBudgetExceeded</c> 回调（交互模式）则询问用户三选一——
/// 追加预算继续（上限加倍放行本轮）/ 收尾总结（注入最后一个收尾 turn 后正常结束）/
/// 直接停止；未提供 handler（GOAL、cron 等无人值守路径）则硬停短路。
/// </para>
///
/// <para>
/// <b>短路行为</b>：当预算超支且无人接管时：
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
///   <item>UsageTracking（内层）：post-execution 记录 → 写入 ITokenLedger</item>
/// </list>
/// 当 BudgetGuard 放行后，UsageTracking 记录本次 run 的实际 usage；
/// 下一次 run 时 BudgetGuard 读取更新后的 <see cref="ITokenLedger.GetTotalTokens"/> 进行检查。
/// </para>
/// </summary>
public static class BudgetGuardRunMiddleware
{
    /// <summary>预警阈值（早级）：可计费消耗达到预算的 70%。</summary>
    internal const double EarlyWarningRatio = 0.7;

    /// <summary>预警阈值（晚级）：可计费消耗达到预算的 90%。</summary>
    internal const double LateWarningRatio = 0.9;

    /// <summary>
    /// 收尾总结注入指令（AllowAlways 决策）：预算耗尽后给模型的最后一个 turn，
    /// 总结已完成部分与剩余工作，不再继续展开新工作（Claude Code task budget 的优雅收尾模式）。
    /// </summary>
    internal const string WrapUpInstruction =
        "[Budget exhausted] The token budget for this run has been used up. Do NOT start new work or call more tools. " +
        "Summarize: (1) what has been completed so far, (2) what remains to be done, (3) any risks or follow-ups. " +
        "Keep it concise.";

    /// <summary>
    /// 创建 Agent Run 级预算守卫中间件的 (runFunc, runStreamingFunc) 委托对。
    /// 传给 <see cref="AIAgentBuilder.Use(System.Func{Microsoft.Agents.AI.AIAgent, Microsoft.Agents.AI.AIAgent})"/> 的 Run 中间件重载。
    /// </summary>
    /// <param name="tokenLedger">ITokenLedger 实例（null 时不执行预算检查）。</param>
    /// <param name="maxBudgetTokens">token 预算上限（可计费口径）。null 时不执行预算检查。</param>
    /// <param name="logger">日志器（可选）。</param>
    /// <param name="budgetBaselineTokens">
    /// 预算基线（run 启动时对 ledger 的快照）。检查条件为 <c>GetTotalTokens() - baseline >= limit</c>，
    /// 即预算只衡量本次 run 的增量消耗。默认 0（会话级累计口径，PLAN/GOAL 使用）；
    /// BUILD 模式传 run 启动快照，使 Plan 阶段调研消耗不占用计划执行的预算。
    /// </param>
    /// <param name="onBudgetExceeded">
    /// 预算 100% 消耗时的用户询问回调（交互模式）。返回 <see cref="ApprovalDecision.AllowOnce"/> =
    /// 追加预算继续（上限加倍放行本轮）；<see cref="ApprovalDecision.AllowAlways"/> = 收尾总结（注入收尾
    /// turn 后正常结束，后续 run 硬停）；<see cref="ApprovalDecision.Deny"/> = 直接停止。
    /// null（GOAL/cron 等无人值守路径）时硬停短路。
    /// </param>
    /// <param name="onBudgetWarning">70%/90% 预警回调（参数：级别、超出基线的已消耗量、预算上限）。</param>
    /// <returns>(runFunc, runStreamingFunc) 委托对。</returns>
    public static (
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>>,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>>
        ) Create(
            ITokenLedger? tokenLedger,
            long? maxBudgetTokens,
            ILogger? logger,
            long budgetBaselineTokens = 0,
            Func<long, long, CancellationToken, Task<ApprovalDecision>>? onBudgetExceeded = null,
            Action<GoalBudgetWarningLevel, long, long>? onBudgetWarning = null)
    {
        // 无 ITokenLedger 或无预算上限 → 不执行预算检查（测试/无预算场景）
        if (tokenLedger is null || maxBudgetTokens is null)
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

        var budgetLimit = maxBudgetTokens.Value;
        // 基线检查：只对超出基线的增量计预算。防御 baseline > current 的极端情况（外部重置账本）。
        var baseline = Math.Min(budgetBaselineTokens, tokenLedger.GetTotalTokens());

        // run 级可变状态：交互决策（加倍放行/收尾）只影响当前 pipeline 实例（即本次 run）
        var effectiveLimit = budgetLimit;
        var exhausted = false;
        GoalBudgetWarningLevel? lastWarningLevel = null;

        return (RunCore, RunStreamingCore);

        async Task<AgentResponse> RunCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, CancellationToken ct)
        {
            var currentTokens = tokenLedger.GetTotalTokens();
            var delta = currentTokens - baseline;

            // 收尾总结之后：后续 run 一律硬停（不再询问、不再调用 agent）
            if (exhausted)
                return ShortCircuit(delta);

            if (delta >= effectiveLimit)
            {
                if (onBudgetExceeded is not null)
                {
                    var decision = await onBudgetExceeded(delta, budgetLimit, ct).ConfigureAwait(false);
                    if (decision == ApprovalDecision.AllowOnce)
                    {
                        effectiveLimit = budgetLimit * 2;
                        logger?.LogWarning(
                            "BudgetGuard: user approved doubling the budget to {NewLimit:N0} for this run (delta {Delta:N0})",
                            effectiveLimit, delta);
                    }
                    else if (decision == ApprovalDecision.AllowAlways)
                    {
                        // 收尾总结：注入最后一个收尾 turn，之后本 pipeline 实例硬停
                        exhausted = true;
                        logger?.LogWarning(
                            "BudgetGuard: user requested graceful wrap-up — injecting final summary turn (delta {Delta:N0})",
                            delta);
                        var wrapMessages = messages.ToList();
                        wrapMessages.Add(new ChatMessage(ChatRole.User, WrapUpInstruction));
                        return await agent.RunAsync(wrapMessages, session, options, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        return ShortCircuit(delta);
                    }
                }
                else
                {
                    return ShortCircuit(delta);
                }
            }

            PublishWarning(delta);
            return await agent.RunAsync(messages, session, options, ct).ConfigureAwait(false);
        }

        async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, [EnumeratorCancellation] CancellationToken ct)
        {
            var currentTokens = tokenLedger.GetTotalTokens();
            var delta = currentTokens - baseline;

            // 收尾总结之后：后续 run 一律硬停（不再询问、不再调用 agent）
            if (exhausted)
            {
                yield return CreateBudgetExceededUpdate(
                    FormatBudgetExceededMessage(delta, budgetLimit));
                yield break;
            }

            if (delta >= effectiveLimit)
            {
                if (onBudgetExceeded is not null)
                {
                    var decision = await onBudgetExceeded(delta, budgetLimit, ct).ConfigureAwait(false);
                    if (decision == ApprovalDecision.AllowOnce)
                    {
                        effectiveLimit = budgetLimit * 2;
                        logger?.LogWarning(
                            "BudgetGuard: user approved doubling the budget to {NewLimit:N0} for this run (delta {Delta:N0})",
                            effectiveLimit, delta);
                    }
                    else if (decision == ApprovalDecision.AllowAlways)
                    {
                        exhausted = true;
                        logger?.LogWarning(
                            "BudgetGuard: user requested graceful wrap-up — injecting final summary turn (delta {Delta:N0})",
                            delta);
                        var wrapMessages = messages.ToList();
                        wrapMessages.Add(new ChatMessage(ChatRole.User, WrapUpInstruction));
                        await foreach (var update in agent.RunStreamingAsync(wrapMessages, session, options, ct)
                                .ConfigureAwait(false))
                        {
                            yield return update;
                        }
                        yield break;
                    }
                    else
                    {
                        yield return CreateBudgetExceededUpdate(
                            FormatBudgetExceededMessage(delta, budgetLimit));
                        yield break;
                    }
                }
                else
                {
                    yield return CreateBudgetExceededUpdate(
                        FormatBudgetExceededMessage(delta, budgetLimit));
                    yield break;
                }
            }

            PublishWarning(delta);
            await foreach (var update in agent.RunStreamingAsync(messages, session, options, ct).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        AgentResponse ShortCircuit(long delta)
        {
            logger?.LogWarning(
                "BudgetGuard: pre-execution budget exceeded — {Current:N0} >= {Limit:N0} billable tokens (baseline {Baseline:N0}), short-circuiting agent run",
                delta, budgetLimit, baseline);
            return CreateBudgetExceededResponse(FormatBudgetExceededMessage(delta, budgetLimit));
        }

        void PublishWarning(long delta)
        {
            if (onBudgetWarning is null || delta <= 0)
                return;
            var ratio = (double)delta / budgetLimit;
            var level = ratio >= LateWarningRatio ? GoalBudgetWarningLevel.Late
                : ratio >= EarlyWarningRatio ? GoalBudgetWarningLevel.Early
                : (GoalBudgetWarningLevel?)null;
            // 只递升去重：同一 run 内同级别只发一次，跨级（Early→Late）允许再发
            if (level is null || level <= lastWarningLevel)
                return;
            lastWarningLevel = level;
            onBudgetWarning(level.Value, delta, budgetLimit);
        }
    }

    /// <summary>
    /// 构造预算超支提示消息。
    /// </summary>
    internal static string FormatBudgetExceededMessage(long currentTokens, long budgetLimit)
        => $"[Budget Exceeded] Billable token usage {currentTokens:N0} (non-cached input + output + cache write + 10% cache read) has reached the budget limit {budgetLimit:N0}. Agent run was not executed to prevent runaway usage. Increase --max-budget-tokens or reset the session to continue.";

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
