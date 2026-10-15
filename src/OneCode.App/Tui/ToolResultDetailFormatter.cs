
namespace OneCode.App.Tui;

/// <summary>展开详情中的一行：文本 + 可选前景色（null 由调用方取默认色）。</summary>
internal sealed record ToolDetailLine(string Text, Color? Fg);

/// <summary>
/// 工具结果展开详情的按工具格式化：先把信封/双重编码解包成工具原始输出
/// （<see cref="DisplayJsonSerializer.TryUnwrapEnvelope(string, out string)"/>），WebSearch 等结构化结果
/// 渲染为人类可读列表，其余工具回退到缩进 JSON。纯函数无状态；
/// 折叠行的单行摘要见 <see cref="ToolResultSummarizer"/>。
/// </summary>
internal static partial class ToolResultDetailFormatter
{
    public static IReadOnlyList<ToolDetailLine> FormatDetail(string toolName, string? rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
            return [new ToolDetailLine("(空结果)", TuiPalette.FgMuted)];

        var content = DisplayJsonSerializer.TryUnwrapEnvelope(rawResult, out var inner)
            ? inner
            : rawResult;

        return ToolResultSummarizer.NormalizeName(toolName) switch
        {
            "websearch" => FormatWebSearch(content),
            "webfetch" => FormatWebFetch(content),
            "agent" => FormatAgent(content),
            _ => FormatGeneric(content),
        };
    }

    /// <summary>
    /// AgentTool 成功信封 {agent, result, turnsCompleted, maxTurnsReached, toolCalls}：
    /// 元信息头行 + result 正文预览（非原始 JSON）。错误/后台信封回退通用展示。
    /// </summary>
    private static IReadOnlyList<ToolDetailLine> FormatAgent(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("result", out var resultEl)
                || resultEl.ValueKind != JsonValueKind.String)
                return FormatGeneric(content);

            var meta = new List<string>();
            if (root.TryGetProperty("turnsCompleted", out var turns) && turns.ValueKind == JsonValueKind.Number)
            {
                var turnText = $"{turns.GetInt32()} turn" + (turns.GetInt32() == 1 ? "" : "s");
                if (root.TryGetProperty("maxTurnsReached", out var max) && max.ValueKind == JsonValueKind.True)
                    turnText += "（达到上限）";
                meta.Add(turnText);
            }
            if (root.TryGetProperty("toolCalls", out var calls) && calls.ValueKind == JsonValueKind.Number && calls.GetInt32() > 0)
                meta.Add($"{calls.GetInt32()} tool calls");

            List<ToolDetailLine> lines =
            [
                new(meta.Count > 0 ? string.Join(" · ", meta) : "完成", TuiPalette.FgPrimary),
            ];
            foreach (var raw in (resultEl.GetString() ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (lines.Count > GenericMaxLines)
                {
                    lines.Add(new("… 还有更多行（完整结果已发送给模型）", TuiPalette.FgMuted));
                    break;
                }
                lines.Add(new(raw, null));
            }
            return lines;
        }
        catch (JsonException)
        {
            return FormatGeneric(content);
        }
    }

    /// <summary>展开详情的正文预览行数上限（WebFetch）——全文已发送给模型或落盘，对话流里只做概览。</summary>
    private const int WebFetchPreviewLines = 30;

    /// <summary>通用（其余工具）展开详情的行数上限——超出部分折叠为剩余行数提示。</summary>
    private const int GenericMaxLines = 60;

    /// <summary>
    /// WebFetch 成功信封 {bytes, code, codeText, result, durationMs}：
    /// 头行 = 状态 · 大小 · prompt，正文预览最多 <see cref="WebFetchPreviewLines"/> 行。
    /// 错误结果（纯文本消息）回退通用展示。
    /// </summary>
    private static IReadOnlyList<ToolDetailLine> FormatWebFetch(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("result", out var resultEl)
                || resultEl.ValueKind != JsonValueKind.String)
                return FormatGeneric(content);

            var status = root.TryGetProperty("code", out var code) ? $"{code.GetInt32()} " : string.Empty;
            var statusText = root.TryGetProperty("codeText", out var codeText) ? codeText.GetString() : string.Empty;
            var sizeText = root.TryGetProperty("bytes", out var bytes)
                ? $" · {ToolResultSummarizer.FormatBytes(bytes.GetInt64())}"
                : string.Empty;
            var header = $"{status}{statusText}{sizeText}";

            // 正文首行形如 "Content from URL (prompt: ...):"，prompt 从中提取进头行，正文跳过该行。
            var body = resultEl.GetString() ?? string.Empty;
            var bodyLines = body.Replace("\r\n", "\n").Split('\n');
            var prompt = string.Empty;
            var skip = 0;
            if (bodyLines.Length > 0 && bodyLines[0].StartsWith("Content from URL (prompt: ", StringComparison.Ordinal))
            {
                var text = bodyLines[0];
                var end = text.LastIndexOf("):", StringComparison.Ordinal);
                prompt = end > 25 ? text["Content from URL (prompt: ".Length..end] : string.Empty;
                skip = 1;
            }
            while (skip < bodyLines.Length && bodyLines[skip].Length == 0)
                skip++;

            List<ToolDetailLine> lines = [new($"{header}{(prompt.Length > 0 ? $" · prompt: {TruncateText(prompt, 80)}" : "")}", TuiPalette.FgPrimary)];

            var remaining = bodyLines.Skip(skip).ToList();
            while (remaining.Count > 0 && remaining[^1].Length == 0)
                remaining.RemoveAt(remaining.Count - 1);

            foreach (var line in remaining.Take(WebFetchPreviewLines))
                lines.Add(new(line, null));
            if (remaining.Count > WebFetchPreviewLines)
            {
                lines.Add(new($"… 还有 {remaining.Count - WebFetchPreviewLines} 行（完整内容已发送给模型）", TuiPalette.FgMuted));
            }
            if (lines.Count == 1)
                lines.Add(new("(空正文)", TuiPalette.FgMuted));

            return lines;
        }
        catch (JsonException)
        {
            return FormatGeneric(content);
        }
    }

    /// <summary>
    /// WebSearch 结果 {provider, query, results:[{title,url,snippet}]} →
    /// 头行（query · provider · N 条）+ 每条结果的标题 / URL / 摘要。
    /// 内层解析失败（含上游截断产生的非法 JSON）回退通用展示。
    /// </summary>
    private static IReadOnlyList<ToolDetailLine> FormatWebSearch(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return FormatGeneric(content);

            var root = doc.RootElement;
            List<ToolDetailLine> lines = [new(BuildSearchHeader(root, results), TuiPalette.FgPrimary)];

            var index = 1;
            foreach (var item in results.EnumerateArray())
            {
                // 标题/摘要过 HTML 剥离：Tavily 旧结果（历史转录回放）夹带站点页脚标签。
                var title = HtmlPlainText.Strip(GetStringProperty(item, "title"));
                if (string.IsNullOrEmpty(title))
                    title = "(无标题)";
                lines.Add(new($"{index++}. {title}", TuiPalette.FgPrimary));

                var url = GetStringProperty(item, "url");
                if (!string.IsNullOrEmpty(url))
                    lines.Add(new($"   {url}", TuiPalette.Accent));

                var snippet = HtmlPlainText.Strip(GetStringProperty(item, "snippet"));
                if (!string.IsNullOrEmpty(snippet))
                    lines.Add(new($"   {TruncateText(snippet, SnippetMaxChars)}", TuiPalette.FgSecondary));
            }

            return lines;
        }
        catch (JsonException)
        {
            return FormatGeneric(content);
        }
    }

    private const int SnippetMaxChars = 200;

    private static string BuildSearchHeader(JsonElement root, JsonElement results)
    {
        var header = $"搜索: {GetStringProperty(root, "query") ?? "?"}";
        var provider = GetStringProperty(root, "provider");
        if (!string.IsNullOrEmpty(provider))
            header += $" · {provider}";
        // 落盘时内联数组只剩前几条，优先读 resultCount（完整条数）。
        var count = root.TryGetProperty("resultCount", out var countEl) && countEl.ValueKind == JsonValueKind.Number
            ? countEl.GetInt32().ToString(CultureInfo.InvariantCulture)
            : results.GetArrayLength().ToString(CultureInfo.InvariantCulture);
        return $"{header} · {count} 条";
    }

    /// <summary>通用回退：NormalizeForDisplay（缩进 JSON / 解码转义的纯文本）按行输出，行数有上限。</summary>
    private static IReadOnlyList<ToolDetailLine> FormatGeneric(string content)
    {
        var display = DisplayJsonSerializer.NormalizeForDisplay(content);
        var lines = display
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => new ToolDetailLine(line, null))
            .ToList();
        if (lines.Count > GenericMaxLines)
        {
            lines.RemoveRange(GenericMaxLines, lines.Count - GenericMaxLines);
            lines.Add(new ToolDetailLine($"… 还有更多行（完整结果已发送给模型）", TuiPalette.FgMuted));
        }
        return lines;
    }

    private static string? GetStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }

    private static string TruncateText(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
