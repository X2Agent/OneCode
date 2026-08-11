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

        return toolName.ToLowerInvariant() switch
        {
            "write" or "writetool" => SummarizeWrite(result, toolInput),
            "read" or "readtool" => SummarizeRead(result, toolInput),
            "edit" or "edittool" => SummarizeEdit(result, toolInput),
            "bash" or "bashtool" => SummarizeBash(result, toolInput),
            "powershell" or "powershelltool" => SummarizePowerShell(result, toolInput),
            "grep" or "greptool" => SummarizeGrep(result, toolInput),
            "glob" or "globtool" => SummarizeGlob(result, toolInput),
            "task" => SummarizeTask(result, toolInput),
            "web_fetch" => SummarizeWebFetch(result, toolInput),
            "web_search" => SummarizeWebSearch(result, toolInput),
            _ => null
        };
    }

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

    private static string? SummarizePowerShell(string result, string? toolInput)
    {
        if (!string.IsNullOrEmpty(toolInput))
        {
            var cmd = ExtractCommandFromInput(toolInput);
            if (!string.IsNullOrEmpty(cmd))
                return Truncate(cmd, 40);
        }

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
        var lines = result.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (lines > 0)
            return $"{lines} results";
        return null;
    }

    /// <summary>
    /// 从工具输入中提取文件名，用于显示在工具调用行中。
    /// </summary>
    public static string? ExtractFileName(string toolName, string? toolInput)
    {
        if (string.IsNullOrEmpty(toolInput))
            return null;

        // 尝试提取路径
        var path = ExtractPathFromInput(toolInput);
        if (!string.IsNullOrEmpty(path))
        {
            // 只返回文件名部分，如果路径太长
            if (path.Length > 40)
            {
                var fileName = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(fileName))
                    return "..." + fileName;
            }
            return path;
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

        // 文件操作工具 - 显示路径
        var fileTools = new[] { "write", "read", "edit", "glob", "grep" };
        if (fileTools.Contains(toolName.ToLowerInvariant()))
        {
            var path = ExtractPathFromInput(toolInput);
            if (!string.IsNullOrEmpty(path))
            {
                // 简化路径显示
                return SimplifyPath(path);
            }
        }

        // Shell 工具 - 显示命令
        var shellTools = new[] { "bash", "powershell" };
        if (shellTools.Contains(toolName.ToLowerInvariant()))
        {
            var cmd = ExtractCommandFromInput(toolInput);
            if (!string.IsNullOrEmpty(cmd))
                return Truncate(cmd, 50);
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
    /// Extracts a string field from a JSON object using <see cref="JsonDocument.Parse"/>.
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
