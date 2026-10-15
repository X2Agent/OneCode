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
            var parseError = ctx.CallContent.Exception;
            if (parseError is not null || (parameters is { Count: 0 } && DeclaresRequiredParameters(ctx.Function)))
            {
                // 工具调用的 JSON 参数无法解析或为空 —— 典型原因是模型单次输出过长被截断
                // （日志实证：Write 三连败，每次参数为 {}，模型已花约 40 秒生成整篇报告）。
                // 返回可指导自愈的错误，而不是让 [SAFETY] fail-closed 消息误导模型盲目重发同样的大参数。
                logger.LogWarning(
                    parseError,
                    "Tool '{ToolName}' received empty or unparseable arguments",
                    ctx.Function.Name);
                return ToolResult.Error(BuildTruncatedArgumentsHint(ctx.Function.Name, parseError));
            }

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

    /// <summary>判断工具 schema 是否声明了非空的 required 参数列表。</summary>
    private static bool DeclaresRequiredParameters(AIFunction function)
    {
        var schema = function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // required 数组非空即视为"本工具必须带参数调用"，空参数一定不是合法调用。
        foreach (var _ in required.EnumerateArray())
            return true;

        return false;
    }

    /// <summary>
    /// 构造针对"参数为空/被截断"场景的可自愈错误消息，指导模型改用分段写入策略。
    /// </summary>
    private static string BuildTruncatedArgumentsHint(string toolName, Exception? parseError)
    {
        var detail = parseError?.Message ?? "arguments were empty";
        return
            $"[TOOL ERROR] Tool '{toolName}' received empty or unparseable arguments ({detail}). " +
            "This usually means the tool-call output was truncated because it was too long. " +
            "Do NOT retry the same large call. Instead, split the work: first Write a short skeleton file, " +
            "then append the remaining content in several smaller Edit calls.";
    }
}
