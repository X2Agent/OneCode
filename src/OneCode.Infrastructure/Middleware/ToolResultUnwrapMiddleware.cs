using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// MAF Agent Middleware：把工具返回的 <see cref="ToolResult"/>（或经 <c>AIFunction.InvokeAsync</c>
/// 序列化后的 <see cref="JsonElement"/> 信封）解包为字符串，并把 <c>IsError</c> 语义写入 <c>ToolExecutionContext</c>。
///
/// 双职责：
///   1. <see cref="ToolResult"/> / <see cref="JsonElement"/> → string：经 <see cref="ToolResultSerializer"/>
///      按模型能力序列化（JSON/Markdown），避免 IChatClient 适配器把 record 序列化为不一致的包装对象；
///      外层 ToolExecutionBudget（按长度截断）依赖 string 输入。
///   2. <c>ToolResult.IsError</c> → <c>ToolExecutionContext.IsError</c>：用强类型字段传递
///      错误语义。ToolResult 序列化为 string 后 IsError 语义仍保留在 context.IsError 中，
///      供外层 EditGuard、StateMachine、ToolCallEvent 读取。
///
/// 模型调用的过载（429/529）重试由 RetryOnOverloadChatClient 在 ChatClient 层负责，
/// 不在本中间件做工具结果的字符串匹配改写。
/// </summary>
public sealed class ToolResultUnwrapMiddleware(
    string? modelId = null,
    string? providerId = null,
    ILogger<ToolResultUnwrapMiddleware>? logger = null)
{
    private static readonly JsonSerializerOptions UnwrapJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Tool '{Tool}' argument marshalling failed", ctx.Function?.Name);
                result = ToolResult.Error(
                    $"Tool argument binding failed: {ex.Message}. {ToolArgumentBindingMiddleware.ArrayFixHint}",
                    suggestedNextAction: "Resend the tool call with arguments matching the tool's JSON schema.");
            }
            catch (ArgumentException ex)
            {
                _logger?.LogWarning(
                    "Tool '{ToolName}' argument binding failed: {Message}",
                    ctx.Function?.Name,
                    ex.Message);
                result = ToolResult.Error(
                    $"[TOOL ERROR] Tool '{ctx.Function?.Name}' was called with invalid arguments: {ex.Message} " +
                    "Check the tool's required parameters and retry with correct arguments.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 工具函数抛出的异常转发为错误结果：模型收到可读错误可自纠正，
                // 也不会计入 FunctionInvokingChatClient 的连续错误上限（达到上限会终止整个 run）。
                _logger?.LogWarning(ex, "Tool '{Tool}' threw; forwarding as error result",
                    ctx.Function?.Name);
                result = ToolResult.Error($"Tool '{ctx.Function?.Name}' failed: {ex.Message}");
            }

            if (!TryExtractToolResult(result, out var tr))
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

    /// <summary>
    /// 从返回值中还原 <see cref="ToolResult"/>：既支持直接返回的 <see cref="ToolResult"/> 实例，
    /// 也支持 <c>AIFunction.InvokeAsync</c>（<c>ReflectionAIFunction</c>）将 <c>Task&lt;ToolResult&gt;</c>
    /// 序列化后产生的 <see cref="JsonElement"/> 信封。
    /// </summary>
    internal static bool TryExtractToolResult(object? result, out ToolResult toolResult)
    {
        switch (result)
        {
            case ToolResult direct:
                toolResult = direct;
                return true;

            case JsonElement { ValueKind: JsonValueKind.Object } element when IsToolResultEnvelope(element):
                try
                {
                    if (element.Deserialize<ToolResult>(UnwrapJsonOptions) is { } deserialized)
                    {
                        toolResult = deserialized;
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // 结构化扩展字段（如 Telemetry 自定义类型）反序列化失败时退回核心字段提取
                }

                toolResult = ExtractFallbackToolResult(element);
                return true;

            default:
                toolResult = null!;
                return false;
        }
    }

    private static bool IsToolResultEnvelope(JsonElement element)
    {
        var hasContent = false;
        var hasIsError = false;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name is "content" or "Content" &&
                prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            {
                hasContent = true;
            }
            else if (prop.Name is "isError" or "IsError" &&
                     prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                hasIsError = true;
            }

            if (hasContent && hasIsError)
                return true;
        }

        return false;
    }

    private static ToolResult ExtractFallbackToolResult(JsonElement element)
    {
        var content = string.Empty;
        var isError = false;
        string? severity = null;
        string? suggestedNextAction = null;

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name is "content" or "Content" && prop.Value.ValueKind == JsonValueKind.String)
                content = prop.Value.GetString() ?? string.Empty;
            else if (prop.Name is "isError" or "IsError" && prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isError = prop.Value.GetBoolean();
            else if (prop.Name is "severity" or "Severity" && prop.Value.ValueKind == JsonValueKind.String)
                severity = prop.Value.GetString();
            else if (prop.Name is "suggestedNextAction" or "SuggestedNextAction" && prop.Value.ValueKind == JsonValueKind.String)
                suggestedNextAction = prop.Value.GetString();
        }

        return new ToolResult(content, IsError: isError, Severity: severity, SuggestedNextAction: suggestedNextAction);
    }
}