using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneCode.App.Query;

/// <summary>
/// Stateless helpers for digesting MAF agent streaming events into TUI QueryEvents.
/// Extracted from ChatService to isolate the event-mapping concern.
///
/// All methods are pure functions — no instance state, no side effects.
/// The optional <see cref="ILogger"/> is used for debug-level failure logging only.
/// </summary>
internal static class AgentEventDigester
{
    /// <summary>
    /// Extracts a human-readable summary from a tool call's arguments
    /// (e.g., file path, command, pattern) for TUI display.
    /// </summary>
    internal static string? ExtractToolInputSummary(FunctionCallContent fcc, ILogger logger)
    {
        try
        {
            if (fcc.Arguments is null) return null;

            var filePath = OneCode.Core.Tools.ToolArgumentExtractor.ExtractFilePath(fcc.Arguments);
            if (filePath is { Length: > 0 }) return filePath;

            if (TryGetArgument(fcc.Arguments, "command", out var cmd)) return Truncate(cmd, 80);
            if (TryGetArgument(fcc.Arguments, "pattern", out var pat)) return Truncate(pat, 80);
            if (TryGetArgument(fcc.Arguments, "query", out var q)) return Truncate(q, 80);
            if (string.Equals(fcc.Name, "AskUserQuestion", StringComparison.OrdinalIgnoreCase)
                && TryGetArgument(fcc.Arguments, "question", out var question))
                return Truncate(question, 80);
            if (string.Equals(fcc.Name, "AskUserQuestions", StringComparison.OrdinalIgnoreCase)
                && TryGetArgument(fcc.Arguments, "title", out var title))
                return Truncate(title, 80);

            var json = System.Text.Json.JsonSerializer.Serialize(fcc.Arguments, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            });
            return Truncate(json, 100);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ExtractToolInputSummary failed for tool {Tool}", fcc.Name);
            return null;
        }
    }

    /// <summary>
    /// Serializes tool-call arguments to a JSON string for transcript persistence.
    /// Returns null on failure — callers must not treat failure as an empty object.
    /// </summary>
    internal static string? SerializeArguments(object? arguments, ILogger logger)
    {
        if (arguments is null) return "{}";
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(arguments, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to serialize tool arguments; skipping ToolUseBlock persistence");
            return null;
        }
    }

    internal static bool TryGetArgument(object arguments, string key, out string value)
    {
        value = "";
        try
        {
            if (arguments is System.Collections.IDictionary dict && dict.Contains(key))
            {
                value = dict[key]?.ToString() ?? "";
                return value.Length > 0;
            }
            if (arguments is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    value = prop.GetString() ?? "";
                    return value.Length > 0;
                }
            }
        }
        catch (Exception ex)
        {
            // Intentionally debug-only: argument extraction is best-effort
            System.Diagnostics.Debug.WriteLine($"TryGetArgument('{key}') failed: {ex.Message}");
        }
        return false;
    }

    internal static string Truncate(string s, int max) =>
        s.Length > max ? s[..(max - 3)] + "..." : s;

    /// <summary>
    /// Extracts error flag and result text from a tool result content.
    /// Handles Exception, string, ToolResult, and arbitrary objects.
    /// </summary>
    internal static (bool IsError, string? Result) ExtractToolResult(FunctionResultContent frc, ILogger logger)
    {
        try
        {
            var result = frc.Result;
            if (result is Exception ex)
                return (true, ex.Message);
            if (result is string s)
                return (false, s);
            if (result is Core.Tools.ToolResult tr)
            {
                var text = Core.Tools.ToolResultSerializer.Serialize(tr);
                return (tr.IsError, text);
            }
            if (result is null)
                return (false, null);
            var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            });
            return (false, json.Length > 500 ? json[..497] + "..." : json);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ExtractToolResult failed for call {CallId}", frc.CallId);
            return (false, null);
        }
    }

    /// <summary>
    /// Extracts token usage from streaming updates via the standard UsageContent API.
    /// Supports Anthropic cache_creation_input_tokens via AdditionalCounts.
    /// </summary>
    internal static bool TryExtractUsage(AgentResponseUpdate update, out TokenUsage usage)
    {
        usage = new TokenUsage(0, 0);

        if (update.Contents is not { Count: > 0 })
            return false;

        var usageContent = update.Contents.OfType<UsageContent>().FirstOrDefault();
        if (usageContent?.Details is not { } details)
            return false;

        var input = SafeInt(details.InputTokenCount);
        var output = SafeInt(details.OutputTokenCount);
        if (input == 0 && output == 0)
            return false;

        var cacheRead = SafeInt(details.CachedInputTokenCount);

        var cacheWrite = ExtractAdditionalCount(details,
            "cache_creation_input_tokens",
            "cache_creation",
            "cacheWriteInputTokens",
            "cache_write_input_tokens");

        usage = new TokenUsage(
            input,
            output,
            CacheReadTokens: cacheRead,
            CacheWriteTokens: cacheWrite);
        return true;
    }

    internal static int ExtractAdditionalCount(UsageDetails details, params string[] keys)
    {
        if (details.AdditionalCounts is null || keys.Length == 0)
            return 0;

        foreach (var key in keys)
        {
            if (details.AdditionalCounts.TryGetValue(key, out var value))
                return SafeInt(value);
        }

        return 0;
    }

    internal static int SafeInt(long? value)
        => value is null or 0 ? 0 : value > int.MaxValue ? int.MaxValue : (int)value.Value;

    /// <summary>
    /// Returns history without the latest user message (which is passed separately
    /// to MainAgentRunner to avoid duplication).
    /// </summary>
    internal static IReadOnlyList<ChatMessage> BuildHistoryWithoutLatestUser(
        IReadOnlyList<ChatMessage> history,
        string latestUserPrompt)
    {
        if (history.Count == 0)
            return history;

        if (history[^1].Role == ChatRole.User
            && string.Equals(history[^1].Text, latestUserPrompt, StringComparison.Ordinal))
        {
            return history.Count == 1
                ? []
                : history.Take(history.Count - 1).ToList();
        }

        return history;
    }
}
