using OneCode.Core.Models;

namespace OneCode.Core.Tools;

/// <summary>
/// 根据模型能力将 <see cref="ToolResult"/> 序列化为 LLM 可读的字符串。
/// </summary>
/// <remarks>
/// 兼容性策略：
/// <list type="bullet">
///   <item>支持结构化 JSON 的模型（Claude/GPT-4+/DeepSeek）：返回 JSON</item>
///   <item>不支持的模型（Ollama 本地模型等）：返回 Markdown 字符串</item>
/// </list>
/// 所有模型都能处理字符串形式的工具结果，因此降级为 Markdown 不会影响功能。
/// </remarks>
public static class ToolResultSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 序列化 ToolResult 为 LLM 可读字符串。
    /// </summary>
    /// <param name="result">工具结果。</param>
    /// <param name="modelId">当前模型 ID（用于能力检测）。</param>
    /// <param name="providerId">当前 provider ID。</param>
    /// <returns>序列化后的字符串。</returns>
    public static string Serialize(ToolResult result, string? modelId = null, string? providerId = null)
    {
        if (ModelCapabilities.SupportsStructuredToolResults(modelId, providerId))
            return JsonSerializer.Serialize(result, JsonOptions);

        return ToMarkdown(result);
    }

    /// <summary>
    /// 将 ToolResult 转换为 Markdown 格式（兼容不支持 JSON 的模型）。
    /// </summary>
    private static string ToMarkdown(ToolResult result)
    {
        var severity = result.Severity ?? (result.IsError ? "error" : "info");
        var tag = severity.ToLowerInvariant() switch
        {
            "error" => "ERROR",
            "warning" => "WARNING",
            _ => "SUCCESS",
        };

        var parts = new List<string>(5) { $"[{tag}] {result.Content}" };

        // 结构化 problemDetails 块（RFC 9457 语义）
        if (result.ErrorDetails is { } problem)
        {
            parts.Add($"Problem: {problem.Type} (status={problem.Status})");
            if (!string.IsNullOrEmpty(problem.TraceId))
                parts.Add($"TraceId: {problem.TraceId}");
        }

        if (result.SuggestedNextAction is not null)
            parts.Add($"Suggested next: {result.SuggestedNextAction}");

        if (result.Telemetry is { Count: > 0 })
        {
            var telemetryParts = result.Telemetry
                .Where(kv => kv.Value is not null)
                .Select(kv => $"  {kv.Key}: {kv.Value}");
            var telemetryList = string.Join("\n", telemetryParts);
            if (telemetryList.Length > 0)
                parts.Add("Telemetry:\n" + telemetryList);
        }

        return string.Join("\n", parts);
    }
}
