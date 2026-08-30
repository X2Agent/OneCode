using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tokens;
using System.Runtime.CompilerServices;

namespace OneCode.Infrastructure.Agent.RunMiddleware;

/// <summary>
/// Agent Run 级预算守卫中间件 — 在 MAF Agent Run 层执行 <b>pre-execution</b> 预算检查，
/// 当 <see cref="ITokenLedger"/> 累计 token（输入 + 输出）已达到或超过 <c>MaxBudgetTokens</c> 时
/// 短路返回错误响应，不发起 LLM 调用，从而防止失控循环继续消耗。
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
///   <item>UsageTracking（内层）：post-execution 记录 → 写入 ITokenLedger</item>
/// </list>
/// 当 BudgetGuard 放行后，UsageTracking 记录本次 run 的实际 usage；
/// 下一次 run 时 BudgetGuard 读取更新后的 <see cref="ITokenLedger.GetTotalTokens"/> 进行检查。
/// </para>
/// </summary>
public static class BudgetGuardRunMiddleware
{
    /// <summary>
    /// 创建 Agent Run 级预算守卫中间件的 (runFunc, runStreamingFunc) 委托对。
    /// 传给 <see cref="AIAgentBuilder.Use(System.Func{Microsoft.Agents.AI.AIAgent, Microsoft.Agents.AI.AIAgent})"/> 的 Run 中间件重载。
    /// </summary>
    /// <param name="tokenLedger">ITokenLedger 实例（null 时不执行预算检查）。</param>
    /// <param name="maxBudgetTokens">token 预算上限（输入 + 输出）。null 时不执行预算检查。</param>
    /// <param name="logger">日志器（可选）。</param>
    /// <returns>(runFunc, runStreamingFunc) 委托对。</returns>
    public static (
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>>,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>>
        ) Create(ITokenLedger? tokenLedger, long? maxBudgetTokens, ILogger? logger)
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
        return (RunCore, RunStreamingCore);

        Task<AgentResponse> RunCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, CancellationToken ct)
        {
            var currentTokens = tokenLedger.GetTotalTokens();
            if (currentTokens >= budgetLimit)
            {
                var message = FormatBudgetExceededMessage(currentTokens, budgetLimit);
                logger?.LogWarning(
                    "BudgetGuard: pre-execution budget exceeded — {Current:N0} >= {Limit:N0} tokens, short-circuiting agent run",
                    currentTokens, budgetLimit);
                return Task.FromResult(CreateBudgetExceededResponse(message));
            }

            return agent.RunAsync(messages, session, options, ct);
        }

        async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCore(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            AIAgent agent, [EnumeratorCancellation] CancellationToken ct)
        {
            var currentTokens = tokenLedger.GetTotalTokens();
            if (currentTokens >= budgetLimit)
            {
                var message = FormatBudgetExceededMessage(currentTokens, budgetLimit);
                logger?.LogWarning(
                    "BudgetGuard: pre-execution budget exceeded (streaming) — {Current:N0} >= {Limit:N0} tokens, short-circuiting agent run",
                    currentTokens, budgetLimit);
                yield return CreateBudgetExceededUpdate(message);
                yield break;
            }

            await foreach (var update in agent.RunStreamingAsync(messages, session, options, ct).ConfigureAwait(false))
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// 构造预算超支提示消息。
    /// </summary>
    internal static string FormatBudgetExceededMessage(long currentTokens, long budgetLimit)
        => $"[Budget Exceeded] Cumulative token usage {currentTokens:N0} (input + output) has reached the budget limit {budgetLimit:N0}. "
           + "Agent run was not executed to prevent runaway usage. "
           + "Increase --max-budget-tokens or reset the session to continue.";

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
