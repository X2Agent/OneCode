using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
/// 模型调用的过载（429/529）重试由 RetryOnOverloadChatClient 在 ChatClient 层负责，
/// 不在本中间件做工具结果的字符串匹配改写。
/// </summary>
public sealed class ToolResultUnwrapMiddleware(
    string? modelId = null,
    string? providerId = null,
    ILogger<ToolResultUnwrapMiddleware>? logger = null)
{
    private readonly string? _modelId = modelId;
    private readonly string? _providerId = providerId;
    private readonly ILogger<ToolResultUnwrapMiddleware>? _logger = logger;

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

            object? result;
            try
            {
                result = await next(ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 工具函数抛出的异常转发为错误结果：模型收到可读错误可自纠正，
                // 也不会计入 FunctionInvokingChatClient 的连续错误上限（达到上限会终止整个 run）。
                _logger?.LogWarning(ex, "Tool '{Tool}' threw; forwarding as error result",
                    ctx.Function?.Name);
                result = ToolResult.Error($"Tool '{ctx.Function?.Name}' failed: {ex.Message}");
            }

            // 仅处理 ToolResult 类型；其他类型（string/null）原样返回，
            // 让 IChatClient 适配器按其默认行为处理。
            if (result is not ToolResult tr)
                return result;

            // 正常路径：序列化 ToolResult 为 string
            var isError = tr.IsError;
            result = (object)ToolResultSerializer.Serialize(tr, _modelId, _providerId);

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
}