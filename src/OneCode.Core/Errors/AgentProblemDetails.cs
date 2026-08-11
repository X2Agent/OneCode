namespace OneCode.Core.Errors;

/// <summary>
/// 结构化错误契约 — 跨边界传递的机器可读错误详情。
/// </summary>
/// <remarks>
/// ERR-1: 替代裸字符串 <c>ToolResult.Error(string)</c>，使子 Agent 失败
/// 可在主会话日志中用 <see cref="TraceId"/> 关联。遵循 RFC 9457 (Problem Details) 语义。
/// </remarks>
public sealed record AgentProblemDetails(
    string Type,           // "https://onecode/errors/permission-denied" 等
    string Title,          // 短标题，如 "Permission Denied"
    int Status,            // HTTP-ish 状态码：403/500/503
    string Detail,         // 人类可读详情
    string? TraceId = null,
    string? ToolName = null,
    string? SuggestedNextAction = null,
    IReadOnlyDictionary<string, object?>? Extensions = null)
{
    /// <summary>从当前 Activity 提取 TraceId（与 OBS-1.1 协同）。</summary>
    public static string? CurrentTraceId =>
        System.Diagnostics.Activity.Current?.TraceId.ToHexString();

    /// <summary>便捷工厂：权限拒绝 (403)。</summary>
    public static AgentProblemDetails PermissionDenied(
        string detail, string? toolName = null, string? traceId = null) =>
        new("https://onecode/errors/permission-denied", "Permission Denied", 403,
            detail, traceId ?? CurrentTraceId, toolName);

    /// <summary>便捷工厂：工具执行失败 (500)。</summary>
    public static AgentProblemDetails ToolExecutionFailed(
        string detail, string? toolName = null, string? traceId = null,
        string? suggestedNextAction = null) =>
        new("https://onecode/errors/tool-execution-failed", "Tool Execution Failed", 500,
            detail, traceId ?? CurrentTraceId, toolName, suggestedNextAction);

    /// <summary>便捷工厂：外部服务不可用 (503)。</summary>
    public static AgentProblemDetails ServiceUnavailable(
        string detail, string? toolName = null, string? traceId = null) =>
        new("https://onecode/errors/service-unavailable", "Service Unavailable", 503,
            detail, traceId ?? CurrentTraceId, toolName);
}
