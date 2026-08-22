using OneCode.Core.Domain;
using OneCode.Core.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// 安全不变量中间件：管道最前注册，Layer 0 保护。
/// 即使 BypassPermissions 模式也必须执行检查。
/// </summary>
public static class SafetyInvariantMiddleware
{
    /// <summary>创建 MAF 中间件委托。</summary>
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(IReadOnlyList<ISafetyInvariant> invariants, ILogger logger)
    {
        return async (_, ctx, next, ct) =>
        {
            if (ctx.Function is null)
                return await next(ctx, ct).ConfigureAwait(false);

            var parameters = ToolArgumentExtractor.ToParameterDictionary(ctx.Arguments, ctx.Function.Name, logger);
            if (parameters is null)
            {
                // 参数解析失败时 fail-closed，但只失败当前调用以保留批次完整性。
                return ToolResult.Error(
                    $"[SAFETY] Parameter extraction failed for tool '{ctx.Function.Name}' — blocked to prevent fail-open.");
            }

            foreach (var invariant in invariants)
            {
                var result = await invariant.CheckAsync(ctx.Function.Name, parameters, ct)
                    .ConfigureAwait(false);

                if (!result.Allowed)
                    return ToolResult.Error(result.Reason ?? "[SAFETY] Operation blocked by safety invariant.");
            }

            try
            {
                return await next(ctx, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // AIFunctionFactory 在参数绑定（marshalling）阶段抛出 ArgumentException
                // （如缺少必需参数、参数类型不匹配）。本中间件是管线最外层，此处兜底把
                // 错误作为 ToolResult 返回给 LLM 自愈重试，而不是让异常穿透中间件链
                // 导致整个 agent run 崩溃。
                logger.LogWarning(
                    "Tool '{ToolName}' argument binding failed: {Message}",
                    ctx.Function.Name,
                    ex.Message);
                return ToolResult.Error(
                    $"[TOOL ERROR] Tool '{ctx.Function.Name}' was called with invalid arguments: {ex.Message} " +
                    "Check the tool's required parameters and retry with correct arguments.");
            }
        };
    }

}
