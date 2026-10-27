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
///   Blocked 后非干预类工具被闸门拦截，等待用户介入（见 <see cref="ShouldBlockTool(AgentState, string?)"/>）。
/// - Plan 模式的只读约束由权限层（PlanModePermissionStrategy）负责，不在此重复。
/// - 工具调用时序观测不在此重复实现：框架侧有 span 埋点，宿主经
///   <see cref="OneCode.Infrastructure.Agent.AgentTelemetryLogForwarder"/>（ActivityListener）把 span 停止事件转发进本地日志。
/// </summary>
public static class StateMachineMiddleware
{
    /// <summary>同一工具连续失败触发定向阻拦的阈值。</summary>
    public const int SameToolBlockThreshold = 3;

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

            // User-question and plan-submission tools are recovery escape hatches: they must execute
            // while Blocked or during single-tool failure recovery so the agent can request user
            // guidance or deliver its final plan without deadlocking the session.
            stateBag.ResetToolExecutionContext();

            if (ShouldBlockTool(stateBag, toolName, out var blockReason))
            {
                var blockedCtx = stateBag.GetOrInitializeToolExecutionContext();
                blockedCtx.Guidance = GuidanceKind.TaskRecovery;
                blockedCtx.IsError = true;
                return blockReason;
            }

            var result = await next(ctx, ct).ConfigureAwait(false);

            var execCtx = stateBag.GetOrInitializeToolExecutionContext();
            var isFailure = result switch
            {
                ToolResult tr => tr.IsError,
                _ => execCtx.IsError,
            };
            // Guidance 已注入（Recovery / NextSteps 等）视为成功，避免恢复或配额引导再推高失败计数。
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
        => string.Equals(toolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, "SubmitPlan", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldBlockTool(AgentState state, string? toolName)
        => state == AgentState.Blocked && !IsUserInterventionTool(toolName);

    /// <summary>
    /// 结合会话状态袋判断当前工具调用是否应被阻拦：
    /// 1. 全局 Blocked 状态：阻拦除 AskUserQuestion / SubmitPlan 外的所有工具；
    /// 2. 单工具连续失败达阈值（>= 3）：仅定向阻拦该故障工具本身，放行其他工具以支持跨工具自愈。
    /// </summary>
    internal static bool ShouldBlockTool(
        AgentSessionStateBag stateBag,
        string? toolName,
        out string blockReason)
    {
        if (IsUserInterventionTool(toolName))
        {
            blockReason = string.Empty;
            return false;
        }

        if (ShouldBlockTool(stateBag.GetCurrentState(), toolName))
        {
            blockReason = "[STATE MACHINE] Agent is blocked. Waiting for user intervention. Use AskUserQuestion to request guidance.";
            return true;
        }

        var sameToolFailures = stateBag.GetSameToolConsecutiveFailures();
        var lastFailedTool = stateBag.GetLastFailedToolName();
        if (sameToolFailures >= SameToolBlockThreshold
            && !string.IsNullOrEmpty(toolName)
            && string.Equals(lastFailedTool, toolName, StringComparison.OrdinalIgnoreCase))
        {
            blockReason =
                $"[STATE MACHINE] Tool '{toolName}' failed {sameToolFailures} times consecutively and is temporarily blocked to prevent a retry loop. " +
                "Switch to an alternative tool, inspect context first, or use AskUserQuestion.";
            return true;
        }

        blockReason = string.Empty;
        return false;
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
    /// 验证失败（编译/类型检查）立即转 Recovering，不等连续失败阈值。
    /// 由 EditGuardMiddleware 写入 ToolExecutionContext.IsVerificationFailure=true 触发。
    /// </param>
    public static void Transition(AgentSessionStateBag stateBag, string toolName, bool isSuccess,
        bool isVerificationFailure = false)
    {
        stateBag.IncrementTotalToolCalls();

        var currentState = stateBag.GetCurrentState();
        var consecutiveFailures = stateBag.GetConsecutiveFailures();
        var sameToolFailures = isSuccess ? 0 : stateBag.RecordToolFailure(toolName);

        var newState = currentState switch
        {
            // Real user answers or plan delivery are explicit terminal/recovery actions.
            // Clear the recovery lock so the agent can continue or finalize cleanly.
            AgentState.Blocked when isSuccess && StateMachineMiddleware.IsUserInterventionTool(toolName)
                => AgentState.Active,

            AgentState.Active when isVerificationFailure
                => AgentState.Recovering,

            AgentState.Active when !isSuccess && consecutiveFailures >= 2
                => AgentState.Recovering,

            AgentState.Recovering when isSuccess
                => AgentState.Active,

            // 单一工具重复失败仅在 Recovering 下触发该工具定向阻拦（不封锁全局其他工具）；
            // 当验证失败持续发生、或切换到其他工具后仍连续失败时，才升级为全局 Blocked。
            AgentState.Recovering when !isSuccess
                && (isVerificationFailure || sameToolFailures < StateMachineMiddleware.SameToolBlockThreshold)
                && consecutiveFailures >= 2
                => AgentState.Blocked,

            _ => currentState,
        };

        stateBag.SetCurrentState(newState);

        if (isSuccess)
        {
            if (newState == AgentState.Active)
                stateBag.ResetToolFailureTracking();
        }
        else
        {
            stateBag.IncrementConsecutiveFailures();
        }
    }
}
