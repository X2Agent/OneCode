using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// Hook 中间件：在工具执行前后触发 pre_tool_call / post_tool_call 拦截点。
///
/// pre 拦截点返回 deny（BlockingErrors）时阻止工具执行（返回 ToolResult.Error）。
/// post 拦截点在工具执行完成后触发，不消费 result（仅做通知/审计）。
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
            var sessionId = options.ConversationId?.ToString();
            var cwd = options.WorkingDirectory;
            var toolUseId = ctx.CallContent?.CallId;

            if (options.HookExecutionService is not null && ctx.Function is not null)
            {
                var prePayload = new HookPayload
                {
                    Point = HookInterceptionPoint.PreToolCall,
                    SessionId = sessionId,
                    Cwd = cwd,
                    ToolName = ctx.Function.Name,
                    ToolUseId = toolUseId,
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
                    logger.LogError(ex, "pre_tool_call hook threw for tool {ToolName}", ctx.Function.Name);
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
                // 内层 ToolResultUnwrapMiddleware 已将 ToolResult 序列化为字符串（JSON 信封或 [ERROR] 文本），
                // 故同时兼容未解包的 ToolResult 与解包后的字符串错误标识。
                var toolError = ExtractToolError(result);

                var postPayload = new HookPayload
                {
                    Point = HookInterceptionPoint.PostToolCall,
                    SessionId = sessionId,
                    Cwd = cwd,
                    ToolName = ctx.Function.Name,
                    ToolUseId = toolUseId,
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
                        "post_tool_call hook threw for tool {ToolName}; tool result preserved",
                        ctx.Function.Name);
                }
            }

            return result;
        };
    }

    /// <summary>
    /// 从工具返回值中提取错误详情：兼容未解包的 <see cref="ToolResult"/>、异常对象、
    /// 以及经 <c>ToolResultUnwrapMiddleware</c> 序列化后的 JSON 信封或纯文本错误前缀。
    /// </summary>
    private static string? ExtractToolError(object? result)
    {
        switch (result)
        {
            case ToolResult tr:
                return tr.IsError ? tr.Content : null;
            case Exception ex:
                return ex.Message;
            case string text:
                _ = DisplayJsonSerializer.TryUnwrapEnvelope(text, out var isError, out var unwrapped);
                return isError ? unwrapped : null;
            default:
                return null;
        }
    }
}
