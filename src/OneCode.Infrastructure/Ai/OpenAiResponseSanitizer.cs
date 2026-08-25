namespace OneCode.Infrastructure.Ai;

using System.Text.Json;

/// <summary>
/// Rewrites OpenAI-compatible JSON so the official OpenAI .NET SDK can deserialize it.
/// Third-party providers often send empty or vendor-specific <c>finish_reason</c>
/// values, and <c>null</c> in place of empty arrays.
/// </summary>
internal static partial class OpenAiResponseSanitizer
{
    /// <summary>
    /// Matches <c>"tool_calls": null</c> or <c>"annotations": null</c> (with any whitespace).
    /// These are the fields most commonly returned as null by third-party
    /// OpenAI-compatible providers (DeepSeek, Qwen, Moonshot, etc.)
    /// where the OpenAI SDK expects an array.
    /// </summary>
    [GeneratedRegex(@"""(tool_calls|annotations)""\s*:\s*null\b")]
    private static partial Regex NullArrayRegex();

    /// <summary>
    /// Matches an empty <c>"choices": []</c> array. Providers like OpenRouter return
    /// HTTP 200 with no choices on upstream overload / content filtering; the official
    /// SDK then crashes inside <c>ChatCompletion.get_Role()</c> (index out of range).
    /// </summary>
    [GeneratedRegex(@"""choices""\s*:\s*\[\s*\]")]
    private static partial Regex EmptyChoicesRegex();

    /// <summary>
    /// Returns true when the JSON payload is a chat-completion response whose
    /// <c>choices</c> array is empty or missing — the SDK cannot deserialize it
    /// and would throw ArgumentOutOfRangeException from ChatCompletion.get_Role().
    /// 覆盖三种形态：空数组、缺失 choices 的 chat.completion 体、以及
    /// OpenRouter 中间件错误体（HTTP 200 + {"error":{...}}，无 choices）。
    /// 判定基于结构而非子串：非 JSON 响应体（HTML 错误页/截断流等）一律不判空，
    /// 避免被误分类为可重试的 empty-choices。
    /// </summary>
    internal static bool HasEmptyChoices(string payload)
    {
        // 快速路径：正常补全体必含 "choices"，正则确认是否为空数组。
        if (payload.Contains("""choices""", StringComparison.Ordinal))
            return EmptyChoicesRegex().IsMatch(payload);

        // 不含 "choices" 字样：需确认是 JSON 对象且其上确实无 choices 属性才算缺失；
        // 解析失败（HTML/纯文本/截断体）→ 不是补全响应 → false。
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && !doc.RootElement.TryGetProperty("choices", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试从 HTTP 200 响应体中提取显式的上游错误对象（<c>{"error":{...}}</c>）。
    /// 一些 OpenAI 兼容网关（OpenRouter → 上游 Nvidia 等）过载时不返回 4xx/5xx，
    /// 而是 200 + 错误体。支持三种形态：
    /// <list type="bullet">
    /// <item><c>error</c> 为对象：<c>message</c> / <c>code</c> 字段（OpenRouter 还可能嵌套 <c>metadata.raw</c>）</item>
    /// <item><c>error</c> 为字符串：整体作为消息</item>
    /// <item>其余情况返回 false（不是错误体）</item>
    /// </list>
    /// </summary>
    internal static bool TryExtractUpstreamError(string payload, out string message, out string? code)
    {
        message = string.Empty;
        code = null;

        // 快速路径：绝大多数正常补全响应不含 "error" 字段，
        // 子串检查避免对每个响应都做完整 JSON 解析（长补全体解析开销可观）。
        if (!payload.Contains("""error""", StringComparison.Ordinal))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
                return false;

            // 补全优先：若响应同时携带非空 choices（有效补全），即使存在 error 字段
            // 也不视为错误体——绝不因附带的 error 字段丢弃正常业务响应。
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                return false;
            }

            switch (error.ValueKind)
            {
                case JsonValueKind.String:
                    message = error.GetString() ?? string.Empty;
                    break;

                case JsonValueKind.Object:
                    if (error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                        message = msg.GetString() ?? string.Empty;
                    if (error.TryGetProperty("code", out var c))
                        code = c.ValueKind switch
                        {
                            JsonValueKind.String => c.GetString(),
                            JsonValueKind.Number => c.GetRawText(),
                            _ => null,
                        };
                    // OpenRouter 形态：{"error":{"metadata":{"raw":"..."}}}
                    if (string.IsNullOrWhiteSpace(message)
                        && error.TryGetProperty("metadata", out var md)
                        && md.ValueKind == JsonValueKind.Object
                        && md.TryGetProperty("raw", out var raw)
                        && raw.ValueKind == JsonValueKind.String)
                    {
                        message = raw.GetString() ?? string.Empty;
                    }
                    break;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    // 某些网关在正常响应中附带 "error": null —— 不是错误。
                    return false;

                default:
                    return false;
            }

            return !string.IsNullOrWhiteSpace(message) || code is not null;
        }
        catch (JsonException)
        {
            // 非 JSON 或截断的响应体——不是可识别的错误体。
            return false;
        }
    }

    /// <summary>
    /// Matches a quoted <c>finish_reason</c> string. JSON <c>null</c> is left untouched
    /// because the SDK already accepts it for in-progress streaming chunks.
    /// </summary>
    [GeneratedRegex(@"""finish_reason""\s*:\s*""(?<value>[^""]*)""")]
    private static partial Regex FinishReasonRegex();

    /// <summary>
    /// Sanitizes a JSON object (full response body or a single SSE <c>data:</c> payload).
    /// </summary>
    internal static string SanitizePayload(string payload)
    {
        var withArrays = NullArrayRegex().Replace(payload, @"""$1"":[]");
        return FinishReasonRegex().Replace(withArrays, MapFinishReasonMatch);
    }

    /// <summary>
    /// Sanitizes one SSE line. Non-<c>data:</c> lines and the <c>[DONE]</c> marker are unchanged.
    /// </summary>
    internal static string SanitizeSseLine(string line)
    {
        const string prefix = "data:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return line;

        var jsonStart = prefix.Length;
        while (jsonStart < line.Length && line[jsonStart] == ' ')
            jsonStart++;

        if (jsonStart >= line.Length)
            return line;

        var json = line[jsonStart..];
        if (json.Equals("[DONE]", StringComparison.Ordinal))
            return line;

        var sanitized = SanitizePayload(json);
        return sanitized == json
            ? line
            : string.Concat(line.AsSpan(0, jsonStart), sanitized);
    }

    private static string MapFinishReasonMatch(Match match)
    {
        var value = match.Groups["value"].Value;
        if (IsPlaceholderFinishReason(value))
            return @"""finish_reason"":null";

        if (IsOfficialFinishReason(value))
            return match.Value;

        return @"""finish_reason"":""" + MapFinishReasonAlias(value) + @"""";
    }

    /// <summary>
    /// Empty / dummy values show up on in-progress streaming chunks. They must become
    /// JSON <c>null</c> (not <c>"stop"</c>), otherwise later deltas look like a completed turn.
    /// </summary>
    private static bool IsPlaceholderFinishReason(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Equals(".", StringComparison.Ordinal)
        || value.Equals("null", StringComparison.OrdinalIgnoreCase);

    private static bool IsOfficialFinishReason(string value) =>
        value.Equals("stop", StringComparison.OrdinalIgnoreCase)
        || value.Equals("length", StringComparison.OrdinalIgnoreCase)
        || value.Equals("tool_calls", StringComparison.OrdinalIgnoreCase)
        || value.Equals("content_filter", StringComparison.OrdinalIgnoreCase)
        || value.Equals("function_call", StringComparison.OrdinalIgnoreCase);

    private static string MapFinishReasonAlias(string value) => value.Trim().ToLowerInvariant() switch
    {
        "eos" or "eos_token" or "end_turn" or "stop_sequence" or "stop_seq" => "stop",
        "max_tokens" or "max_length" => "length",
        "tool_call" => "tool_calls",
        "content_filtered" or "sensitive" or "safety" => "content_filter",
        _ => "stop",
    };
}
