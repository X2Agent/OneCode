namespace OneCode.Core.Tools;

/// <summary>
/// 结构化工具执行结果。
/// </summary>
/// <remarks>
/// 工具方法应返回 <see cref="ToolResult"/> 而非裸 <c>string</c>，以便：
/// <list type="bullet">
///   <item><see cref="IsError"/> 字段让 ChatService/StateMachineMiddleware 准确识别错误</item>
///   <item><see cref="Severity"/> 让 UI 层按严重级别渲染</item>
///   <item><see cref="Telemetry"/> 记录文件路径/行数/耗时等结构化指标</item>
///   <item><see cref="SuggestedNextAction"/> 引导 LLM 下一步（如"修复编译错误后重试"）</item>
/// </list>
/// 序列化时由 <c>ToolResultSerializer</c> 根据模型能力选择 JSON 或 Markdown 格式，
/// 兼容不支持结构化 JSON 的模型（如某些 Ollama 本地模型）。
/// </remarks>
public sealed record ToolResult(
    string Content,
    bool IsError = false,
    string? Severity = null,
    IReadOnlyDictionary<string, object?>? Telemetry = null,
    string? SuggestedNextAction = null)
{
    /// <summary>创建成功结果。</summary>
    public static ToolResult Success(string content, string? suggestedNextAction = null) =>
        new(content, IsError: false, Severity: "info", SuggestedNextAction: suggestedNextAction);

    /// <summary>
    /// 将匿名对象/DTO 序列化为 JSON 并创建成功结果。
    /// 统一工具层反复出现的「匿名对象序列化为 JSON 的成功结果」模式（曾达 20+ 处）。
    /// </summary>
    public static ToolResult JsonSuccess(object data, string? suggestedNextAction = null) =>
        Success(System.Text.Json.JsonSerializer.Serialize(data), suggestedNextAction);

    /// <summary>创建错误结果。</summary>
    public static ToolResult Error(string content, string? suggestedNextAction = null) =>
        new(content, IsError: true, Severity: "error", SuggestedNextAction: suggestedNextAction);

    /// <summary>创建警告结果（非错误，但需要关注）。</summary>
    public static ToolResult Warning(string content, string? suggestedNextAction = null) =>
        new(content, IsError: false, Severity: "warning", SuggestedNextAction: suggestedNextAction);

    /// <summary>
    /// ERR-1: 从 <see cref="Errors.AgentProblemDetails"/> 创建结构化错误结果。
    /// 将 problemDetails 字段写入 Telemetry，使 ToolResultSerializer 能输出结构化 problemDetails 块。
    /// </summary>
    public static ToolResult Error(Errors.AgentProblemDetails details)
    {
        var telemetry = new Dictionary<string, object?>
        {
            ["problem.type"] = details.Type,
            ["problem.status"] = details.Status,
            ["problem.traceId"] = details.TraceId ?? string.Empty,
            ["problem.toolName"] = details.ToolName ?? string.Empty,
        };
        if (details.Extensions is { Count: > 0 })
        {
            foreach (var kv in details.Extensions)
                telemetry[$"problem.ext.{kv.Key}"] = kv.Value;
        }
        return new ToolResult(
            Content: details.Detail ?? details.Title,
            IsError: true,
            Severity: "error",
            Telemetry: telemetry,
            SuggestedNextAction: details.SuggestedNextAction);
    }
}
