namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 事件的运行时数据载荷——传递给钩子处理器的完整上下文。
/// </summary>
/// <remarks>
/// 字段按事件类型选择性填充：公共字段始终可写，事件专属字段
/// （Trigger/TerminalReason/ErrorCategory）仅在对应事件上有语义。
/// matcher 的实际比较值通过 <see cref="IHookExecutionService.FireAsync"/> 的
/// actualMatcherValue 参数显式传入，不从 payload 字段猜测（语义声明见 HookEventMetadataRegistry）。
/// </remarks>
public sealed record HookPayload
{
    /// <summary>事件类型。</summary>
    public HookEvent Event { get; init; }

    /// <summary>会话标识（未处于会话上下文时为 null）。</summary>
    public string? SessionId { get; init; }

    /// <summary>transcript 文件路径（Stop/StopFailure 等终结类事件）。</summary>
    public string? TranscriptPath { get; init; }

    /// <summary>当前工作目录。</summary>
    public string? Cwd { get; init; }

    /// <summary>工具名称（PreToolUse/PostToolUse/Notification 等工具相关事件）。</summary>
    public string? ToolName { get; set; }

    /// <summary>工具调用输入（PreToolUse/PostToolUse）。</summary>
    public JsonElement? ToolInput { get; init; }

    /// <summary>工具调用 ID（多工具批次内区分单次调用）。</summary>
    public string? ToolUseId { get; init; }

    /// <summary>工具响应（PostToolUse / PostCompact 摘要）。</summary>
    public object? ToolResponse { get; init; }

    /// <summary>工具错误内容（PostToolUse，ToolResult.IsError 时提取）。</summary>
    public string? ToolError { get; init; }

    /// <summary>是否为用户中断产生的调用（PostToolUse）。</summary>
    public bool IsInterrupt { get; init; }

    /// <summary>用户输入文本（UserPromptSubmit；StopFailure 时为异常消息）。</summary>
    public string? UserMessage { get; set; }

    /// <summary>子代理标识（子代理链路事件）。</summary>
    public string? AgentId { get; init; }

    /// <summary>子代理类型（子代理链路事件）。</summary>
    public string? AgentType { get; init; }

    /// <summary>事件发生时间。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 触发方式（PreCompact/PostCompact：<see cref="HookTriggers.Manual"/>=手动命令，
    /// <see cref="HookTriggers.Auto"/>=自动压缩/恢复），同时作为 matcher 值。
    /// </summary>
    public string? Trigger { get; init; }

    /// <summary>
    /// 终结原因（Stop 事件，RunTerminalReason 枚举名），同时作为 Stop hook 的 matcher 值——
    /// 可按 "成功完成" / "各种异常收场" 分别配置 hook。
    /// </summary>
    public string? TerminalReason { get; set; }

    /// <summary>
    /// 失败类别（StopFailure 事件），同时作为 matcher 值：
    /// rate_limit / auth_failed / billing / invalid_request / server_error / max_output_tokens / unknown。
    /// </summary>
    public string? ErrorCategory { get; set; }
}
