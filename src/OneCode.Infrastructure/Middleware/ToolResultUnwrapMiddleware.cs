using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Domain;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// MAF Agent Middleware：把工具返回的 <see cref="ToolResult"/> 解包为字符串，
/// 并把 <c>IsError</c> 语义写入 <c>ToolExecutionContext</c>。
///
/// 双职责：
///   1. <see cref="ToolResult"/> → string：经 <see cref="ToolResultSerializer"/> 按模型能力
///      序列化（JSON/Markdown），避免 IChatClient 适配器把 record 序列化为 JSON 包装对象
///      （如 {"Content":"...","IsError":false}）造成 LLM 看到格式不一致；下游
///      ToolExecutionBudget（按长度截断）依赖 string 输入。
///   2. <c>ToolResult.IsError</c> → <c>ToolExecutionContext.IsError</c>：用强类型字段传递
///      错误语义。ToolResult 序列化为 string 后 IsError 语义丢失，外层 StateMachine
///      从 context.IsError 读取，不依赖字符串前缀匹配。
///
/// 此外处理可恢复错误（overloaded/529）：工具返回的 ToolResult 内容包含 "overloaded"
/// 或 "529" 时，标记为 Recovery guidance（IsError=false），让 LLM 重试而非计入失败。
/// </summary>
public sealed class ToolResultUnwrapMiddleware
{
    private readonly string? _modelId;
    private readonly string? _providerId;
    private readonly ILogger<ToolResultUnwrapMiddleware>? _logger;

    public ToolResultUnwrapMiddleware(
        string? modelId = null,
        string? providerId = null,
        ILogger<ToolResultUnwrapMiddleware>? logger = null)
    {
        _modelId = modelId;
        _providerId = providerId;
        _logger = logger;
    }

    public Func<AIAgent, FunctionInvocationContext,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
            CancellationToken, ValueTask<object?>>
        CreateDelegate()
    {
        return async (_, ctx, next, ct) =>
        {
            // 每次工具调用前重置 ToolExecutionContext，
            // 确保上一次残留状态（IsError/Guidance）不污染本次判定。
            var stateBag = AIAgent.CurrentRunContext?.Session?.StateBag;
            stateBag?.ResetToolExecutionContext();

            object result;
            try
            {
                result = await next(ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableError(ex.Message))
            {
                _logger?.LogWarning("Withholding recoverable exception (overloaded): {Message}",
                    ex.Message.Length > 200 ? ex.Message[..200] + "..." : ex.Message);
                MarkRecoveryGuidance(stateBag);
                return (object)"[RECOVERY] The service is overloaded. I'll retry shortly.";
            }

            // 仅处理 ToolResult 类型；其他类型（string/Exception/null）原样返回，
            // 让 IChatClient 适配器按其默认行为处理。
            if (result is not ToolResult tr)
                return result;

            var isError = tr.IsError;

            // 检查可恢复错误（overloaded/529）：覆盖 IsError=false + Guidance=Recovery
            if (isError && tr.Content is not null && IsRecoverableError(tr.Content))
            {
                _logger?.LogWarning("Withholding recoverable error (overloaded): {Message}",
                    tr.Content.Length > 200 ? tr.Content[..200] + "..." : tr.Content);
                isError = false;
                MarkRecoveryGuidance(stateBag);
                result = (object)"[RECOVERY] The service is overloaded. I'll retry shortly.";
            }
            else
            {
                // 正常路径：序列化 ToolResult 为 string
                result = (object)ToolResultSerializer.Serialize(tr, _modelId, _providerId);
            }

            // 从 ToolResult.IsError 提取写入 context（不覆盖 Guidance）。
            if (stateBag is not null)
            {
                var execCtx = stateBag.GetOrInitializeToolExecutionContext();
                execCtx.IsError = isError;
            }

            var text = result as string ?? "";
            _logger?.LogDebug(
                "Tool '{Tool}' result unwrapped: IsError={IsError}, Severity={Severity}, {Chars} chars",
                ctx.Function?.Name,
                isError,
                tr.Severity,
                text.Length);

            return result;
        };
    }

    /// <summary>
    /// 检测文本是否包含 529/overloaded 关键词（可恢复错误）。
    /// </summary>
    private static bool IsRecoverableError(string text)
    {
        return text.Contains("529", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("overloaded", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 StateBag 中标记当前结果为 Recovery guidance（IsError=false, Guidance=Recovery）。
    /// </summary>
    private static void MarkRecoveryGuidance(AgentSessionStateBag? stateBag)
    {
        if (stateBag is null)
            return;
        var execCtx = stateBag.GetOrInitializeToolExecutionContext();
        execCtx.IsError = false;
        execCtx.Guidance = GuidanceKind.Recovery;
    }
}
