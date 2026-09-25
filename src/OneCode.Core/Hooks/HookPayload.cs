namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 拦截点的运行时数据载荷——传递给钩子处理器的完整上下文。
/// </summary>
/// <remarks>
/// 字段按拦截点选择性填充：公共字段始终可写，拦截点专属字段
/// （ModelId / RequestMessages / ModelResponse / FinishReason / ToolInput / ToolResponse / OutputText）
/// 仅在对应节点上有语义。
/// matcher 的实际比较值通过 <see cref="IHookExecutionService.FireAsync"/> 的
/// actualMatcherValue 参数显式传入，不从 payload 字段猜测（语义声明见 <see cref="HookPointMetadataRegistry"/>）。
/// </remarks>
public sealed record HookPayload
{
    /// <summary>拦截点。</summary>
    public HookInterceptionPoint Point { get; init; }

    /// <summary>会话标识（未处于会话上下文时为 null）。</summary>
    public string? SessionId { get; init; }

    /// <summary>当前工作目录。</summary>
    public string? Cwd { get; init; }

    /// <summary>工具名称（pre_tool_call / post_tool_call），同时作为工具节点的 matcher 值。</summary>
    public string? ToolName { get; set; }

    /// <summary>工具调用输入（pre_tool_call / post_tool_call）。</summary>
    public JsonElement? ToolInput { get; init; }

    /// <summary>工具调用 ID（多工具批次内区分单次调用）。</summary>
    public string? ToolUseId { get; init; }

    /// <summary>工具结果（post_tool_call）。</summary>
    public object? ToolResponse { get; init; }

    /// <summary>工具错误内容（post_tool_call，ToolResult.IsError 时提取）。</summary>
    public string? ToolError { get; init; }

    /// <summary>用户输入文本（input）。</summary>
    public string? UserMessage { get; set; }

    /// <summary>模型标识（pre/post_model_call），同时作为模型节点的 matcher 值。</summary>
    public string? ModelId { get; init; }

    /// <summary>模型请求消息投影（pre/post_model_call）。</summary>
    public JsonElement? RequestMessages { get; init; }

    /// <summary>模型响应文本投影（post_model_call）。</summary>
    public string? ModelResponse { get; init; }

    /// <summary>模型完成原因（post_model_call，如 stop / tool_use / length）。</summary>
    public string? FinishReason { get; init; }

    /// <summary>最终响应文本（output）。</summary>
    public string? OutputText { get; set; }

    /// <summary>子代理标识（子代理链路节点）。</summary>
    public string? AgentId { get; init; }

    /// <summary>子代理类型（子代理链路节点）。</summary>
    public string? AgentType { get; init; }

    /// <summary>节点发生时间。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
