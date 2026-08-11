using OneCode.Core.Domain;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Note: This middleware sits OUTSIDE ToolResultUnwrapMiddleware in the pipeline.
// Returning ToolResult.Error(...) would bypass serialization and cause IChatClient
// to emit a JSON wrapper object. Always return strings for guidance messages.

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// 状态机中间件：跟踪工具调用、管理错误恢复流程、注入 3-strike 修复指导。
///
/// 职责边界：
/// - 状态机负责错误恢复（Active → Recovering → Blocked）和工具调用追踪。
/// - 当 enableStrikeGuidance=true 时，在失败后注入递进式修复指导
///   （Strike 1→重试提示, Strike 2→聚焦修复, Strike 3→Blocked）。
/// - Plan 模式的只读约束由权限层（PlanModePermissionStrategy）负责，不在此重复。
/// </summary>
public static class StateMachineMiddleware
{
    private const int RecoveringThreshold = 2;
    private const int BlockedThreshold = 3;

    /// <summary>创建 MAF 中间件委托。</summary>
    /// <param name="logger">日志器（可选）。</param>
    /// <param name="enableStrikeGuidance">是否注入 3-strike 修复指导。Main 路径开启，Worker/Team 关闭。</param>
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(ILogger? logger = null, bool enableStrikeGuidance = false)
    {
        return async (_, ctx, next, ct) =>
        {
            if (ctx.Function is null)
                return await next(ctx, ct).ConfigureAwait(false);

            var toolName = ctx.Function.Name;

            var stateBag = AIAgent.CurrentRunContext?.Session?.StateBag;
            if (stateBag is null)
            {
                logger?.LogWarning("StateMachineMiddleware: StateBag unavailable, skipping state machine tracking");
                return await next(ctx, ct).ConfigureAwait(false);
            }

            if (ShouldBlockTool(stateBag.GetCurrentState(), toolName))
            {
                stateBag.GetOrInitializeToolExecutionContext().Guidance = GuidanceKind.TaskRecovery;
                return "[STATE MACHINE] Agent is blocked. Waiting for user intervention. Use AskUserQuestion or AskUserQuestions to request guidance.";
            }

            // User-question tools are the recovery escape hatch: they must execute while Blocked,
            // otherwise the TUI never receives the interactive question request that the
            // state-machine guidance asks the model to use.
            stateBag.ResetToolExecutionContext();

            var sw = Stopwatch.StartNew();
            var result = await next(ctx, ct).ConfigureAwait(false);
            sw.Stop();

            var execCtx = stateBag.GetOrInitializeToolExecutionContext();
            var isFailure = result switch
            {
                ToolResult tr => tr.IsError,
                _ => execCtx.IsError,
            };
            var isGuidance = execCtx.Guidance != GuidanceKind.None;
            var isSuccess = isGuidance || !isFailure;
            var isVerificationFailure = !isSuccess && execCtx.IsVerificationFailure;

            StateMachine.Transition(stateBag, toolName, isSuccess, isVerificationFailure);

            stateBag.GetOrInitializeRecentToolCalls().Add(new ToolCallRecord(
                toolName,
                ToolArgumentExtractor.ExtractFilePath(ctx.Arguments),
                isSuccess,
                DateTimeOffset.UtcNow,
                sw.Elapsed));

            if (toolName is "Bash" or "PowerShell")
                stateBag.ResetEditsSinceLastBuild();

            // 3-strike guidance injection (Main path only).
            // Skip for verification failures: the verification error itself is appended
            // to the tool result by VerificationMiddleware, providing specific repair
            // guidance. Adding strike noise on top is redundant. The IsVerificationFailure
            // flag already triggered Active→Recovering via StateMachine.Transition.
            if (enableStrikeGuidance && !isSuccess && !isVerificationFailure)
            {
                var strikes = stateBag.GetConsecutiveFailures(); // post-increment value
                if (strikes >= BlockedThreshold)
                {
                    stateBag.SetCurrentState(AgentState.Blocked);
                    stateBag.GetOrInitializeToolExecutionContext().Guidance = GuidanceKind.TaskRecovery;
                    logger?.LogWarning("State machine: {Strikes} strikes reached, agent blocked. Tool: {Tool}",
                        strikes, toolName);
                    return FormatGuidance(strikes, toolName,
                        "Agent entering Blocked state. Please review failures and intervene.",
                        result);
                }

                if (strikes >= RecoveringThreshold)
                {
                    logger?.LogWarning("State machine: Strike {Strike}/{Max}. Tool: {Tool}. Generating focused repair guidance.",
                        strikes, BlockedThreshold, toolName);
                    stateBag.GetOrInitializeToolExecutionContext().Guidance = GuidanceKind.TaskRecovery;
                    return FormatGuidance(strikes, toolName,
                        "Please carefully analyze the failure, re-read the target file, " +
                        "and try a different approach. Next failure will block the agent.",
                        result);
                }

                logger?.LogInformation("State machine: Strike {Strike}/{Max}. Tool: {Tool}. Injecting retry feedback.",
                    strikes, BlockedThreshold, toolName);
                stateBag.GetOrInitializeToolExecutionContext().Guidance = GuidanceKind.TaskRecovery;
                return FormatGuidance(strikes, toolName,
                    "Review the error and retry with adjustments.",
                    result);
            }

            return result;
        };
    }

    internal static bool IsUserInterventionTool(string? toolName)
        => string.Equals(toolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolName, "AskUserQuestions", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldBlockTool(AgentState state, string? toolName)
        => state == AgentState.Blocked && !IsUserInterventionTool(toolName);

    private static string FormatGuidance(int strikes, string toolName, string guidance, object? originalResult)
    {
        var originalText = originalResult switch
        {
            ToolResult tr => tr.Content ?? tr.ToString() ?? "",
            string s => s,
            _ => originalResult?.ToString() ?? "",
        };
        return $"[STATE MACHINE - Strike {strikes}/{BlockedThreshold}] " +
               $"Tool '{toolName}' failed. {guidance}\n\n" +
               $"--- Original error ---\n{originalText}";
    }
}

/// <summary>
/// 状态机纯函数：错误恢复转移逻辑，不包含工具闸门。
/// </summary>
public static class StateMachine
{
    /// <summary>根据工具执行结果推进状态转换。</summary>
    /// <param name="stateBag">MAF AgentSession StateBag，存储状态机所有共享状态。</param>
    /// <param name="toolName">Name of the tool that was executed.</param>
    /// <param name="isSuccess">Whether the tool execution succeeded.</param>
    /// <param name="isVerificationFailure">
    /// 验证失败（编译/类型检查）立即转 Recovering，不等 3-strike。
    /// 由 VerificationMiddleware 写入 ToolExecutionContext.IsVerificationFailure=true 触发。
    /// </param>
    public static void Transition(AgentSessionStateBag stateBag, string toolName, bool isSuccess,
        bool isVerificationFailure = false)
    {
        stateBag.IncrementTotalToolCalls();

        var currentState = stateBag.GetCurrentState();
        var consecutiveFailures = stateBag.GetConsecutiveFailures();

        var newState = currentState switch
        {
            // Real user answers are explicit intervention. Clear the recovery lock so the
            // agent can continue with the decisions supplied through either question tool.
            AgentState.Blocked when isSuccess && StateMachineMiddleware.IsUserInterventionTool(toolName)
                => AgentState.Active,

            AgentState.Active when isVerificationFailure
                => AgentState.Recovering,

            AgentState.Active when !isSuccess && consecutiveFailures >= 2
                => AgentState.Recovering,

            AgentState.Recovering when isSuccess
                => AgentState.Active,

            AgentState.Recovering when !isSuccess && consecutiveFailures >= 2
                => AgentState.Blocked,

            _ => currentState,
        };

        stateBag.SetCurrentState(newState);

        if (isSuccess)
            stateBag.ResetConsecutiveFailures();
        else
            stateBag.IncrementConsecutiveFailures();
    }
}
