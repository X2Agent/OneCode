namespace OneCode.Infrastructure.Ai;

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
