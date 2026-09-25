namespace OneCode.Infrastructure.Ai;

using System.Text.Json;

/// <summary>
/// Rewrites OpenAI-compatible JSON so the official OpenAI .NET SDK can deserialize it.
/// </summary>
internal static partial class OpenAiResponseSanitizer
{
    [GeneratedRegex(@"""(tool_calls|annotations)""\s*:\s*null\b")]
    private static partial Regex NullArrayRegex();

    [GeneratedRegex(@"""choices""\s*:\s*\[\s*\]")]
    private static partial Regex EmptyChoicesRegex();

    /// <summary>choices 为空或缺失（含 HTTP 200 + 错误体形态）；非 JSON 体不算。</summary>
    internal static bool HasEmptyChoices(string payload)
    {
        if (payload.Contains("""choices""", StringComparison.Ordinal))
            return EmptyChoicesRegex().IsMatch(payload);

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
    /// 提取响应体中的显式错误对象（{"error":{...}}），辅助字段折叠进 message。
    /// <paramref name="errorType"/> 取 OpenRouter 规范类型码（随 skin 在
    /// <c>error.metadata.error_type</c> 或 <c>error.error_type</c>），供按类型判定瞬时性。
    /// </summary>
    internal static bool TryExtractUpstreamError(
        string payload,
        out string message,
        out string? code,
        out string? errorType)
    {
        message = string.Empty;
        code = null;
        errorType = null;

        if (!payload.Contains("""error""", StringComparison.Ordinal))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
                return false;

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                return false;
            }

            // OpenRouter 类型码位置随 skin 漂移：Chat Completions 在 metadata 下，
            // Anthropic skin 平铺在 error 上。
            errorType = ReadString(error, "error_type")
                ?? ReadProperty(error, "metadata", "error_type");

            string? raw = null;

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
                    raw = ReadProperty(error, "metadata", "raw");
                    var rawClamped = Clamp(raw);

                    // 掩码外壳（OpenRouter 500 类）只保留通用 message，上游原文在
                    // metadata.raw，通用串无信息量，以 raw 为主消息。
                    if (IsMaskedMessage(message) && !string.IsNullOrWhiteSpace(raw))
                        message = rawClamped;

                    if (string.IsNullOrWhiteSpace(message) && raw is not null)
                    {
                        message = rawClamped;
                    }
                    break;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return false;

                default:
                    return false;
            }

            if (error.ValueKind == JsonValueKind.Object)
            {
                var details = CollectErrorDetails(error, messageIsRaw: message == Clamp(raw));
                if (details.Length > 0)
                    message = string.Concat(message, " ", details).Trim();
            }

            return !string.IsNullOrWhiteSpace(message) || code is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>OpenRouter 掩码外壳的通用 message 串，命中时 raw 优先作主消息。</summary>
    private static bool IsMaskedMessage(string? message) =>
        message is not null && (
            message.Equals("Provider returned error", StringComparison.OrdinalIgnoreCase)
            || message.Equals("Provider returned an error", StringComparison.OrdinalIgnoreCase)
            || message.Equals("The provider returned an error", StringComparison.OrdinalIgnoreCase)
            || message.Equals("An unexpected error occurred", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>按属性路径读字符串，任一环缺失或类型不符返回 null。</summary>
    private static string? ReadProperty(JsonElement parent, string name, string nested)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(element, nested);
    }

    private static string CollectErrorDetails(JsonElement error, bool messageIsRaw)
    {
        List<string> parts = [];

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"[{label}={value}]");
        }

        if (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            Add("type", type.GetString());

        if (error.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("error_type", out var errorType)
                && errorType.ValueKind == JsonValueKind.String)
                Add("error_type", errorType.GetString());

            if (metadata.TryGetProperty("provider_code", out var providerCode))
                Add("provider_code", providerCode.ValueKind == JsonValueKind.String
                    ? providerCode.GetString()
                    : providerCode.GetRawText());

            Add("provider_name", ReadString(metadata, "provider_name"));

            // moderation 错误带 reasons / flagged_input，指出哪段输入被标记。
            if (metadata.TryGetProperty("reasons", out var reasons)
                && reasons.ValueKind == JsonValueKind.Array)
            {
                var joined = string.Join(", ", reasons.EnumerateArray()
                    .Where(static r => r.ValueKind == JsonValueKind.String)
                    .Select(static r => r.GetString()!));
                Add("reasons", joined);
            }

            Add("flagged_input", ReadString(metadata, "flagged_input"));

            if (!messageIsRaw)
                Add("raw", Clamp(ReadString(metadata, "raw")));
        }

        return parts.Count == 0 ? string.Empty : string.Join(" ", parts);
    }

    /// <summary>上游原文可能是一整包 JSON，截断防止错误消息刷屏。</summary>
    private static string? Clamp(string? value) =>
        value is null || value.Length <= 512 ? value : string.Concat(value.AsSpan(0, 512), "…(truncated)");

    [GeneratedRegex(@"""finish_reason""\s*:\s*""(?<value>[^""]*)""")]
    private static partial Regex FinishReasonRegex();

    internal static string SanitizePayload(string payload)
    {
        var withArrays = NullArrayRegex().Replace(payload, @"""$1"":[]");
        return FinishReasonRegex().Replace(withArrays, MapFinishReasonMatch);
    }

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
