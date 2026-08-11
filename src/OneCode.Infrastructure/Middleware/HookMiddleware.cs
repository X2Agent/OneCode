using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// Hook 中间件：在工具执行前后触发 Pre/PostToolUse Hook。
///
/// Pre-hook 返回 BlockingErrors 时阻止工具执行（返回 ToolResult.Error）。
/// Post-hook 在工具执行完成后触发，不消费 result（仅做通知/审计）。
///
/// 异常处理策略：
/// 1. Pre-hook 异常 — fail-closed。异常冒泡到 MAF runtime 会导致整轮 agent run 失败，
///    转为 ToolResult.Error 限制为单次工具调用失败，同时保留批次完整性。
/// 2. Post-hook 异常 — fail-soft。Post-hook 是通知/审计语义，失败不应丢弃已成功的工具结果。
///    try-catch 包裹仅记日志，保留原 result 返回。
/// 3. hookResult null 检查 — 防御 FireAsync 返回 null 的实现。
/// 4. JsonDocument 资源管理 — 用 JsonSerializer.SerializeToElement 替代 JsonDocument.Parse
///    避免 using 释放问题。
/// 5. OperationCanceledException 透传 — catch 块显式排除 OCE 保留取消传播。
/// </summary>
public static class HookMiddleware
{
    /// <summary>创建 MAF 中间件委托。</summary>
    public static Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        Create(AgentPipelineOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return async (_, ctx, next, ct) =>
        {
            if (options.HookExecutionService is not null && ctx.Function is not null)
            {
                var prePayload = new HookPayload
                {
                    Event = HookEvent.PreToolUse,
                    ToolName = ctx.Function.Name,
                    ToolInput = ctx.Arguments is not null
                        ? JsonSerializer.SerializeToElement(ctx.Arguments)
                        : JsonSerializer.SerializeToElement(new { }),
                };

                AggregatedHookResult? hookResult;
                try
                {
                    hookResult = await options.HookExecutionService.FireAsync(
                        prePayload,
                        actualMatcherValue: ctx.Function.Name,
                        ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // OCE 必须透传，保留取消信号
                    throw;
                }
                catch (Exception ex)
                {
                    // Pre-hook 抛异常 → fail-closed，但只失败当前调用。
                    logger.LogError(ex, "PreToolUse hook threw for tool {ToolName}", ctx.Function.Name);
                    return ToolResult.Error(
                        $"Tool '{ctx.Function.Name}' blocked: pre-hook execution failed: {ex.Message}");
                }

                if (hookResult?.BlockingErrors is { Count: > 0 })
                {
                    return ToolResult.Error(
                        $"Tool '{ctx.Function.Name}' blocked by hook: {hookResult.BlockingErrors[0].Error}");
                }
            }

            var result = await next(ctx, ct).ConfigureAwait(false);

            if (options.HookExecutionService is not null && ctx.Function is not null)
            {
                // 从工具结果提取 ToolResponse / ToolError，供外部脚本做审计/失败通知。
                // ToolResult 携带 IsError 语义；其他类型（string/Exception/null）原样作为 response。
                string? toolError = null;
                if (result is ToolResult tr && tr.IsError)
                    toolError = tr.Content;

                var postPayload = new HookPayload
                {
                    Event = HookEvent.PostToolUse,
                    ToolName = ctx.Function.Name,
                    ToolInput = ctx.Arguments is not null
                        ? JsonSerializer.SerializeToElement(ctx.Arguments)
                        : JsonSerializer.SerializeToElement(new { }),
                    ToolResponse = result,
                    ToolError = toolError,
                };

                // Post-hook 是通知/审计语义，失败不应丢弃已成功的工具结果。
                // 仅记录日志，保留 result 原样返回（fail-soft）。
                try
                {
                    await options.HookExecutionService.FireAsync(
                        postPayload,
                        actualMatcherValue: ctx.Function.Name,
                        ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // OCE 必须透传，保留取消信号
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "PostToolUse hook threw for tool {ToolName}; tool result preserved",
                        ctx.Function.Name);
                }
            }

            return result;
        };
    }
}
