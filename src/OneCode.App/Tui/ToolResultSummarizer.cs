namespace OneCode.App.Tui;

/// <summary>
/// 为工具调用结果生成人类可读的摘要。
/// 类似于 Claude Code 助手的工具调用显示风格。
/// </summary>
public static class ToolResultSummarizer
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

    private static string? SummarizeWebFetch(string result, string? toolInput)
    {
        // 尝试提取标题或 URL
        if (!string.IsNullOrEmpty(toolInput) && toolInput.Contains("url", StringComparison.Ordinal))
        {
            var url = ExtractUrlFromInput(toolInput);
            if (!string.IsNullOrEmpty(url))
                return Truncate(url, 50);
        }
        return "Fetched";
    }

    private static string? SummarizeWebSearch(string result, string? toolInput)
    {
        // 结果经信封解包后是 {provider, query, results:[...]}——按结构化数组计数，
        // 旧实现按原始文本行数计数，JSON 单行结果恒显示 "1 results"。
        var content = OneCode.Core.Tools.DisplayJsonSerializer.TryUnwrapEnvelope(result, out var inner)
            ? inner
            : result;
        var count = ExtractArrayLength(content, "results");
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

        // 用户提问工具 - 显示问题或向导标题，不显示整段 JSON 参数。
        if (toolName.Equals("AskUserQuestion", StringComparison.OrdinalIgnoreCase))
        {
            var question = ExtractFieldFromJson(toolInput, "question") ?? toolInput;
            return Truncate(question, 60);
        }

        if (toolName.Equals("AskUserQuestions", StringComparison.OrdinalIgnoreCase))
        {
            var title = ExtractFieldFromJson(toolInput, "title") ?? toolInput;
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

    private static string? ExtractUrlFromInput(string input)
        => ExtractFieldFromJson(input, "url");

    /// <summary>
    /// Extracts a string field from a JSON object using <c>JsonDocument.Parse</c>.
    /// Handles escaped characters, nested objects, and Unicode correctly — unlike
    /// manual string IndexOf-based parsing which breaks on escaped quotes or
    /// values containing the field name as a substring.
    /// </summary>
    private static string? ExtractFieldFromJson(string input, string fieldName)
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
