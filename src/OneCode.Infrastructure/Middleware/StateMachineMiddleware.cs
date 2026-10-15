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
/// 状态机中间件：管理错误恢复流程（Active → Recovering → Blocked）。
///
/// 职责边界：
/// - 状态转移由 <see cref="StateMachine.Transition"/> 纯函数推进；
///   Blocked 后非干预类工具被闸门拦截，等待用户介入（见 <see cref="ShouldBlockTool"/>）。
/// - Plan 模式的只读约束由权限层（PlanModePermissionStrategy）负责，不在此重复。
/// - 工具调用时序观测不在此重复实现：框架侧有 span 埋点，但产品未注册 OpenTelemetry
///   订阅者（`src/` 内无 `AddSource` / `TracerProvider`），当前 span 全部为 `null`、不可观测。
/// </summary>
public static class StateMachineMiddleware
{
    /// <summary>创建 MAF 中间件委托。</summary>
    /// <param name="logger">日志器（可选）。</param>
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(ILogger? logger = null)
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
                return "[STATE MACHINE] Agent is blocked. Waiting for user intervention. Use AskUserQuestion to request guidance.";
            }

            // User-question tools are the recovery escape hatch: they must execute while Blocked,
            // otherwise the TUI never receives the interactive question request that the
            // state-machine guidance asks the model to use.
            stateBag.ResetToolExecutionContext();

            var result = await next(ctx, ct).ConfigureAwait(false);

            var execCtx = stateBag.GetOrInitializeToolExecutionContext();
            var isFailure = result switch
            {
                ToolResult tr => tr.IsError,
                _ => execCtx.IsError,
            };
            // Guidance 已注入（Recovery 等）视为成功，避免恢复引导本身再推高失败计数。
            var isGuidance = execCtx.Guidance != GuidanceKind.None;
            var isSuccess = isGuidance || !isFailure;
            var isVerificationFailure = !isSuccess && execCtx.IsVerificationFailure;

            StateMachine.Transition(stateBag, toolName, isSuccess, isVerificationFailure);

            if (toolName is "Bash")
                stateBag.ResetEditsSinceLastBuild();

            return result;
        };
    }

    internal static bool IsUserInterventionTool(string? toolName)
        => string.Equals(toolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldBlockTool(AgentState state, string? toolName)
        => state == AgentState.Blocked && !IsUserInterventionTool(toolName);
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
    /// 验证失败（编译/类型检查）立即转 Recovering，不等连续失败阈值。
    /// 由 EditGuardMiddleware 写入 ToolExecutionContext.IsVerificationFailure=true 触发。
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
