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
///   <item>权限检查（Allow/Deny/Ask 路由）</item>
///   <item>审批路由：Ask → MAF 审批协议（标记工具产生审批请求，绝不静默执行；无通道时 fail-safe Deny）</item>
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
                ctx,
                ExecuteWithLimitAsync,
                ct).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// 权限检查 + 执行。
    /// 路由 Allow/Deny/Ask 决策到对应处理路径。
    /// </summary>
    private static async ValueTask<object> CheckPermissionAndExecuteAsync(
        AgentPipelineOptions options,
        IReadOnlyDictionary<string, PermissionRuleGroup> rulesBySource,
        IReadOnlyDictionary<string, AdditionalWorkingDirectory> additionalWorkingDirectories,
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

        // Ask 处理（单通道）：唯一通道是 MAF 的审批协议。放行到 next 后，
        // FunctionInvokingChatClient 遇到带 ApprovalRequiredAIFunction 标记的工具会产生
        // ToolApprovalRequestContent 而不是执行；由 ToolApprovalAgent 的自动规则决定放行，
        // 否则交给上层桥（Main 流式审批拆分 / Team 工作流审批桥）呈现给用户。
        // 中间件自身从不询问用户，也不持有 broker——那会形成第二套审批通道。
        // 路径未启用审批时不存在可应答的通道，fail-safe Deny。
        if (perm.Decision == PermissionDecision.Ask)
        {
            if (options.EnableToolApproval)
                return await next(ctx, ct).ConfigureAwait(false);

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
