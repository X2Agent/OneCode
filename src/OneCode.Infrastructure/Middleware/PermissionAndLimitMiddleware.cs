using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// 权限与工具上限中间件：Layer 1 保护。
///
/// 职责：
/// <list type="bullet">
///   <item>工具调用计数 + MaxToolCalls 上限（超限时返回当前调用错误结果）</item>
///   <item>IsToolAllowed 白名单过滤</item>
///   <item>权限检查（Allow/Deny/Ask/Passthrough 路由）</item>
///   <item>审批路由：Ask/Passthrough → MAF ToolApprovalAgent 或 inline ApprovalHandler</item>
/// </list>
/// </summary>
public static class PermissionAndLimitMiddleware
{
    /// <summary>创建 MAF 中间件委托。</summary>
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(AgentPipelineOptions options, AgentPipelineMetrics metrics)
    {
        // 预分配空集合，避免每次工具调用重复分配（options 字段为 null 时使用）
        var rulesBySource = options.RulesBySource ?? new Dictionary<string, PermissionRuleGroup>();
        var additionalWorkingDirectories = options.AdditionalWorkingDirectories
            ?? new Dictionary<string, AdditionalWorkingDirectory>();
        var sessionAllowlist = options.SessionAllowlist ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return async (_, ctx, next, ct) =>
        {
            // 白名单拒绝只失败当前调用。批次中的每个 call 都必须生成对应 result；
            // 是否停止下一轮模型请求由编排层在完整批次排空后决定。
            // 白名单拒绝不计入 MaxToolCalls（计数仅针对实际尝试执行的调用）。
            if (ctx.Function is not null
                && options.IsToolAllowed is not null
                && !options.IsToolAllowed(ctx.Function.Name))
            {
                return ToolResult.Error($"Tool '{ctx.Function.Name}' is not permitted in this agent.");
            }

            // 计数 + MaxToolCalls 检查推迟到权限通过后、实际执行前进行：
            // 被权限拒绝或审批拒绝的工具调用不计入 MaxToolCalls，避免计数偏差。
            async ValueTask<object> ExecuteWithLimitAsync(FunctionInvocationContext innerCtx, CancellationToken innerCt)
            {
                if (metrics.IncrementToolCallCount() > options.MaxToolCalls)
                    return ToolResult.Error(options.ToolLimitMessage);
                return await next(innerCtx, innerCt).ConfigureAwait(false) ?? "";
            }

            return await CheckPermissionAndExecuteAsync(
                options,
                rulesBySource,
                additionalWorkingDirectories,
                sessionAllowlist,
                ctx,
                ExecuteWithLimitAsync,
                ct).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// 权限检查 + 执行。
    /// 路由 Allow/Deny/Ask/Passthrough 决策到对应处理路径。
    /// </summary>
    private static async ValueTask<object> CheckPermissionAndExecuteAsync(
        AgentPipelineOptions options,
        IReadOnlyDictionary<string, PermissionRuleGroup> rulesBySource,
        IReadOnlyDictionary<string, AdditionalWorkingDirectory> additionalWorkingDirectories,
        HashSet<string> sessionAllowlist,
        FunctionInvocationContext ctx,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object>> next,
        CancellationToken ct)
    {
        if (options.PermissionChecker is null || ctx.Function is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var toolInput = ctx.Arguments is not null
            ? JsonSerializer.SerializeToElement(ctx.Arguments)
            : JsonSerializer.SerializeToElement(new { });

        var permContext = new ToolPermissionContext
        {
            Mode = options.PermissionMode,
            WorkingDirectory = options.WorkingDirectory,
            RulesBySource = rulesBySource,
            AdditionalWorkingDirectories = additionalWorkingDirectories,
            SessionAllowlist = sessionAllowlist,
        };

        var perm = await options.PermissionChecker.CheckAsync(
            ctx.Function.Name, toolInput, permContext, ct).ConfigureAwait(false);

        if (perm.Decision == PermissionDecision.Allow)
            return await next(ctx, ct).ConfigureAwait(false);

        if (perm.Decision == PermissionDecision.Deny)
        {
            return ToolResult.Error(
                $"Tool '{ctx.Function.Name}' denied: {perm.Message}",
                "Request user permission or modify the tool call.");
        }

        // Ask/Passthrough 处理：
        // - EnableToolApproval: true → 放行到 MAF ToolApprovalAgent（Main 路径事件驱动审批）
        // - EnableToolApproval: false → 使用 ApprovalHandler inline 处理（Team 路径）
        //   无 ApprovalHandler 时 fail-safe Deny
        if (perm.Decision is PermissionDecision.Ask or PermissionDecision.Passthrough)
        {
            var isMafApprovalFunction = ctx.Function is ApprovalRequiredAIFunction;
            if (options.ApprovalBroker is not null
                && (!options.EnableToolApproval || !isMafApprovalFunction))
            {
                var approval = await options.ApprovalBroker.RequestAsync(
                    new ApprovalRequest(
                        RequestId: Guid.NewGuid().ToString("N"),
                        ToolName: ctx.Function.Name,
                        ToolInput: toolInput.GetRawText()),
                    ct).ConfigureAwait(false);

                if (approval is ApprovalDecision.AllowOnce or ApprovalDecision.AllowAlways)
                    return await next(ctx, ct).ConfigureAwait(false);

                return ToolResult.Error(
                    $"Tool '{ctx.Function.Name}' denied by approval broker.",
                    "Request user permission or modify the tool call.");
            }

            if (options.EnableToolApproval)
            {
                // 放行到 ToolApprovalAgent
                return await next(ctx, ct).ConfigureAwait(false);
            }

            if (options.ApprovalHandler is not null)
            {
                var approved = await options.ApprovalHandler(
                    ctx.Function.Name, toolInput, ct).ConfigureAwait(false);
                if (approved)
                    return await next(ctx, ct).ConfigureAwait(false);
                return ToolResult.Error(
                    $"Tool '{ctx.Function.Name}' denied by user.",
                    "Request user permission or modify the tool call.");
            }

            // fail-safe Deny：无审批通道时仅返回当前调用的拒绝结果。
            return ToolResult.Error(
                $"Tool '{ctx.Function.Name}' requires approval but no approval channel is available (decision={perm.Decision}).",
                "Adjust permission rules to auto-allow this tool.");
        }

        // Fail-safe deny: any unrecognized PermissionDecision value (e.g. future enum additions)
        // must not fall through to execution. This prevents fail-open on enum expansion.
        return ToolResult.Error(
            $"Tool '{ctx.Function.Name}' denied: unrecognized permission decision '{perm.Decision}'.",
            "This is likely a framework bug — the PermissionDecision enum has an unhandled value.");
    }
}
