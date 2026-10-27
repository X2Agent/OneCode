using System.Text.Json;
using OneCode.App.Query;

namespace OneCode.WebUI.Services;

/// <summary>上下文条目类型。</summary>
public enum WebContextKind
{
    /// <summary>本轮读过或写过的文件。</summary>
    File,

    /// <summary>本轮访问过的网页或发起的搜索。</summary>
    Link,
}

/// <summary>会话级上下文条目：同一目标在一次会话内只出现一条，统计读写次数与增删行数。</summary>
public sealed class WebContextItem(string key, WebContextKind kind, string target)
{
    public string Key { get; } = key;

    public WebContextKind Kind { get; } = kind;

    /// <summary>文件路径或链接（搜索为查询词）。</summary>
    public string Target { get; } = target;

    public int ReadCount { get; set; }

    public int WriteCount { get; set; }

    public int AddedLines { get; set; }

    public int RemovedLines { get; set; }
}

/// <summary>
/// 从工具入参与文件改动事件维护会话级上下文清单。
/// 运行时的 <see cref="ToolStartEvent"/> 与会话历史里的 <c>ToolUseBlock</c> 走同一套提取规则，
/// 因此切换会话后重放出的上下文与实时运行一致。
/// </summary>
public static class WebContextTracker
{
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
    {
        "read", "write", "edit", "multiedit", "notebookedit", "applyworkspaceedit", "delete",
    };

    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "write", "edit", "multiedit", "notebookedit", "applyworkspaceedit", "delete",
    };

    private static readonly HashSet<string> LinkTools = new(StringComparer.Ordinal)
    {
        "webfetch", "browserfetch", "websearch",
    };

    private static readonly string[] FileTargetKeys = ["filePath", "path", "notebookPath", "targetFile"];

    private static readonly string[] LinkTargetKeys = ["url", "query"];

    /// <summary>把一次工具调用折算进上下文清单；与文件/链接无关的工具直接忽略。</summary>
    public static void AccumulateToolUse(
        List<WebContextItem> context,
        string toolName,
        string? toolInput,
        ILogger logger)
    {
        var name = toolName.ToLowerInvariant();
        var isFileTool = FileTools.Contains(name);
        var isLinkTool = LinkTools.Contains(name);
        if (!isFileTool && !isLinkTool)
            return;

        var target = ExtractFirstString(toolInput, isFileTool ? FileTargetKeys : LinkTargetKeys, logger);
        if (string.IsNullOrWhiteSpace(target))
            return;

        var kind = isFileTool ? WebContextKind.File : WebContextKind.Link;
        var item = GetOrAdd(context, kind, target);
        if (isFileTool && WriteTools.Contains(name))
            item.WriteCount++;
        else
            item.ReadCount++;
    }

    /// <summary>文件改动事件补上增删行数；目标不在清单里时也补一条（例如子代理直接写盘）。</summary>
    public static void AccumulateFileChange(List<WebContextItem> context, FileChangeEvent change)
    {
        var item = GetOrAdd(context, WebContextKind.File, change.FileName);
        item.AddedLines += change.AddedLines.Count;
        item.RemovedLines += change.RemovedLines.Count;
    }

    private static WebContextItem GetOrAdd(List<WebContextItem> context, WebContextKind kind, string target)
    {
        var key = $"{kind}:{target}";
        var existing = context.FirstOrDefault(item => item.Key == key);
        if (existing is not null)
            return existing;

        existing = new WebContextItem(key, kind, target);
        context.Add(existing);
        return existing;
    }

    private static string? ExtractFirstString(string? json, IReadOnlyList<string> keys, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var key in keys)
            {
                if (document.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }
        catch (JsonException ex)
        {
            // 工具入参不一定是 JSON（部分内置工具传原文）：提取不到就不进上下文清单。
            logger.LogDebug(ex, "工具入参不是合法 JSON，跳过上下文提取");
        }

        return null;
    }
}
