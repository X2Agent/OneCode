namespace OneCode.App.Tui;

using System.Text.RegularExpressions;

/// <summary>
/// 为工具调用结果生成人类可读的摘要。
/// 类似于 Claude Code 助手的工具调用显示风格。
/// </summary>
public static partial class ToolResultSummarizer
{
    /// <summary>
    /// 为工具调用生成结果摘要。
    /// </summary>
    /// <param name="toolName">工具名称</param>
    /// <param name="result">工具结果文本</param>
    /// <param name="toolInput">工具输入参数（可选）</param>
    /// <returns>人类可读的摘要，如 "Wrote 248 lines" 或 null（如果无法生成摘要）</returns>
    public static string? Summarize(string toolName, string? result, string? toolInput = null)
    {
        if (string.IsNullOrEmpty(result))
            return null;

        return NormalizeName(toolName) switch
        {
            "write" => SummarizeWrite(result, toolInput),
            "read" => SummarizeRead(result, toolInput),
            "edit" => SummarizeEdit(result, toolInput),
            "bash" => SummarizeBash(result, toolInput),
            "grep" => SummarizeGrep(result, toolInput),
            "glob" => SummarizeGlob(result, toolInput),
            "task" => SummarizeTask(result, toolInput),
            "agent" => SummarizeAgent(result),
            "webfetch" => SummarizeWebFetch(result, toolInput),
            "websearch" => SummarizeWebSearch(result, toolInput),
            "ls" => SummarizeLs(result),
            _ => null
        };
    }

    /// <summary>
    /// 工具名归一化：小写 + 去下划线，让注册名（WebSearch/LS/WebFetch）与
    /// 旧 snake_case 写法（web_search）落到同一 case。旧实现按字面小写匹配，
    /// 注册名 WebSearch 归一化后是 websearch，case 写的 web_search 永不命中。
    /// </summary>
    internal static string NormalizeName(string toolName)
        => toolName.Replace("_", "").ToLowerInvariant();

    private static string? SummarizeWrite(string result, string? toolInput)
    {
        // 尝试从结果中提取行数
        var lines = result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (lines > 1)
            return $"Wrote {lines} lines";
        return "Wrote file";
    }

    private static string? SummarizeRead(string result, string? toolInput)
    {
        var lines = result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (lines > 0)
            return $"Read {lines} lines";
        return "Read file";
    }

    private static string? SummarizeEdit(string result, string? toolInput)
    {
        // 尝试从结果中解析修改统计
        var added = CountOccurrences(result, "+");
        var removed = CountOccurrences(result, "-");
        if (added > 0 || removed > 0)
            return $"+{added}/-{removed} lines";
        return "Modified file";
    }

    private static string? SummarizeBash(string result, string? toolInput)
    {
        // 显示命令的前一部分
        if (!string.IsNullOrEmpty(toolInput))
        {
            var cmd = ExtractCommandFromInput(toolInput);
            if (!string.IsNullOrEmpty(cmd))
                return Truncate(cmd, 40);
        }

        // 或者显示输出行数
        var lines = result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (lines > 1)
            return $"{lines} lines output";
        return null;
    }

    private static string? SummarizeGrep(string result, string? toolInput)
    {
        var matches = result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (matches == 0)
            return "No matches";
        if (matches == 1)
            return "1 match";
        return $"{matches} matches";
    }

    private static string? SummarizeGlob(string result, string? toolInput)
    {
        var files = result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (files == 0)
            return "No files";
        if (files == 1)
            return "1 file";
        return $"{files} files";
    }

    private static string? SummarizeTask(string result, string? toolInput)
    {
        // The unified Task tool routes by action parameter.
        // Try to extract the action from toolInput JSON; fall back to result content.
        var action = ExtractFieldFromJson(toolInput ?? "", "action");
        if (string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(result))
        {
            // Result JSON may contain status or task fields
            if (result.Contains("\"status\":\"started\"", StringComparison.OrdinalIgnoreCase))
                action = "create";
            else if (result.Contains("\"status\":\"cancelled\"", StringComparison.OrdinalIgnoreCase))
                action = "stop";
        }

        return action?.ToLowerInvariant() switch
        {
            "create" => "Created",
            "update" => "Updated",
            "stop" => "Stopped",
            "get" => "Task details",
            "list" => "Task list",
            "output" => "Task output",
            _ => null,
        };
    }

    private static string? SummarizeAgent(string result)
    {
        // AgentTool 成功信封 {agent, conversationId, result, turnsCompleted, maxTurnsReached, toolCalls}：
        // 完成行显示 "Explore · 3 turns · 12 tool calls"；错误信封显示错误 title。
        // 后台模式信封 {background:true, taskId, ...} 无 result 字段——显示 "launched"。
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("background", out var bg) && bg.ValueKind == System.Text.Json.JsonValueKind.True)
                return "launched in background";

            if (root.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object
                && error.TryGetProperty("title", out var title))
                return Truncate(title.GetString() ?? "failed", 60);

            var agentName = root.TryGetProperty("agent", out var agent) ? agent.GetString() : null;
            var turns = ExtractIntField(root, "turnsCompleted");
            var toolCalls = ExtractIntField(root, "toolCalls");
            if (agentName is null && turns is null)
                return null;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(agentName))
                parts.Add(agentName);
            if (turns is { } t)
                parts.Add($"{t} turn" + (t == 1 ? "" : "s"));
            if (toolCalls is { } c and > 0)
                parts.Add($"{c} tool call" + (c == 1 ? "" : "s"));
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? SummarizeWebFetch(string result, string? toolInput)
    {
        // 成功信封 {bytes, code, codeText, result, durationMs}：折叠行显示状态码 + 大小，
        // URL 已由折叠行目标列展示，不再重复。
        // TryUnwrapEnvelope 对该信封不解包（无 Content 字段），content 即原始信封 JSON。
        // 错误结果信封会被解成错误消息纯文本——取首行作为失败原因。
        var content = OneCode.Core.Tools.DisplayJsonSerializer.TryUnwrapEnvelope(result, out var inner)
            ? inner
            : result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("code", out var code)
                && code.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                var status = root.TryGetProperty("codeText", out var codeText)
                    ? $"{code.GetInt32()} {codeText.GetString()}"
                    : $"{code.GetInt32()}";
                var size = root.TryGetProperty("bytes", out var bytes)
                    ? $" · {FormatBytes(bytes.GetInt64())}"
                    : "";
                return $"{status}{size}";
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 非 JSON——按错误消息文本处理，取首行。
        }

        var firstLine = content.Replace("\r\n", "\n").Split('\n')[0];
        return Truncate(firstLine, 60);
    }

    internal static string FormatBytes(long byteCount)
    {
        const long BytesPerKb = 1024;
        const long BytesPerMb = BytesPerKb * 1024;
        return byteCount switch
        {
            >= BytesPerMb => $"{byteCount / (double)BytesPerMb:0.#} MB",
            >= BytesPerKb => $"{byteCount / (double)BytesPerKb:0.#} KB",
            _ => $"{byteCount} B",
        };
    }

    private static string? SummarizeWebSearch(string result, string? toolInput)
    {
        // 结果经信封解包后是 {provider, query, results:[...]}——按结构化数组计数，
        // 旧实现按原始文本行数计数，JSON 单行结果恒显示 "1 results"。
        // 超阈值落盘后内联 results 只剩前几条，优先读 resultCount（完整条数）。
        var content = OneCode.Core.Tools.DisplayJsonSerializer.TryUnwrapEnvelope(result, out var inner)
            ? inner
            : result;
        var count = ExtractIntField(content, "resultCount") ?? ExtractArrayLength(content, "results");
        if (count is { } results)
            return $"{results} results";

        var lines = result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (lines > 0)
            return $"{lines} results";
        return null;
    }

    private static string? SummarizeLs(string result)
    {
        // LS 返回纯文本表格，首行 "Total: N entries"
        var content = OneCode.Core.Tools.DisplayJsonSerializer.TryUnwrapEnvelope(result, out var inner)
            ? inner
            : result;
        var firstLine = content.Replace("\r\n", "\n").Split('\n')[0];
        var start = firstLine.IndexOf("Total: ", StringComparison.Ordinal);
        if (start < 0)
            return null;
        var digits = new string(firstLine[(start + "Total: ".Length)..].TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? $"{digits} entries" : null;
    }

    /// <summary>从 JSON 文本中提取指定数组的长度；缺失或非 JSON 返回 null。</summary>
    private static int? ExtractArrayLength(string json, string propertyName)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out var prop)
                && prop.ValueKind == System.Text.Json.JsonValueKind.Array)
                return prop.GetArrayLength();
        }
        catch (System.Text.Json.JsonException)
        {
            // 非 JSON 文本——调用方回落到行数统计
        }
        return null;
    }

    /// <summary>从 JSON 对象中提取整数字段；缺失或类型不符返回 null。</summary>
    private static int? ExtractIntField(string json, string propertyName)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return ExtractIntField(doc.RootElement, propertyName);
        }
        catch (System.Text.Json.JsonException)
        {
            // 非 JSON 文本——调用方回落到数组计数
        }
        return null;
    }

    private static int? ExtractIntField(System.Text.Json.JsonElement root, string propertyName)
        => root.ValueKind == System.Text.Json.JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == System.Text.Json.JsonValueKind.Number
            && prop.TryGetInt32(out var value)
                ? value
                : null;

    /// <summary>
    /// 格式化工具调用行的目标显示。
    /// 例如："path/to/file.cs" 或 "grep pattern"
    /// </summary>
    public static string FormatTarget(string toolName, string? toolInput)
    {
        if (string.IsNullOrEmpty(toolInput))
            return "";

        var normalized = NormalizeName(toolName);

        // 文件操作工具 - 显示路径
        var fileTools = new[] { "write", "read", "edit", "glob", "grep", "ls" };
        if (fileTools.Contains(normalized))
        {
            var path = ExtractPathFromInput(toolInput);
            if (!string.IsNullOrEmpty(path))
            {
                // 简化路径显示
                return SimplifyPath(path);
            }
        }

        // Shell 工具 - 显示命令
        if (normalized == "bash")
        {
            var cmd = ExtractCommandFromInput(toolInput);
            if (!string.IsNullOrEmpty(cmd))
                return Truncate(cmd, 50);
        }

        // 搜索工具 - 显示 query / url，而不是整段 JSON 参数。
        // 输入有两种形状：完整 JSON（ToolDone 通道）或 ExtractToolInputSummary 提取的
        // 裸值（ToolStart 摘要通道）——JSON 提取失败时回退裸文本，两种通道显示一致。
        if (normalized == "websearch")
        {
            var query = ExtractFieldFromJson(toolInput, "query") ?? RawInputFallback(toolInput);
            if (!string.IsNullOrEmpty(query))
                return Truncate(query, 60);
        }
        if (normalized == "webfetch")
        {
            var url = ExtractFieldFromJson(toolInput, "url") ?? RawInputFallback(toolInput);
            if (!string.IsNullOrEmpty(url))
                return Truncate(url, 60);
        }

        // Agent 派工工具 - 显示 "代理名 · 任务描述"（如 "Explore · 调研近半月新闻"），
        // 不显示整段 JSON 参数。输入有完整/截断两种 JSON 形状——截断 JSON 解析失败时
        // 回退裸文本（ToolStart 摘要通道），与 websearch/webfetch 的双通道处理一致。
        if (normalized == "agent")
        {
            var agentName = ExtractFieldFromJson(toolInput, "agent") ?? ExtractAgentNameTolerant(toolInput);
            var description = ExtractFieldFromJson(toolInput, "description");
            if (!string.IsNullOrEmpty(agentName) && !string.IsNullOrEmpty(description))
                return Truncate($"{agentName} · {description}", 60);
            if (!string.IsNullOrEmpty(agentName))
                return Truncate(agentName, 60);
            if (!string.IsNullOrEmpty(description))
                return Truncate(description, 60);
            // 截断 JSON（ToolStart 通道 100 字符摘要）解析失败——原样截断显示，不返回 null。
            return Truncate(toolInput, 60);
        }

        // 用户提问工具 - 显示向导标题或首个问题文本，不显示整段 JSON 参数。
        if (toolName.Equals("AskUserQuestion", StringComparison.OrdinalIgnoreCase))
        {
            var title = ExtractFieldFromJson(toolInput, "title")
                ?? ExtractFirstQuestionText(toolInput)
                ?? toolInput;
            return Truncate(title, 60);
        }

        // 其他工具 - 先规范化 JSON 供人阅读，避免摘要显示 \uXXXX。
        var displayInput = OneCode.Core.Tools.DisplayJsonSerializer.NormalizeForDisplay(toolInput, writeIndented: false);
        return Truncate(displayInput, 40);
    }

    private static string? ExtractPathFromInput(string input)
    {
        var path = ExtractFieldFromJson(input, "path");
        if (!string.IsNullOrEmpty(path)) return path;

        // Fallback: if input is not JSON, treat as a raw path
        if (!input.Contains('{') && !input.Contains('['))
            return input.Trim('"', ' ', '\n', '\r');

        return null;
    }

    private static string? ExtractCommandFromInput(string input)
    {
        var cmd = ExtractFieldFromJson(input, "command");
        if (!string.IsNullOrEmpty(cmd)) return cmd;

        // Fallback: if input is not JSON, treat as a raw command
        if (!input.Contains('{') && !input.Contains('['))
            return input.Trim('"', ' ', '\n', '\r');

        return null;
    }

    /// <summary>
    /// 从 AskUserQuestion 的 questions 数组里取第一个元素的 question 文本，
    /// 用于单题（无 title）时给摘要一个可读目标。
    /// </summary>
    private static string? ExtractFirstQuestionText(string input)
    {
        if (string.IsNullOrEmpty(input) || input[0] != '{')
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("questions", out var questions)
                || questions.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            foreach (var item in questions.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object
                    && item.TryGetProperty("question", out var question)
                    && question.ValueKind == System.Text.Json.JsonValueKind.String)
                    return question.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not valid JSON — caller will fall back to raw input
        }
        return null;
    }

    /// <summary>
    /// Extracts a string field from a JSON object using <c>JsonDocument.Parse</c>.
    /// Handles escaped characters, nested objects, and Unicode correctly — unlike
    /// manual string IndexOf-based parsing which breaks on escaped quotes or
    /// values containing the field name as a substring.
    /// </summary>
    internal static string? ExtractFieldFromJson(string input, string fieldName)
    {
        if (string.IsNullOrEmpty(input) || input[0] != '{')
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty(fieldName, out var prop)
                && prop.ValueKind == System.Text.Json.JsonValueKind.String)
                return prop.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
            // Not valid JSON — caller will fall back to raw input
        }
        return null;
    }

    /// <summary>JSON 提取失败时的裸值回退：输入本身已是摘要文本（非 JSON）则原样返回。</summary>
    private static string? RawInputFallback(string input)
        => input.Contains('{') || input.Contains('[') ? null : input.Trim('"', ' ', '\n', '\r');

    /// <summary>
    /// 容错提取工具输入中的 agent 字段值（AddToolStart/AddToolDone 子代理进度行注册共用）。
    /// ToolStart 摘要通道传入截断 100 字符的 JSON——JsonDocument 解析失败，需用正则
    /// 直接匹配字段文本。仅在值完整（含闭合引号）时返回，避免截断值与进度事件里的
    /// 完整代理名对不上导致进度行永远不被更新。
    /// </summary>
    [GeneratedRegex(@"""agent""\s*:\s*""([^""]+)""")]
    private static partial Regex AgentFieldRegex();

    internal static string? ExtractAgentNameTolerant(string? toolInput)
    {
        if (string.IsNullOrEmpty(toolInput))
            return null;
        var match = AgentFieldRegex().Match(toolInput);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SimplifyPath(string path)
    {
        if (path.Length <= 45)
            return path;

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
            return Truncate(path, 45);

        // 显示 ".../folder/filename.ext" 格式
        if (fileName.Length > 40)
            return "..." + fileName[^40..];

        var prefix = "...";
        var remaining = 45 - prefix.Length - fileName.Length - 1; // -1 for separator
        if (remaining <= 0)
            return prefix + fileName;

        // 尝试包含部分目录结构
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && dir.Length > remaining)
        {
            var startIndex = Math.Max(0, dir.Length - remaining);
            return prefix + dir[startIndex..] + Path.DirectorySeparatorChar + fileName;
        }

        return prefix + fileName;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s ?? "";
        return s[..(max - 3)] + "...";
    }
}
